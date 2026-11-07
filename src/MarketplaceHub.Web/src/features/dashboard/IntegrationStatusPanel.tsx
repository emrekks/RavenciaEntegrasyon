import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { useQueries, useQuery } from '@tanstack/react-query'
import { backgroundPollHeaders, hubApi, loadAllPages } from '../../shared/api'
import { InvoiceGlyph } from '../invoicing/invoice-state-glyph'
import { RrLogo } from '../marketplace/returns-ui'
import type { Connection } from '../marketplace/shared'
import { connectionAuthSummary } from '../marketplace/integrations/connection-auth-alert'
import { instantNotificationsQueryKey, type InstantNotificationStatus } from '../marketplace/instant-notification-status'
import { webhookCell } from './integration-health'
import './dashboard-sade.css'

// Entegrasyon Durumu: one row per marketplace store with the last 7 days of each data flow, the webhook and the
// 24-hour job counts. Genel Bakış and İşlem Takibi › API Sağlık draw the same panel from the same queries.

export type DashboardMetrics = { pendingOrders: number; lateOrders: number; pendingReturns: number; pendingByConnection?: Record<string, number>; lateByConnection?: Record<string, number>; pendingReturnsByConnection?: Record<string, number>; pendingUnitsByConnection?: Record<string, number> | null; deadJobCount?: number; manualReviewJobCount?: number; recentJobCount?: number }
/** What API Sağlık adds to a store's row: the automatic price/stock write state, a problem count and the order-source note (all as tooltips). */
export type StatusExtras = {
  write: { label: string; tone: 'on' | 'partial' | 'off'; title: string }
  problem: { text: string; tone: 'warn' | 'bad'; title: string } | null
  sourceHint: string
  attention: boolean
}
export type MatrixCell = { state: 'ok' | 'warn' | 'bad' | 'off'; lastSuccessAt: string | null; lastProblemAt: string | null }
export type MatrixRow = { connectionId: string; platformCode: string; displayName: string; externalStoreId: string; status: string; cells: Record<string, MatrixCell>; issue: string | null }

// Only marketplaces: invoicing providers are connections too, but they carry no orders, stock or prices.
const MARKETPLACES = ['TRENDYOL', 'HEPSIBURADA', 'SHOPIFY', 'N11', 'PAZARAMA', 'PTTAVM']
const DATA_KEYS: Array<[string, string]> = [['orders', 'Sipariş'], ['products', 'Ürün'], ['stock', 'Stok'], ['price', 'Fiyat'], ['returns', 'İade'], ['invoices', 'Fatura']]
const CELL_STATE = { ok: 'Güncel', warn: 'Gecikiyor', bad: 'Hata var', off: 'Son 7 günde işlem yok' }
// Refreshed while the page is on screen only; the polls say they are automatic so they do not keep an idle session alive.
const refresh = { refetchInterval: 60_000, refetchIntervalInBackground: false, refetchOnWindowFocus: true, staleTime: 30_000 } as const
const poll = { headers: backgroundPollHeaders }

export const dashboardBootstrapQuery = { queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<{ metrics: DashboardMetrics; channels?: unknown[] }>('/dashboard/bootstrap', poll), ...refresh }

export function age(timestamp: string | null) {
  if (!timestamp) return null
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(timestamp).getTime()) / 60_000))
  if (minutes < 1) return 'Az önce'
  if (minutes < 60) return `${minutes} dk önce`
  const hours = Math.floor(minutes / 60)
  return hours < 48 ? `${hours} sa önce` : `${Math.floor(hours / 24)} gün önce`
}

// What a matrix cell says: its own state first, the time of the last success after it.
export function matrixCellText(cell: MatrixCell) {
  const when = age(cell.lastSuccessAt)
  if (cell.state === 'off') return 'İşlem Yok'
  if (cell.state === 'bad') return 'Hata Var'
  // A late flow shows only its time; the amber dot and the hover title say it is late ("Gecikiyor · 2 gün önce" did not fit a phone cell).
  if (cell.state === 'warn') return when || 'Gecikiyor'
  return when ?? 'Güncel'
}

