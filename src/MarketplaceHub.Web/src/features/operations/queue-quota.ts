import { jobStatusLabel } from '../../shared/job-status-labels'
import { platformDisplayName, platformDisplayOrder } from '../../shared/platform-names'
import type { ApiAnswer } from '../../shared/api/api-answer'

// İşlem Takibi › Kuyruk ve Kota: what GET /jobs/metrics, /jobs/metrics/lanes, /jobs/metrics/quota,
// /jobs/metrics/order-latency and /jobs/counts answer, and the words the tab shows for them.

export type LaneMetric = ApiAnswer<'getJobsMetricsLanes'>[number]
export type JobTypeMetric = ApiAnswer<'getJobsMetrics'>[number]
export type QuotaUsage = ApiAnswer<'getJobsMetricsQuota'>[number]
export type JobCount = ApiAnswer<'getJobsCounts'>[number]

export type QueueWindow = '1' | '24' | '168'
export const queueWindowOptions: Array<{ value: QueueWindow; label: string }> = [
  { value: '1', label: 'Son 1 Saat' }, { value: '24', label: 'Son 24 Saat' }, { value: '168', label: 'Son 7 Gün' }
]

// The worker's lanes (WorkerLanes): which work each runs, in plain words.
const laneLabels: Record<string, { label: string; hint: string }> = {
  USER: { label: 'Kullanıcı İşlemleri', hint: 'Panelden başlatılan işler; toplu işler de burada sıraya girer.' },
  INITIAL: { label: 'İlk Çekim', hint: 'Yeni bağlanan mağazanın ilk veri çekimi.' },
  ORDER_STATUS: { label: 'Sipariş Durumu Taraması', hint: 'Açık siparişlerin durumlarının yeniden okunması.' },
  RETURNS_READ: { label: 'İade Okuma', hint: 'Pazaryerlerinden iade talepleri ve durumları.' },
  PRODUCT_CATALOG: { label: 'Ürün Kataloğu', hint: 'Ürün listesi eşitleme ve yayın kontrolleri.' },
  HEPSIBURADA_STATUS: { label: 'Hepsiburada Durum Taraması', hint: 'Hepsiburada açık sipariş durumları.' },
  HEPSIBURADA_ORDERS: { label: 'Hepsiburada Siparişleri', hint: 'Hepsiburada sipariş okumaları.' },
  PAZARAMA_ORDERS: { label: 'Pazarama Siparişleri', hint: 'Pazarama sipariş okumaları.' },
  PTTAVM_ORDERS: { label: 'PTT AVM Siparişleri', hint: 'PTT AVM sipariş okumaları.' },
  HOT: { label: 'Öncelikli İşler', hint: 'Yeni siparişler, webhooklar ve pazaryerine yazmalar.' },
  BACKGROUND: { label: 'Arka Plan', hint: 'Zamanlanmış diğer okumalar.' }
}

export function laneLabel(code: string) {
  return laneLabels[code]?.label ?? code.split('_').map(part => part.charAt(0) + part.slice(1).toLocaleLowerCase('tr-TR')).join(' ')
}
export function laneHint(code: string) {
  return laneLabels[code]?.hint ?? ''
}

export type OrderLatency = ApiAnswer<'getJobsMetricsOrderLatency'>[number]
export type LaneWaitCauses = NonNullable<LaneMetric['waitCauses']>

/** Marketplace tabs above a list: "Tümü" first, then the marketplaces that have rows, in display order. */
export function marketplaceTabs(rows: readonly { platformCode: string }[]): JobPlatformTab[] {
  const counts = new Map<string, number>()
  for (const row of rows) counts.set(row.platformCode, (counts.get(row.platformCode) ?? 0) + 1)
  const tabs: JobPlatformTab[] = [{ value: 'ALL', label: 'Tümü', count: rows.length }]
  for (const code of platformDisplayOrder) if (counts.has(code)) tabs.push({ value: code, label: platformDisplayName(code), count: counts.get(code)! })
  return tabs
}

