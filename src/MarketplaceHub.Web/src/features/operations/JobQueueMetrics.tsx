import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import './job-queue-metrics.css'

export type JobTypeMetric = {
  jobType: string
  attempts: number
  failed: number
  averageWaitSeconds: number | null
  p95WaitSeconds: number | null
  averageDurationSeconds: number | null
  p95DurationSeconds: number | null
  queued: number
  running: number
}

export function formatMetricSeconds(seconds: number | null) {
  if (seconds == null) return '—'
  if (seconds < 1) return '<1 sn'
  if (seconds < 90) return `${Math.round(seconds)} sn`
  if (seconds < 5400) return `${(seconds / 60).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} dk`
  return `${(seconds / 3600).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sa`
}

export function failureRate(metric: JobTypeMetric) {
  return metric.attempts === 0 ? null : metric.failed / metric.attempts
}

// Queue health per job type: how long jobs wait before they start, how long they run and how
// often they fail. Loaded only while the panel is open.
export function JobQueueMetrics({ hours, titleFor }: { hours: number; titleFor: (jobType: string) => string }) {
  const [open, setOpen] = useState(false)
  const metrics = useQuery({
    queryKey: ['jobs', 'metrics', hours],
    queryFn: () => hubApi<JobTypeMetric[]>(`/jobs/metrics?hours=${hours}`),
    enabled: open,
    refetchInterval: open ? 30_000 : false
  })
  const rows = metrics.data ?? []
  return <details className="job-queue-metrics" open={open} onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>
      <span>Kuyruk Performansı</span>
      <small>Bekleme, çalışma süresi ve hata oranı · son {hours >= 168 ? '7 gün' : `${hours} saat`}</small>
    </summary>
    {open && (metrics.isLoading
      ? <p className="job-queue-metrics-state">Metrikler yükleniyor…</p>
      : metrics.isError
        ? <p role="alert" className="job-queue-metrics-state job-queue-metrics-error">Metrikler alınamadı.</p>
        : rows.length === 0
          ? <p className="job-queue-metrics-state">Bu aralıkta çalışan işlem yok.</p>
          : <div className="job-queue-metrics-scroll"><table>
            <thead><tr><th>İşlem Türü</th><th>Kuyrukta</th><th>Çalışıyor</th><th>Deneme</th><th>Hata Oranı</th><th>Bekleme (ort. / p95)</th><th>Süre (ort. / p95)</th></tr></thead>
            <tbody>{rows.map(row => {
              const rate = failureRate(row)
              return <tr key={row.jobType}>
                <td><strong>{titleFor(row.jobType)}</strong><small>{row.jobType}</small></td>
                <td className={row.queued > 0 ? 'job-queue-metrics-warn' : undefined}>{row.queued}</td>
                <td>{row.running}</td>
                <td>{row.attempts.toLocaleString('tr-TR')}</td>
                <td className={rate != null && rate >= 0.1 ? 'job-queue-metrics-bad' : undefined}>{rate == null ? '—' : `%${(rate * 100).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`}</td>
                <td>{formatMetricSeconds(row.averageWaitSeconds)} / {formatMetricSeconds(row.p95WaitSeconds)}</td>
                <td>{formatMetricSeconds(row.averageDurationSeconds)} / {formatMetricSeconds(row.p95DurationSeconds)}</td>
              </tr>
            })}</tbody>
          </table></div>)}
  </details>
}