// A count the page could not read (still loading, the request failed, the field is missing) is unknown and shows "—":
// a wrong number on screen is worse than a missing one, so it never falls back to 0.
export function knownCount(loading: boolean, value: number | null | undefined) {
  return loading || value == null ? null : value
}

// The integration matrix covers the last 7 days of one store: its "İncele" opens İşlem Takibi's failed jobs over the
// same 7 days, filtered to that marketplace.
export function matrixJobsLink(platformCode: string) {
  return `/jobs?${new URLSearchParams({ durum: 'hata', aralik: '7g', platform: platformCode.toLowerCase() })}`
}

export const storeLabel = (row: MatrixRow) => /^\d+$/.test(row.externalStoreId) ? `Mağaza ${row.externalStoreId}` : row.externalStoreId.length > 24 ? `Mağaza ${row.externalStoreId.slice(0, 8)}…` : row.externalStoreId

/** The matrix rows with each store's credentials and webhook as the integration page shows them, and whether it needs a look. */
export function useIntegrationHealth() {
  const matrix = useQuery({ queryKey: ['dashboard', 'integration-matrix'], queryFn: () => hubApi<MatrixRow[]>('/dashboard/integration-matrix', poll), ...refresh, refetchInterval: 300_000, staleTime: 120_000 })
  const rows = (matrix.data ?? []).filter(row => MARKETPLACES.includes(row.platformCode.toUpperCase()))
  // The webhook status of a Trendyol or Shopify store asks the marketplace, so it is read again only every five minutes.
  const connections = useQuery({ queryKey: ['connections'], queryFn: () => loadAllPages<Connection>('/connections'), staleTime: 120_000 })
  const webhooks = useQueries({ queries: rows.map(row => ({ queryKey: instantNotificationsQueryKey(row.connectionId), queryFn: () => hubApi<InstantNotificationStatus>(`/connections/${row.connectionId}/instant-notifications`, poll), staleTime: 300_000, refetchInterval: 300_000, refetchIntervalInBackground: false })) })
  const health = rows.map((row, index) => {
    const connection = connections.data?.items.find(item => item.id === row.connectionId)
    const auth = connection ? connectionAuthSummary(connection) : null
    const webhook = webhookCell(webhooks[index]?.data, Boolean(webhooks[index]?.isError), age, row.platformCode)
    const attention = Boolean(row.issue || auth || webhook.tone === 'bad' || webhook.tone === 'warn' || Object.values(row.cells).some(cell => cell.state === 'bad' || cell.state === 'warn'))
    return { row, auth, webhook, attention }
  })
  return { matrix, rows, health, attention: health.filter(item => item.attention).length }
}

export type IntegrationHealth = ReturnType<typeof useIntegrationHealth>

/** Where a store's "İncele" goes: its integration page for credentials or webhook trouble, its failed jobs otherwise. */
export function healthLink({ row, auth, webhook }: IntegrationHealth['health'][number]) {
  if (auth || (!row.issue && (webhook.tone === 'bad' || webhook.tone === 'warn'))) return `/integrations/${row.connectionId}`
  return row.issue || Object.values(row.cells).some(cell => cell.state === 'bad' || cell.state === 'warn') ? matrixJobsLink(row.platformCode) : null
}

