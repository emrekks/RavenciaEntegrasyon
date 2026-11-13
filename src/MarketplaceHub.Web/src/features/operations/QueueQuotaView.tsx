import { cardCountText } from '../../shared/metric-value'
import { useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { jobTitle } from './job-presentation'
import { ChoiceField } from './filter-fields'
import {
  activeJobTypes, countsByStatus, formatSeconds, laneHint, laneLabel, laneTotals, queueWindowOptions, quotaLaneLabels, usagePercent, usageTone, windowSince,
  type JobCount, type JobTypeMetric, type LaneMetric, type QueueWindow, type QuotaUsage
} from './queue-quota'
import './queue-quota.css'

// İşlem Takibi › Kuyruk ve Kota: how full the worker's lanes are (running jobs against their slots, what waits for a
// slot), the jobs of the window by state and by type, and how much of each store's marketplace quota the last hour
// used. Read-only; polled every 30 seconds while open.
const pollMs = 30_000

function UsageBar({ used, limit, label }: { used: number; limit: number; label: string }) {
  const percent = usagePercent(used, limit)
  return <span className={`qq-bar is-${usageTone(percent)}`} role="meter" aria-valuemin={0} aria-valuemax={limit} aria-valuenow={used} aria-label={label}>
    <i style={{ width: `${percent}%` }} />
  </span>
}

export function QueueQuotaView() {
  const [hours, setHours] = useState<QueueWindow>('24')
  const since = useMemo(() => windowSince(hours), [hours])
  const options = { placeholderData: keepPreviousData, refetchInterval: pollMs } as const
  const lanes = useQuery({ queryKey: ['queue-quota', 'lanes', hours], queryFn: () => hubPoll<LaneMetric[]>(`/jobs/metrics/lanes?hours=${hours}`), ...options })
  const metrics = useQuery({ queryKey: ['queue-quota', 'metrics', hours], queryFn: () => hubPoll<JobTypeMetric[]>(`/jobs/metrics?hours=${hours}`), ...options })
  const quota = useQuery({ queryKey: ['queue-quota', 'quota'], queryFn: () => hubPoll<QuotaUsage[]>('/jobs/metrics/quota'), ...options })
  const counts = useQuery({ queryKey: ['queue-quota', 'counts', since], queryFn: () => hubPoll<JobCount[]>(`/jobs/counts?since=${encodeURIComponent(since)}`), ...options })
  const refresh = () => { void lanes.refetch(); void metrics.refetch(); void quota.refetch(); void counts.refetch() }
  const fetching = lanes.isFetching || metrics.isFetching || quota.isFetching || counts.isFetching

  const totals = lanes.data ? laneTotals(lanes.data) : null
  const types = metrics.data ? activeJobTypes(metrics.data) : []
  const states = counts.data ? countsByStatus(counts.data) : []
  const windowLabel = queueWindowOptions.find(option => option.value === hours)?.label ?? ''
  const number = (value: number | undefined) => cardCountText(value)

  return <div className="qq">
    <div className="jobs-reference-toolbar qq-toolbar">
      <div className="qq-window">
        <ChoiceField name="Ölçüm Aralığı" value={hours} options={queueWindowOptions} onChange={value => setHours(value as QueueWindow)} width={200} />
        <p className="qq-note">Çalışan ve bekleyen işler şu anı, deneme ve bekleme süreleri seçili aralığı gösterir.</p>
      </div>
      <div className="jobs-reference-toolbar-actions">
        <button type="button" className={`jobs-reference-refresh${fetching ? ' is-busy' : ''}`} title="Yenile" aria-label="Kuyruk ve kota ölçümlerini yenile" onClick={refresh}><UiIcon name="refresh" size={16} /></button>
      </div>
    </div>

    <div className="qq-cards" aria-label="Kuyruk özeti">
      <div className="qq-card"><span>Çalışan İş</span><strong>{number(totals?.running)}</strong><small>{totals ? `Slot doluluğu %${usagePercent(totals.running, totals.slots)}` : '—'}</small></div>
      <div className="qq-card"><span>Slot Bekleyen İş</span><strong>{number(totals?.queued)}</strong><small>Slot sırası bekliyor</small></div>
      <div className="qq-card"><span>Deneme</span><strong>{number(totals?.attempts)}</strong><small>{windowLabel}</small></div>
      <div className="qq-card"><span>Toplam Slot</span><strong>{number(totals?.slots)}</strong><small>{lanes.data ? `${lanes.data.length} şeritte toplam` : '—'}</small></div>
    </div>

    <section className="qq-section" aria-labelledby="qq-lanes">
      <h2 id="qq-lanes">Şeritler ve Slot Kullanımı</h2>
      {lanes.isLoading ? <Busy text="Şeritler yükleniyor…" /> : lanes.isError && !lanes.data ? <p role="alert" className="qq-error">Şerit ölçümleri alınamadı.</p> : <div className="qq-table-scroll"><table className="qq-table">
        <thead><tr><th className="qq-first">Şerit</th><th className="qq-usage-th">Slot Kullanımı</th><th className="is-num">Bekleyen</th><th className="is-num">Deneme</th><th className="is-num">Ort. Bekleme</th><th className="is-num">En Uzun %5 Bekleme</th></tr></thead>
        <tbody>{(lanes.data ?? []).map(lane => <tr key={lane.lane}>
          <td className="qq-first"><strong>{laneLabel(lane.lane)}</strong><small>{laneHint(lane.lane)}</small></td>
          <td className="qq-usage-cell" data-label="Slot Kullanımı"><span className="qq-usage"><UsageBar used={lane.running} limit={lane.slots} label={`${laneLabel(lane.lane)}: ${lane.running} / ${lane.slots} slot`} /><span>{lane.running} / {lane.slots}</span></span></td>
          <td className="is-num" data-label="Bekleyen">{lane.queued.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Deneme">{lane.attempts.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Ort. Bekleme">{formatSeconds(lane.averageWaitSeconds)}</td>
          <td className="is-num" data-label="En Uzun %5 Bekleme">{formatSeconds(lane.p95WaitSeconds)}</td>
        </tr>)}</tbody>
      </table></div>}
    </section>

    <div className="qq-split">
      <section className="qq-section" aria-labelledby="qq-states">
        <h2 id="qq-states">Duruma Göre İşler</h2>
        <p className="qq-sub">{windowLabel} içinde oluşturulan işler.</p>
        {counts.isLoading ? <Busy text="Sayılar yükleniyor…" /> : states.length === 0 ? <p className="qq-empty">Bu aralıkta iş yok.</p> : <ul className="qq-states">
          {states.map(state => <li key={state.status} className={`is-${state.status.toLowerCase()}`}><span>{state.label}</span><strong>{state.count.toLocaleString('tr-TR')}</strong></li>)}
        </ul>}
      </section>
      <section className="qq-section" aria-labelledby="qq-quota">
        <h2 id="qq-quota">Pazaryeri Kotası</h2>
        <p className="qq-sub">Son 1 saatte gönderilen istekler; en yoğun dakika, dakikalık sınırla karşılaştırılır. Şimdilik Trendyol sipariş okumaları ölçülüyor.</p>
        {quota.isLoading ? <Busy text="Kota yükleniyor…" /> : quota.isError && !quota.data ? <p role="alert" className="qq-error">Kota ölçümleri alınamadı.</p> : (quota.data ?? []).length === 0 ? <p className="qq-empty">Kotası ölçülen etkin bağlantı yok.</p> : <ul className="qq-quota">
          {(quota.data ?? []).map(row => <li key={`${row.storeName}:${row.lane}`}>
            <div className="qq-quota-head"><PlatformSquareMark code={row.platformCode} name={row.storeName} /><strong>{row.storeName}</strong><span className="qq-tag">{quotaLaneLabels[row.lane] ?? row.lane}</span></div>
            <div className="qq-usage"><UsageBar used={row.peakMinuteRequests} limit={row.limitPerMinute} label={`${row.storeName} ${quotaLaneLabels[row.lane] ?? row.lane}: en yoğun dakika ${row.peakMinuteRequests} / ${row.limitPerMinute}`} /><span>{row.peakMinuteRequests} / {row.limitPerMinute} dk</span></div>
            <small>Son 1 saat: {row.lastHourRequests.toLocaleString('tr-TR')} istek · Ortalama {row.averagePerMinute.toLocaleString('tr-TR')} / dk</small>
          </li>)}
        </ul>}
      </section>
    </div>

    <section className="qq-section" aria-labelledby="qq-types">
      <h2 id="qq-types">İş Türüne Göre Kuyruk</h2>
      {metrics.isLoading ? <Busy text="İş türleri yükleniyor…" /> : metrics.isError && !metrics.data ? <p role="alert" className="qq-error">İş türü ölçümleri alınamadı.</p> : types.length === 0 ? <p className="qq-empty">Bu aralıkta çalışan ya da bekleyen iş yok.</p> : <div className="qq-table-scroll"><table className="qq-table">
        <thead><tr><th className="qq-first">İş Türü</th><th className="is-num">Bekleyen</th><th className="is-num">Çalışan</th><th className="is-num">Deneme</th><th className="is-num">Hata</th><th className="is-num">Ort. Bekleme</th><th className="is-num">Ort. Süre</th></tr></thead>
        <tbody>{types.map(row => <tr key={row.jobType}>
          <td className="qq-first"><strong>{jobTitle(row.jobType)}</strong><small>{row.jobType}</small></td>
          <td className="is-num" data-label="Bekleyen">{row.queued.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Çalışan">{row.running.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Deneme">{row.attempts.toLocaleString('tr-TR')}</td>
          <td className={`is-num${row.failed > 0 ? ' is-error' : ''}`} data-label="Hata">{row.failed.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Ort. Bekleme">{formatSeconds(row.averageWaitSeconds)}</td>
          <td className="is-num" data-label="Ort. Süre">{formatSeconds(row.averageDurationSeconds)}</td>
        </tr>)}</tbody>
      </table></div>}
    </section>
  </div>
}

export default QueueQuotaView
