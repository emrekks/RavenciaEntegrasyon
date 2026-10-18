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

export type LaneMetric = { lane: 'USER' | 'INITIAL' | 'HOT' | 'BACKGROUND'; attempts: number; averageWaitSeconds: number | null; p95WaitSeconds: number | null; queued: number; running: number }
export type QuotaUsage = { platformCode: string; storeName: string; lane: 'FOREGROUND' | 'BACKGROUND'; limitPerMinute: number; lastHourRequests: number; peakMinuteRequests: number; averagePerMinute: number }

export const laneLabels: Record<LaneMetric['lane'], string> = { USER: 'Kullanıcı İşlemleri', INITIAL: 'İlk Aktarım', HOT: 'Yeni Veri', BACKGROUND: 'Arka Plan' }
const quotaLaneLabels: Record<QuotaUsage['lane'], string> = { FOREGROUND: 'Yeni sipariş ve işlemler', BACKGROUND: 'Diğer okumalar' }

// How full a quota ran over the last hour: the average requests per minute against the limit.
export function quotaFullness(usage: QuotaUsage) {
  return usage.limitPerMinute <= 0 ? null : Math.min(1, usage.averagePerMinute / usage.limitPerMinute)
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
  const lanes = useQuery({
    queryKey: ['jobs', 'metrics', 'lanes', hours],
    queryFn: () => hubApi<LaneMetric[]>(`/jobs/metrics/lanes?hours=${hours}`),
    enabled: open,
    refetchInterval: open ? 30_000 : false
  })
  const quota = useQuery({
    queryKey: ['jobs', 'metrics', 'quota'],
    queryFn: () => hubApi<QuotaUsage[]>('/jobs/metrics/quota'),
    enabled: open,
    refetchInterval: open ? 60_000 : false
  })
  const rows = metrics.data ?? []
  return <details className="job-queue-metrics" open={open} onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>
      <span>Kuyruk Performansı</span>
      <small>Bekleme, çalışma süresi ve hata oranı · son {hours >= 168 ? '7 gün' : `${hours} saat`}</small>
    </summary>
    {open && (lanes.data?.length || quota.data?.length) ? <div className="job-queue-overview">
      {lanes.data?.length ? <section aria-label="Şeritler">
        <h3>Şeritler</h3>
        <div className="job-queue-lanes">{lanes.data.map(lane => <div key={lane.lane} className="job-queue-lane">
          <strong>{laneLabels[lane.lane]}</strong>
          <span>p95 bekleme <b>{formatMetricSeconds(lane.p95WaitSeconds)}</b></span>
          <small>Kuyrukta {lane.queued} · Çalışıyor {lane.running} · {lane.attempts.toLocaleString('tr-TR')} deneme</small>
        </div>)}</div>
      </section> : null}
      {quota.data?.length ? <section aria-label="Trendyol sipariş kotası">
        <h3>Trendyol Sipariş Kotası <small>son 1 saat</small></h3>
        <ul className="job-queue-quota">{quota.data.map(usage => {
          const fullness = quotaFullness(usage)
          return <li key={`${usage.storeName}-${usage.lane}`}>
            <span>{quotaLaneLabels[usage.lane]}<small>{usage.storeName}</small></span>
            <span className="job-queue-quota-bar" aria-hidden="true"><i style={{ width: `${Math.round((fullness ?? 0) * 100)}%` }} /></span>
            <span className={fullness != null && fullness >= 0.8 ? 'job-queue-metrics-bad' : undefined}>ort. {usage.averagePerMinute.toLocaleString('tr-TR')}/dk · tepe {usage.peakMinuteRequests}/dk · sınır {usage.limitPerMinute}/dk{fullness == null ? '' : ` (%${Math.round(fullness * 100)})`}</span>
          </li>
        })}</ul>
      </section> : null}
    </div> : null}
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