/** The rows of one marketplace tab; a tab with no rows any more shows everything. */
export function filterByMarketplace<T extends { platformCode: string }>(rows: readonly T[], tab: string): T[] {
  if (tab === 'ALL') return [...rows]
  const matching = rows.filter(row => row.platformCode === tab)
  return matching.length > 0 ? matching : [...rows]
}

/** A quota bucket's busiest minute against its limit: "81 / 100 dk", or "81 / dk" when the code keeps no limit for it. */
export function quotaPeakText(row: QuotaUsage) {
  const limit = row.limitPerMinute
  return limit === null || limit === undefined ? `${row.peakMinuteRequests.toLocaleString('tr-TR')} / dk` : `${row.peakMinuteRequests.toLocaleString('tr-TR')} / ${limit.toLocaleString('tr-TR')} dk`
}

/** An order-flow time: below 10 s with one decimal ("0,4 sn": the panel's own part is often under a second), else formatSeconds. */
export function latencySeconds(seconds: number | null | undefined) {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—'
  return seconds < 10 ? `${seconds.toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sn` : formatSeconds(seconds)
}

// How an order reached the panel (GET /jobs/metrics/order-latency): every order, by a webhook delivery, or by a read.
const latencySources: Record<string, string> = { ALL: 'Tüm Siparişler', WEBHOOK: 'Webhook ile', POLLING: 'Okuma ile' }
export function latencySourceLabel(source: string) {
  return latencySources[source] ?? source
}

/** A flow against its target: OK green, WARN (within twice the target) amber, BAD red; no status, no colour. */
export function latencyState(status: string | null | undefined): { label: string; tone: 'ok' | 'warn' | 'bad' } | null {
  if (status === 'OK') return { label: 'Hedefte', tone: 'ok' }
  if (status === 'WARN') return { label: 'Hedefe Yakın', tone: 'warn' }
  if (status === 'BAD') return { label: 'Hedef Aşıldı', tone: 'bad' }
  return null
}

/** The lane's recorded wait causes as tooltip lines; nothing while no attempt recorded them. */
export function laneWaitCauseLines(causes: LaneWaitCauses | null | undefined): string[] {
  if (!causes || causes.attempts === 0) return []
  const part = (label: string, seconds: number) => `${label}: ${formatSeconds(seconds)}`
  return [
    `Bekleme nedeni (${causes.attempts.toLocaleString('tr-TR')} deneme, toplam ${formatSeconds(causes.waitSeconds)})`,
    part('Aynı bağlantının kilidi', causes.lockSeconds),
    part('Kota geri çekilmesi', causes.backoffSeconds),
    part('Kota bekletmesi', causes.quotaHoldSeconds),
    part('Boş slot yok', causes.slotSeconds),
    part('Nedeni bilinmeyen', causes.unknownSeconds)
  ]
}

/** "8 sn", "3,5 dk", "1,2 sa"; a missing value is "—". */
export function formatSeconds(seconds: number | null | undefined) {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—'
  if (seconds < 60) return `${Math.round(seconds).toLocaleString('tr-TR')} sn`
  if (seconds < 3600) return `${(seconds / 60).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} dk`
  return `${(seconds / 3600).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sa`
}

/** A lane against its wait target: Hedefte, Hedef Aşıldı (missed at this look) or Uyarı (missed for ten minutes on end). */
export function laneWaitState(status: string): { label: string; tone: 'ok' | 'warn' | 'bad' } {
  if (status === 'ALERT') return { label: 'Uyarı', tone: 'bad' }
  if (status === 'OVER_TARGET') return { label: 'Hedef Aşıldı', tone: 'warn' }
  return { label: 'Hedefte', tone: 'ok' }
}

/** The share of a limit used, 0 to 100 (more than the limit shows full). */
export function usagePercent(used: number, limit: number) {
  if (!(limit > 0)) return 0
  return Math.max(0, Math.min(100, Math.round((used / limit) * 100)))
}

