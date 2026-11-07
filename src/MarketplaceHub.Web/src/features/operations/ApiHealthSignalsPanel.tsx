import { useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { RrLogo } from '../marketplace/returns-ui'
import { signalCells, type ApiHealthSignals } from './api-health-signals'

const COLUMNS = ['Anahtar / Yetki', 'Geride Kalma', 'Hata Oranı (24 sa)', 'Hız Sınırı (429)', 'Kuyruk Yaşı', 'Webhook'] as const

// İşlem Takibi › API Sağlık › API Sağlığı: per connection the key, schedule lag, error rate, quota, queue age and webhook
// signals, read from stored data only. One value per cell; the detail is the tooltip.
export function ApiHealthSignalsPanel() {
  const signals = useQuery({ queryKey: ['api-health-signals'], queryFn: () => hubPoll<ApiHealthSignals>('/api-health-signals'), refetchInterval: 60_000, refetchIntervalInBackground: false })
  const rows = signals.data?.connections ?? []
  return <section className="rr-panel" aria-label="API Sağlığı"><div className="rr-panel-h"><div><h2>API Sağlığı</h2><p>Pazaryeri bağlantılarının anahtar, gecikme, hata ve kuyruk sinyalleri; yalnız kayıtlı verilerden</p></div></div>
    <div className="rr-mxwrap"><table className="rr-mxt rr-mxt-x rr-sigt"><thead><tr><th>Pazar Yeri</th>{COLUMNS.map(label => <th key={label}>{label}</th>)}</tr></thead><tbody>
      {!rows.length && <tr><td colSpan={COLUMNS.length + 1}><span className="rr-lead">{signals.isLoading ? 'Sinyaller yükleniyor…' : signals.isError ? 'Sinyaller okunamadı.' : 'Aktif pazar yeri bağlantısı bulunmuyor.'}</span></td></tr>}
      {rows.map(row => {
        const cells = signalCells(row)
        const entries = [cells.auth, cells.lag, cells.errorRate, cells.rateLimit, cells.queue, cells.webhook]
        return <tr key={row.connectionId}><td><span className="rr-mx-nm"><RrLogo code={row.platformCode} /><span><b>{row.displayName}</b></span></span></td>
          {entries.map((cell, index) => <td key={COLUMNS[index]} data-l={index === 5 ? 'Bildirim (Webhook)' : COLUMNS[index]}><span className={`rr-mx rr-${cell.tone === 'ok' ? 'ok' : cell.tone}`} title={cell.title}><i />{cell.text}</span></td>)}</tr>
      })}
    </tbody></table></div>
  </section>
}
