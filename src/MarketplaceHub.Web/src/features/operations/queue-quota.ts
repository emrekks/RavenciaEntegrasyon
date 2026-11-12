import { jobStatusLabel } from '../../shared/job-status-labels'

// İşlem Takibi › Kuyruk ve Kota: what GET /jobs/metrics, /jobs/metrics/lanes, /jobs/metrics/quota and /jobs/counts
// answer, and the words the tab shows for them.

export type LaneMetric = { lane: string; attempts: number; averageWaitSeconds: number | null; p95WaitSeconds: number | null; queued: number; running: number; slots: number }
export type JobTypeMetric = { jobType: string; attempts: number; failed: number; averageWaitSeconds: number | null; p95WaitSeconds: number | null; averageDurationSeconds: number | null; p95DurationSeconds: number | null; queued: number; running: number }
export type QuotaUsage = { platformCode: string; storeName: string; lane: string; limitPerMinute: number; lastHourRequests: number; peakMinuteRequests: number; averagePerMinute: number }
export type JobCount = { status: string; jobType: string; count: number }

export type QueueWindow = '1' | '24' | '168'
export const queueWindowOptions: Array<{ value: QueueWindow; label: string }> = [
  { value: '1', label: 'Son 1 Saat' }, { value: '24', label: 'Son 24 Saat' }, { value: '168', label: 'Son 7 Gün' }
]

// The worker's lanes (WorkerLanes): which work each runs, in plain words.
const laneLabels: Record<string, { label: string; hint: string }> = {
  USER: { label: 'Kullanıcı İşlemleri', hint: 'Panelden başlatılan işler; toplu işler de burada sıraya girer.' },
  INITIAL: { label: 'İlk Aktarım', hint: 'Yeni bağlanan mağazanın ilk veri çekimi.' },
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

export const quotaLaneLabels: Record<string, string> = { FOREGROUND: 'Ön Plan', BACKGROUND: 'Arka Plan' }

/** "8 sn", "3,5 dk", "1,2 sa"; a missing value is "—". */
export function formatSeconds(seconds: number | null | undefined) {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—'
  if (seconds < 60) return `${Math.round(seconds).toLocaleString('tr-TR')} sn`
  if (seconds < 3600) return `${(seconds / 60).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} dk`
  return `${(seconds / 3600).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sa`
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