/** How busy a share is: calm below 60 %, busy to 90 %, full above. */
export function usageTone(percent: number): 'ok' | 'busy' | 'full' {
  return percent >= 90 ? 'full' : percent >= 60 ? 'busy' : 'ok'
}

export type LaneTotals = { running: number; queued: number; slots: number; attempts: number }
export function laneTotals(lanes: readonly LaneMetric[]): LaneTotals {
  return lanes.reduce((total, lane) => ({ running: total.running + lane.running, queued: total.queued + lane.queued, slots: total.slots + lane.slots, attempts: total.attempts + lane.attempts }), { running: 0, queued: 0, slots: 0, attempts: 0 })
}

/** The job types with work now or in the window, busiest first: what waits and runs, then what ran. */
export function activeJobTypes(metrics: readonly JobTypeMetric[]) {
  return metrics.filter(row => row.queued + row.running + row.attempts > 0)
    .sort((a, b) => (b.queued + b.running) - (a.queued + a.running) || b.attempts - a.attempts || a.jobType.localeCompare(b.jobType))
}

// The order the job states are listed in: open work first, then the outcomes.
const statusOrder = ['PENDING', 'RETRY_SCHEDULED', 'LEASED', 'BLOCKED', 'MANUAL_REVIEW', 'SUCCEEDED', 'DEAD', 'CANCELLED']

/** The jobs of the window by state, every type added up, in statusOrder (unknown states last). */
export function countsByStatus(counts: readonly JobCount[]) {
  const totals = new Map<string, number>()
  for (const row of counts) totals.set(row.status, (totals.get(row.status) ?? 0) + row.count)
  return [...totals.entries()]
    .map(([status, count]) => ({ status, label: jobStatusLabel(status), count }))
    .sort((a, b) => (statusOrder.indexOf(a.status) + 1 || 99) - (statusOrder.indexOf(b.status) + 1 || 99))
}

/** The /jobs/counts since value of a window, rounded down to the minute so the query key holds for a minute. */
export function windowSince(hours: QueueWindow, now = Date.now()) {
  const minute = Math.floor(now / 60_000) * 60_000
  return new Date(minute - Number(hours) * 3_600_000).toISOString()
}

/** The tab of the jobs that belong to no marketplace (question sync, reset, retention, invoice provider…). */
export const generalJobTab = 'GENERAL'

/** The marketplace code a job type starts with (TRENDYOL_, HEPSIBURADA_, N11_, PAZARAMA_, PTTAVM_, SHOPIFY_), else null. */
export function jobPlatformCode(jobType: string): string | null {
  const type = jobType.toUpperCase()
  const prefix = type.split('_')[0]
  return (platformDisplayOrder as readonly string[]).includes(prefix) && type.includes('_') && !type.includes('EFATURAM') ? prefix : null
}

export type JobPlatformTab = { value: string; label: string; count: number }

/** "Tümü" first, then only the platforms that have rows (display order), then "Genel" for platform-less jobs. */
export function jobPlatformTabs(rows: readonly JobTypeMetric[]): JobPlatformTab[] {
  const counts = new Map<string, number>()
  for (const row of rows) { const key = jobPlatformCode(row.jobType) ?? generalJobTab; counts.set(key, (counts.get(key) ?? 0) + 1) }
  const tabs: JobPlatformTab[] = [{ value: 'ALL', label: 'Tümü', count: rows.length }]
  for (const code of platformDisplayOrder) if (counts.has(code)) tabs.push({ value: code, label: platformDisplayName(code), count: counts.get(code)! })
  if (counts.has(generalJobTab)) tabs.push({ value: generalJobTab, label: 'Genel', count: counts.get(generalJobTab)! })
  return tabs
}

/** The rows of one tab; a tab with no rows any more (the window changed) shows everything. */
export function filterJobsByTab<T extends JobTypeMetric>(rows: readonly T[], tab: string): T[] {
  if (tab === 'ALL') return [...rows]
  const matching = rows.filter(row => (jobPlatformCode(row.jobType) ?? generalJobTab) === tab)
  return matching.length > 0 ? matching : [...rows]
}
