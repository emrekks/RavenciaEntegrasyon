import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { CardBackgroundIcon, type CardIconName, type CardTone } from '../../shared/components/CardBackgroundIcon'
import { RrIcon } from '../marketplace/returns-ui'
import { dashboardBootstrapQuery, healthLink, IntegrationStatusPanel, knownCount, useIntegrationHealth, type IntegrationHealth } from '../dashboard/IntegrationStatusPanel'
import { PriceStockMonitorPanel } from '../marketplace/integrations/PriceStockMonitorPanel'
import { priceStockMonitorQueryKey, type PriceStockMonitor } from '../marketplace/integrations/integration-helpers'
import type { IntegrationFeedback } from '../marketplace/shared'
import './api-health.css'

export type ApiHealthItem = { key: string; icon: CardIconName; label: string; value: string; tone: CardTone; to: string }

const MATRIX_LABELS: Record<string, string> = { orders: 'Sipariş', products: 'Ürün', stock: 'Stok', price: 'Fiyat', returns: 'İade', invoices: 'Fatura' }

/**
 * What the owner has to look at, worst first: stores with a credential, webhook or data-flow problem (as Entegrasyon
 * Durumu marks them), lost listings and failed price/stock pushes (as Fiyat ve Stok Gönderimi counts them), and the
 * 24-hour jobs that ended or wait for a decision. A count that is not known yet adds nothing; it is never read as zero.
 */
export function apiHealthItems(health: IntegrationHealth['health'], monitor: PriceStockMonitor | undefined, deadJobs: number | null, manualReviewJobs: number | null): ApiHealthItem[] {
  const items: ApiHealthItem[] = []
  for (const item of health) {
    if (!item.attention) continue
    const { row, auth, webhook } = item
    const cells = Object.entries(row.cells).filter(([, cell]) => cell.state === 'bad' || cell.state === 'warn')
    const bad = Boolean(auth) || webhook.tone === 'bad' || cells.some(([, cell]) => cell.state === 'bad')
    const value = auth?.title ?? row.issue ?? (webhook.tone === 'bad' || webhook.tone === 'warn' ? `Webhook ${webhook.text}` : cells.map(([key, cell]) => `${MATRIX_LABELS[key] ?? key} ${cell.state === 'bad' ? 'Hata Var' : 'Gecikiyor'}`).join(', '))
    items.push({ key: `matrix:${row.connectionId}`, icon: auth ? 'key' : 'connect', label: row.displayName, value, tone: bad ? 'bad' : 'warn', to: healthLink(item) ?? `/integrations/${row.connectionId}` })
  }
  for (const row of monitor?.connections ?? []) {
    const failed = row.failedPushes7d + row.manualReviewPushes
    if (failed) items.push({ key: `failed:${row.connectionId}`, icon: 'send', label: `${row.displayName} · Başarısız Fiyat-Stok Gönderimi`, value: failed.toLocaleString('tr-TR'), tone: 'bad', to: `/integrations/${row.connectionId}` })
    const lost = row.notFoundLinks + row.rejectedLinks
    if (lost) items.push({ key: `lost:${row.connectionId}`, icon: 'tag', label: `${row.displayName} · Kayıp İlan`, value: lost.toLocaleString('tr-TR'), tone: 'warn', to: `/integrations/${row.connectionId}` })
  }
  if (deadJobs) items.push({ key: 'dead', icon: 'jobs', label: 'Son 24 Saatte Tamamlanamayan İş', value: deadJobs.toLocaleString('tr-TR'), tone: 'bad', to: '/jobs?statu=DEAD' })
  if (manualReviewJobs) items.push({ key: 'review', icon: 'activity', label: 'Kararınızı Bekleyen İş', value: manualReviewJobs.toLocaleString('tr-TR'), tone: 'warn', to: '/jobs?statu=MANUAL_REVIEW&aralik=tum' })
  return items.sort((left, right) => Number(right.tone === 'bad') - Number(left.tone === 'bad'))
}

// İşlem Takibi › API Sağlık: what needs a look as Genel Bakış's operation cards, then Genel Bakış's own Entegrasyon
// Durumu panel, then the price and stock sending panel. Each part is the shared one, so the pages never drift apart.
export function ApiHealthView({ onFeedback }: { onFeedback: (feedback: IntegrationFeedback) => void }) {
  const integration = useIntegrationHealth()
  const bootstrap = useQuery(dashboardBootstrapQuery)
  const monitor = useQuery({ queryKey: priceStockMonitorQueryKey, queryFn: () => hubPoll<PriceStockMonitor>('/price-stock-monitor'), refetchInterval: 60_000, refetchIntervalInBackground: false })
  const metrics = bootstrap.data?.metrics
  const items = apiHealthItems(integration.health, monitor.data, knownCount(bootstrap.isLoading, metrics?.deadJobCount), knownCount(bootstrap.isLoading, metrics?.manualReviewJobCount))
  return <div className="api-health">
    <div className="rr rr-dashboard api-health-rr">
      {items.length > 0 && <section className="rr-dsect" aria-labelledby="api-health-todo"><header><h2 id="api-health-todo">Bakmanız Gerekenler</h2></header><div className="rr-opgrid">
        {items.map(item => <Link key={item.key} className={`rr-opcard rr-${item.tone}`} to={item.to}><CardBackgroundIcon name={item.icon} tone={item.tone} /><span className="rr-op-t"><small>{item.label}</small><strong className="rr-num" title={item.value}>{item.value}</strong></span><RrIcon name="arrowR" /></Link>)}
      </div></section>}
      <IntegrationStatusPanel integration={integration} />
    </div>
    <PriceStockMonitorPanel onFeedback={onFeedback} />
  </div>
}