export function IntegrationStatusPanel({ integration, extras, headerAction, notice }: { integration?: IntegrationHealth; extras?: Record<string, StatusExtras>; headerAction?: ReactNode; notice?: string | null }) {
  const own = useIntegrationHealth()
  const { matrix, rows, health, attention } = integration ?? own
  const bootstrap = useQuery(dashboardBootstrapQuery)
  const metrics = bootstrap.data?.metrics
  const loadingValue = (value: number | undefined) => { const known = knownCount(bootstrap.isLoading, value); return known === null ? '—' : known.toLocaleString('tr-TR') }
  return <section className="rr-panel"><div className="rr-panel-h"><div><h2>Entegrasyon Durumu</h2><p>Pazaryerleriyle bağlantının son 7 günü</p></div>{matrix.isLoading ? <span className="rr-hpill">Kontrol ediliyor…</span> : <span className={`rr-hpill${attention ? ' rr-attn' : ''}`}><InvoiceGlyph kind={attention ? 'waiting' : 'done'} />{attention ? `${attention} konu dikkatinizi bekliyor` : 'Her şey yolunda'}</span>}{headerAction}</div>
    {notice ? <p className="rr-mx-notice" role="status">{notice}</p> : null}
    <div className="rr-mxwrap"><table className={`rr-mxt${extras ? ' rr-mxt-x' : ''}`}><thead><tr><th>Pazar Yeri</th>{DATA_KEYS.map(([, label]) => <th key={label}>{label}</th>)}<th>Webhook</th>{extras ? <><th>Otomatik Yazma</th><th>Sorun</th></> : null}<th /></tr></thead><tbody>
      {!rows.length && <tr><td colSpan={DATA_KEYS.length + 3 + (extras ? 2 : 0)}><span className="rr-lead">{matrix.isLoading ? 'Bağlantılar yükleniyor…' : 'Aktif pazar yeri bağlantısı bulunmuyor.'}</span></td></tr>}
      {health.map(item => { const { row, auth, webhook, attention: rowAttention } = item; const extra = extras?.[row.connectionId]; const link = auth || row.issue || webhook.tone === 'bad' || webhook.tone === 'warn' ? healthLink(item) : null; return <tr key={row.connectionId} className={rowAttention || extra?.attention ? 'rr-attn' : undefined}><td><span className="rr-mx-nm"><RrLogo code={row.platformCode} /><span><b>{row.displayName}</b><small className={auth || row.issue ? 'rr-attn' : undefined} title={auth?.action ?? row.issue ?? undefined}>{auth?.title ?? row.issue ?? storeLabel(row)}</small></span></span></td>
        {DATA_KEYS.map(([key, label]) => { const cell = row.cells[key] ?? { state: 'off', lastSuccessAt: null, lastProblemAt: null }; return <td key={key} data-l={label}><span className={`rr-mx rr-${cell.state}`} title={`${label} · ${CELL_STATE[cell.state]}`}><i />{matrixCellText(cell)}</span></td> })}
        <td data-l="Webhook"><span className={`rr-mx rr-${webhook.tone}`} title={extra ? `${webhook.title}\n${extra.sourceHint}` : webhook.title}><i />{webhook.text}</span></td>
        {extras ? <><td data-l="Otomatik Yazma">{extra ? <span className={`psm-chip is-write-${extra.write.tone}`} title={extra.write.title}><i aria-hidden="true" />{extra.write.label}</span> : <span className="rr-mx rr-off">—</span>}</td>
          <td data-l="Sorun">{extra?.problem ? <span className={`rr-mx rr-${extra.problem.tone}`} title={extra.problem.title}><i />{extra.problem.text}</span> : null}</td></> : null}
        <td className="rr-mx-act">{link && <Link className="rr-btn rr-link" to={link}>İncele</Link>}</td></tr> })}
    </tbody></table></div>
    <div className="rr-mx-foot"><span>Son 24 saatte <b className="rr-num">{loadingValue(metrics?.recentJobCount)}</b> istek · Açıkta kalan: <Link className="rr-mx-foot-link" to="/jobs?statu=DEAD&aralik=tum" title="Deneme hakkı dolan işler; İşlem Takibi’nde tüm zamanlarla açılır"><b className="rr-num">{loadingValue(metrics?.deadJobCount)}</b> tamamlanamayan</Link> · <Link className="rr-mx-foot-link" to="/jobs?statu=MANUAL_REVIEW&aralik=tum" title="Kararınızı bekleyen işler; İşlem Takibi’nde tüm zamanlarla açılır"><b className="rr-num">{loadingValue(metrics?.manualReviewJobCount)}</b> karar bekleyen iş</Link></span><span className="rr-legend"><span><i className="rr-mx-l rr-ok" />Güncel</span><span><i className="rr-mx-l rr-warn" />Gecikiyor</span><span><i className="rr-mx-l rr-bad" />Hata Var</span><span><i className="rr-mx-l rr-off" />İşlem Yok</span></span></div>
  </section>
}
