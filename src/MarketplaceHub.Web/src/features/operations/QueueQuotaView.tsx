import type { ApiAnswer } from '../../shared/api/api-answer'
import { cardCountText } from '../../shared/metric-value'
import { lazy, Suspense, useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { UiIcon } from '../../shared/components'
import { Tabs } from '../../shared/components/DesignSystem'
import { Busy } from '../../shared/components/AsyncState'
import { FieldHelp } from '../../shared/components/FieldHelp'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { jobTitle } from './job-presentation'
import { formatDateTime } from '../../shared/format-date'
import { ChoiceField } from './filter-fields'
import {
  activeJobTypes, countsByStatus, filterByMarketplace, filterJobsByTab, generalJobTab, isDispatcherModel, jobLaneText, jobPlatformTabs, formatSeconds, laneCountsFor, laneDetail, laneHint, lanePlatformTabs, laneSlotKind, laneWaitCauseLines, laneWaitState, laneLabel, laneTotals,
  latencySeconds, latencySourceLabel, latencyState, marketplaceTabs, queueWindowOptions, quotaPeakText, usagePercent, usageTone, windowSince,
  type QueueWindow
} from './queue-quota'
import { platformDisplayName } from '../../shared/platform-names'
import './queue-quota.css'
import { EmptyState } from '../../shared/components/EmptyState'
import { queryKeys } from '../../shared/api/query-keys'

// İşlem Takibi › Kuyruk ve Kota: how full the worker's lanes are (running jobs against their slots, what waits for a
// slot), the jobs of the window by state and by type, and how much of each store's marketplace quota the last hour
// used. Read-only; polled every 30 seconds while open.
const pollMs = 30_000
// The Hata window loads on its first open (its own chunk), so the view stays light.
const QueueFailuresDialog = lazy(() => import('./QueueFailuresDialog').then(module => ({ default: module.QueueFailuresDialog })))

function UsageBar({ used, limit, label }: { used: number; limit: number; label: string }) {
  const percent = usagePercent(used, limit)
  return <span className={`qq-bar is-${usageTone(percent)}`} role="meter" aria-valuemin={0} aria-valuemax={limit} aria-valuenow={used} aria-label={label}>
    <i style={{ width: `${percent}%` }} />
  </span>
}

// The seconds of a measured flow; no orders, "Veri Yok" (never 0).
function seconds(value: number | null | undefined, orders: number) {
  return orders === 0 ? 'Veri Yok' : latencySeconds(value)
}

// "32 sn / 1,5 dk": the median and the slowest 5 % of a split part of the order flow.
function pair(p50: number | null | undefined, p95: number | null | undefined) {
  return p50 === null || p50 === undefined ? '—' : `${latencySeconds(p50)} / ${latencySeconds(p95)}`
}

export function QueueQuotaView({ onOpenJob }: { onOpenJob?: (jobId: string) => void } = {}) {
  const [hours, setHours] = useState<QueueWindow>('24')
  const [platformTab, setPlatformTab] = useState('ALL')
  const [quotaTab, setQuotaTab] = useState('ALL')
  const [laneTab, setLaneTab] = useState('ALL')
  const since = useMemo(() => windowSince(hours), [hours])
  const options = { placeholderData: keepPreviousData, refetchInterval: pollMs } as const
  const lanes = useQuery({ queryKey: queryKeys.queueQuota('lanes', hours), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getJobsMetricsLanes'>>(`/jobs/metrics/lanes?hours=${hours}`, { signal }), ...options })
  const metrics = useQuery({ queryKey: queryKeys.queueQuota('metrics', hours), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getJobsMetrics'>>(`/jobs/metrics?hours=${hours}`, { signal }), ...options })
  const quota = useQuery({ queryKey: queryKeys.queueQuota('quota'), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getJobsMetricsQuota'>>('/jobs/metrics/quota', { signal }), ...options })
  const latency = useQuery({ queryKey: queryKeys.queueQuota('order-latency'), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getJobsMetricsOrderLatency'>>('/jobs/metrics/order-latency', { signal }), ...options })
  const counts = useQuery({ queryKey: queryKeys.queueQuota('counts', since), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getJobsCounts'>>(`/jobs/counts?dateFrom=${encodeURIComponent(since)}`, { signal }), ...options })
  const refresh = () => { void lanes.refetch(); void metrics.refetch(); void quota.refetch(); void latency.refetch(); void counts.refetch() }
  const fetching = lanes.isFetching || metrics.isFetching || quota.isFetching || latency.isFetching || counts.isFetching
  const quotaRows = quota.data ?? []
  const quotaTabs = marketplaceTabs(quotaRows)
  const activeQuotaTab = quotaTabs.some(tab => tab.value === quotaTab) ? quotaTab : 'ALL'
  const shownQuota = filterByMarketplace(quotaRows, activeQuotaTab)

  const totals = lanes.data ? laneTotals(lanes.data) : null
  const dispatcher = isDispatcherModel(lanes.data)
  const laneTabs = lanePlatformTabs(lanes.data ?? [])
  const activeLaneTab = laneTabs.some(tab => tab.value === laneTab) ? laneTab : 'ALL'
  const types = metrics.data ? activeJobTypes(metrics.data) : []
  const platformTabs = jobPlatformTabs(types)
  const activeTab = platformTabs.some(tab => tab.value === platformTab) ? platformTab : 'ALL'
  const shownTypes = filterJobsByTab(types, activeTab)
  const states = counts.data ? countsByStatus(counts.data) : []
  const windowLabel = queueWindowOptions.find(option => option.value === hours)?.label ?? ''
  const number = (value: number | undefined) => cardCountText(value)
  // The Hata window reads the attempts now; the table is read again with it, so the number and the window's total agree.
  const [failuresOf, setFailuresOf] = useState<string | null>(null)
  const openFailures = (jobType: string) => { setFailuresOf(jobType); void metrics.refetch() }

  return <div className="qq">
    <div className="jobs-reference-toolbar qq-toolbar">
      <div className="rr qq-window">
        <ChoiceField name="Ölçüm Aralığı" value={hours} options={queueWindowOptions} onChange={value => setHours(value as QueueWindow)} width={200} />
        <FieldHelp label="Ölçüm Aralığı">Çalışan ve bekleyen işler şu anı, deneme ve bekleme süreleri seçili aralığı gösterir.</FieldHelp>
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
      {dispatcher && <p className="qq-sub">Tek dağıtıcı boşalan slotu sıradaki işe verir. Kullanıcı, Webhook ve Pazaryerine Yazma’nın ayrılmış, Sipariş Okuma ve Durum Taraması’nın asgari slotları başka şeride verilmez; diğer şeritler ortak slotları payları kadar önce alır, paydan artanı önce kısa işler, sonra toplu ve uzun işler kullanır. Bir mağazanın Katalog, Arka Plan ve İlk Çekim işleri birlikte sayılır ve aynı anda en çok 2 slot kullanır; sipariş, durum, iade, webhook, yazma ve kullanıcı işleri bu sınıra girmez.</p>}
      {lanes.isLoading ? <Busy text="Şeritler yükleniyor…" /> : lanes.isError && !lanes.data ? <p role="alert" className="qq-error">Şerit ölçümleri alınamadı.</p> : <>
        {laneTabs.length > 1 && <Tabs className="rv-view-tabs qq-platform-tabs" ariaLabel="Şerit pazaryeri" value={activeLaneTab} onChange={setLaneTab} items={laneTabs.map(tab => ({
          value: tab.value, count: tab.count,
          label: tab.value === 'ALL' ? tab.label : <><PlatformSquareMark code={tab.value} name={tab.label} size="sm" />{tab.label}</>
        }))} />}
        {activeLaneTab !== 'ALL' && <p className="qq-sub">Slot kullanımı ve bekleyen yalnız {platformDisplayName(activeLaneTab)} işleri; deneme ve bekleme süreleri şeridin tümü içindir.</p>}
        <div className="qq-table-scroll"><table className="qq-table">
        <thead><tr><th className="qq-first">Şerit</th><th className="qq-usage-th">Slot Kullanımı</th><th className="is-num">Bekleyen</th><th className="is-num">Deneme</th><th className="is-num">Ort. Bekleme</th><th className="is-num">En Uzun %5 Bekleme</th><th className="is-num">Hedef</th><th>Durum</th></tr></thead>
        <tbody>{(lanes.data ?? []).map(lane => { const shown = laneCountsFor(lane, activeLaneTab); const kind = laneSlotKind(lane); return <tr key={lane.lane}>
          <td className="qq-first"><strong>{laneLabel(lane.lane)}</strong><small title={laneDetail(lane.lane)}>{laneHint(lane.lane)}</small></td>
          <td className="qq-usage-cell" data-label="Slot Kullanımı"><span className="qq-usage"><UsageBar used={shown.running} limit={lane.slots} label={`${laneLabel(lane.lane)}: ${shown.running} / ${lane.slots} slot`} /><span>{shown.running} / {lane.slots}</span>{kind && <small className={kind.stacked ? 'qq-no-limit is-stacked' : 'qq-no-limit'} title={kind.title}>{kind.text}</small>}</span></td>
          <td className="is-num" data-label="Bekleyen">{shown.queued.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Deneme">{lane.attempts.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Ort. Bekleme">{(() => { const causes = laneWaitCauseLines(lane.waitCauses); return causes.length > 0 ? <span className="qq-causes" title={causes.join('\n')}>{formatSeconds(lane.averageWaitSeconds)}</span> : formatSeconds(lane.averageWaitSeconds) })()}</td>
          <td className="is-num" data-label="En Uzun %5 Bekleme">{formatSeconds(lane.p95WaitSeconds)}</td>
          <td className="is-num" data-label="Hedef">{formatSeconds(lane.targetSeconds)}</td>
          <td data-label="Durum">{(() => { const state = laneWaitState(lane.waitStatus); return <span className={`qq-wait is-${state.tone}`} title={[`Son 5 dk en uzun %5 bekleme: ${formatSeconds(lane.recentP95WaitSeconds)}`, `Sıradaki en eski iş: ${formatSeconds(lane.oldestWaitSeconds)}`, lane.alertSince ? `Uyarı ${formatDateTime(lane.alertSince)} tarihinden beri açık` : ''].filter(Boolean).join('\n')}><i aria-hidden="true" />{state.label}</span> })()}</td>
        </tr> })}</tbody>
      </table></div></>}
    </section>

    <section className="qq-section" aria-labelledby="qq-latency">
      <h2 id="qq-latency">Siparişin Panele Ulaşma Süresi</h2>
      <p className="qq-sub">Son 24 saatte panele gelen yeni siparişler: pazaryerindeki sipariş anından panele kaydına kadar. Webhook ile gelenlerde süre, pazaryerinin bildirimi göndermesi ve panelin işlemesi olarak ayrılır.</p>
      {latency.isLoading ? <Busy text="Sipariş süreleri yükleniyor…" /> : latency.isError && !latency.data ? <p role="alert" className="qq-error">Sipariş süreleri alınamadı.</p> : (latency.data ?? []).length === 0 ? <EmptyState icon="connect" title="Veri Yok">Pazaryeri bağlantısı yok.</EmptyState> : <div className="qq-table-scroll"><table className="qq-table">
        <thead><tr><th className="qq-first">Pazaryeri</th><th className="is-num">Sipariş</th><th className="is-num">Medyan</th><th className="is-num">En Uzun %5</th><th className="is-num" title="Sipariş anından webhook'un panele ulaşmasına kadar (medyan / en uzun %5)">Pazaryeri Bildirimi</th><th className="is-num" title="Webhook'un ulaşmasından siparişin panelde görünmesine kadar (medyan / en uzun %5)">Panelde İşleme</th><th className="is-num">Hedef</th><th>Durum</th></tr></thead>
        <tbody>{(latency.data ?? []).map(row => <tr key={`${row.platformCode}:${row.source}`} className={row.source === 'ALL' ? 'qq-latency-all' : 'qq-latency-part'}>
          <td className="qq-first"><span className="qq-mark-line"><PlatformSquareMark code={row.platformCode} name={platformDisplayName(row.platformCode)} size="sm" /><strong>{platformDisplayName(row.platformCode)}</strong></span><small>{latencySourceLabel(row.source)}</small></td>
          <td className="is-num" data-label="Sipariş">{row.orders.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Medyan">{seconds(row.p50Seconds, row.orders)}</td>
          <td className="is-num" data-label="En Uzun %5">{seconds(row.p95Seconds, row.orders)}</td>
          <td className="is-num" data-label="Pazaryeri Bildirimi">{pair(row.noticeP50Seconds, row.noticeP95Seconds)}</td>
          <td className="is-num" data-label="Panelde İşleme">{pair(row.processingP50Seconds, row.processingP95Seconds)}</td>
          <td className="is-num" data-label="Hedef">{row.targetSeconds === null || row.targetSeconds === undefined ? '—' : `${formatSeconds(row.targetSeconds)} (${row.source === 'WEBHOOK' ? 'işleme' : 'toplam'})`}</td>
          <td data-label="Durum">{(() => { const state = latencyState(row.status); return state ? <span className={`qq-wait is-${state.tone}`}><i aria-hidden="true" />{state.label}</span> : '—' })()}</td>
        </tr>)}</tbody>
      </table></div>}
    </section>

    <section className="qq-section" aria-labelledby="qq-quota">
      <h2 id="qq-quota">Pazaryeri Kotası</h2>
      <p className="qq-sub">Son 1 saatte her kotaya giden istekler: en yoğun dakika, kodun dakikalık sınırıyla karşılaştırılır. Sınırı tutulmayan kotada yalnız kullanım gösterilir.</p>
      {quota.isLoading ? <Busy text="Kota yükleniyor…" /> : quota.isError && !quota.data ? <p role="alert" className="qq-error">Kota ölçümleri alınamadı.</p> : quotaRows.length === 0 ? <EmptyState icon="connect" title="Veri Yok">Son 30 günde kota kullanan bağlantı yok.</EmptyState> : <>
        <Tabs className="rv-view-tabs qq-platform-tabs" ariaLabel="Kota pazaryeri" value={activeQuotaTab} onChange={setQuotaTab} items={quotaTabs.map(tab => ({
          value: tab.value, count: tab.count,
          label: tab.value === 'ALL' ? tab.label : <><PlatformSquareMark code={tab.value} name={tab.label} size="sm" />{tab.label}</>
        }))} />
        <div className="qq-table-scroll"><table className="qq-table">
          <thead><tr><th className="qq-first">Kota</th><th className="qq-usage-th">En Yoğun Dakika</th><th className="is-num">Son 1 Saat</th><th className="is-num">Ortalama</th></tr></thead>
          <tbody>{shownQuota.map(row => <tr key={`${row.bucket}:${row.storeName}`}>
            <td className="qq-first"><span className="qq-mark-line"><PlatformSquareMark code={row.platformCode} name={platformDisplayName(row.platformCode)} size="sm" /><strong>{row.label}</strong></span><small>{row.shared ? 'Tüm hesaplar ortak' : row.storeName}</small></td>
            <td className="qq-usage-cell" data-label="En Yoğun Dakika"><span className="qq-usage">{row.limitPerMinute ? <UsageBar used={row.peakMinuteRequests} limit={row.limitPerMinute} label={`${row.label}: en yoğun dakika ${quotaPeakText(row)}`} /> : null}<span>{quotaPeakText(row)}</span>{row.limitPerMinute ? (row.baseLimit ? <small className="qq-no-limit" title="Kodun başladığı sınır. İşçi, Trendyol’un yanıtlarından daha yüksek bir kademe öğrenebilir; panel o kademeyi bilmez.">Taban Sınır</small> : null) : <small className="qq-no-limit">Sınır tutulmuyor</small>}</span></td>
            <td className="is-num" data-label="Son 1 Saat">{row.lastHourRequests.toLocaleString('tr-TR')} istek</td>
            <td className="is-num" data-label="Ortalama">{row.averagePerMinute.toLocaleString('tr-TR')} / dk</td>
          </tr>)}</tbody>
        </table></div></>}
    </section>

    <section className="qq-section" aria-labelledby="qq-states">
      <h2 id="qq-states">Duruma Göre İşler</h2>
      <p className="qq-sub">{windowLabel} içinde oluşturulan işler.</p>
      {counts.isLoading ? <Busy text="Sayılar yükleniyor…" /> : states.length === 0 ? <EmptyState icon="jobs" title="İş Yok">Bu aralıkta iş yok.</EmptyState> : <ul className="qq-states">
        {states.map(state => <li key={state.status} className={`is-${state.status.toLowerCase()}`}><span>{state.label}</span><strong>{state.count.toLocaleString('tr-TR')}</strong></li>)}
      </ul>}
    </section>

    <section className="qq-section" aria-labelledby="qq-types">
      <h2 id="qq-types">İş Türüne Göre Kuyruk</h2>
      {metrics.isLoading ? <Busy text="İş türleri yükleniyor…" /> : metrics.isError && !metrics.data ? <p role="alert" className="qq-error">İş türü ölçümleri alınamadı.</p> : types.length === 0 ? <EmptyState icon="jobs" title="İş Yok">Bu aralıkta çalışan ya da bekleyen iş yok.</EmptyState> : <>
        <Tabs className="rv-view-tabs qq-platform-tabs" ariaLabel="İş türü platformu" value={activeTab} onChange={setPlatformTab} items={platformTabs.map(tab => ({
          value: tab.value, count: tab.count,
          label: tab.value === 'ALL' || tab.value === generalJobTab ? tab.label : <><PlatformSquareMark code={tab.value} name={tab.label} size="sm" />{tab.label}</>
        }))} />
        <div className="qq-table-scroll"><table className="qq-table">
        <thead><tr><th className="qq-first">İş Türü</th><th title="Bu aralıktaki denemeleri çalıştıran şerit; birden fazlaysa en çok çalıştıran önce">Şerit</th><th className="is-num">Bekleyen</th><th className="is-num">Çalışan</th><th className="is-num">Deneme</th><th className="is-num">Hata</th><th className="is-num">Ort. Bekleme</th><th className="is-num">Ort. Süre</th></tr></thead>
        <tbody>{shownTypes.map(row => <tr key={row.jobType}>
          <td className="qq-first"><strong>{jobTitle(row.jobType)}</strong><small>{row.jobType}</small></td>
          <td className="qq-lane-cell" data-label="Şerit">{(() => { const lane = jobLaneText(row.lanes); return <span className={(row.lanes ?? []).length > 0 ? 'qq-causes' : undefined} title={lane.title}>{lane.text}</span> })()}</td>
          <td className="is-num" data-label="Bekleyen">{row.queued.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Çalışan">{row.running.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Deneme">{row.attempts.toLocaleString('tr-TR')}</td>
          <td className={`is-num${row.failed > 0 ? ' is-error' : ''}`} data-label="Hata">{row.failed > 0
            ? <button type="button" className="qq-fail-link" aria-haspopup="dialog" aria-label={`${jobTitle(row.jobType)}: ${row.failed.toLocaleString('tr-TR')} hatayı göster`} onClick={() => openFailures(row.jobType)}>{row.failed.toLocaleString('tr-TR')}</button>
            : row.failed.toLocaleString('tr-TR')}</td>
          <td className="is-num" data-label="Ort. Bekleme">{formatSeconds(row.averageWaitSeconds)}</td>
          <td className="is-num" data-label="Ort. Süre">{formatSeconds(row.averageDurationSeconds)}</td>
        </tr>)}</tbody>
      </table></div></>}
    </section>
    {failuresOf && <Suspense fallback={null}><QueueFailuresDialog jobType={failuresOf} hours={hours} onClose={() => setFailuresOf(null)} onOpenJob={onOpenJob} /></Suspense>}
  </div>
}

export default QueueQuotaView
