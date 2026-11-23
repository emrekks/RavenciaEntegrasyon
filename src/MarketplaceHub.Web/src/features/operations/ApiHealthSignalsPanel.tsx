import { lazy, Suspense, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { RrLogo, RrModal } from '../../shared/ui/rr-components'
import { signalCells, signalDetail, type ApiHealthSignals, type SignalKind } from './api-health-signals'
import { queryKeys } from '../../shared/api/query-keys'
// The attempts window loads when a Hata Oranı or Hız Sınırı cell is opened.
const ApiHealthAttemptsDialog = lazy(() => import('./MonitorDetailDialogs').then(module => ({ default: module.ApiHealthAttemptsDialog })))

const COLUMNS = ['Anahtar / Yetki', 'Geride Kalma', 'Hata Oranı (24 sa)', 'Hız Sınırı (429)', 'Kuyruk Yaşı', 'Webhook'] as const
const KINDS: SignalKind[] = ['auth', 'lag', 'errorRate', 'rateLimit', 'queue', 'webhook']

// İşlem Takibi › API Sağlık › API Sağlığı: per connection the key, schedule lag, error rate, quota, queue age and webhook
// signals, read from stored data only. One value per cell; the detail is the tooltip.
export function ApiHealthSignalsPanel({ onOpenJob }: { onOpenJob?: (jobId: string) => void }) {
  const signals = useQuery({ queryKey: queryKeys.apiHealthSignals(), queryFn: ({ signal }) => hubPoll<ApiHealthSignals>('/api-health-signals', { signal }), refetchInterval: 60_000, refetchIntervalInBackground: false })
  const rows = signals.data?.connections ?? []
  // A warning or fault cell is a button that opens its detail; green and empty cells stay plain.
  const [open, setOpen] = useState<{ connectionId: string; kind: SignalKind } | null>(null)
  const openRow = open ? rows.find(row => row.connectionId === open.connectionId) : undefined
  // Hata Oranı and Hız Sınırı open the attempts behind their numbers; the other signals keep their fact window.
  const attempts = open && openRow && (open.kind === 'errorRate' || open.kind === 'rateLimit') ? { kind: open.kind, row: openRow } : null
  const detail = open && openRow && !attempts ? signalDetail(open.kind, openRow) : null
  return <section className="rr-panel" aria-label="API Sağlığı"><div className="rr-panel-h"><div><h2>API Sağlığı</h2><p>Pazaryeri bağlantılarının anahtar, gecikme, hata ve kuyruk sinyalleri; yalnız kayıtlı verilerden</p></div></div>
    <div className="rr-mxwrap"><table className="rr-mxt rr-mxt-x rr-sigt"><thead><tr><th>Pazar Yeri</th>{COLUMNS.map(label => <th key={label}>{label}</th>)}</tr></thead><tbody>
      {!rows.length && <tr><td colSpan={COLUMNS.length + 1}><span className="rr-lead">{signals.isLoading ? 'Sinyaller yükleniyor…' : signals.isError ? 'Sinyaller okunamadı.' : 'Aktif pazar yeri bağlantısı bulunmuyor.'}</span></td></tr>}
      {rows.map(row => {
        const cells = signalCells(row)
        const entries = [cells.auth, cells.lag, cells.errorRate, cells.rateLimit, cells.queue, cells.webhook]
        return <tr key={row.connectionId}><td><span className="rr-mx-nm"><RrLogo code={row.platformCode} /><span><b>{row.displayName}</b></span></span></td>
          {entries.map((cell, index) => <td key={COLUMNS[index]} data-l={index === 5 ? 'Bildirim (Webhook)' : COLUMNS[index]}>{cell.tone === 'warn' || cell.tone === 'bad'
            ? <button type="button" className={`rr-mx rr-${cell.tone} rr-mx-open`} title={cell.title} aria-haspopup="dialog" onClick={() => setOpen({ connectionId: row.connectionId, kind: KINDS[index] })}><i />{cell.text}</button>
            : <span className={`rr-mx rr-${cell.tone === 'ok' ? 'ok' : cell.tone}`} title={cell.title}><i />{cell.text}</span>}</td>)}</tr>
      })}
    </tbody></table></div>
    {attempts && <Suspense fallback={null}><ApiHealthAttemptsDialog key={`${attempts.row.connectionId}-${attempts.kind}`} connectionId={attempts.row.connectionId} displayName={attempts.row.displayName} kind={attempts.kind} onClose={() => setOpen(null)} onOpenJob={onOpenJob} /></Suspense>}
    {detail && <RrModal crumb="API Sağlığı" title={detail.title} size="sm" onClose={() => setOpen(null)} foot={<button type="button" className="rr-btn" onClick={() => setOpen(null)}>Kapat</button>}>
      {detail.lead && <p className="rr-lead">{detail.lead}</p>}
      <dl className="rr-kv">{detail.facts.map(([label, value]) => <Fact key={label} label={label} value={value} />)}</dl>
    </RrModal>}
  </section>
}

function Fact({ label, value }: { label: string; value: string }) {
  return <><dt>{label}</dt><dd>{value}</dd></>
}
