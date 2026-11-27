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
// What each lane really carries (WorkerLanes and JobPriorityPolicy on the server). Queue phase 1A, item 5: a platform lane
// takes only part of its platform's reads; the scheduled order history scans of Hepsiburada, Pazarama and PTT AVM run in
// Arka Plan (priority 3), only a scan started by hand runs in the platform's lane. The job-type table's Şerit column shows
// where each type actually ran.
// hint: the short line under the lane's name; detail: the full list, in its tooltip.
const platformOrders = (platform: string, status: boolean) =>
  `${platform} yeni sipariş okuması${status ? ' ve açık sipariş durum taraması' : ''}. Sipariş geçmişi taraması yalnız elle başlatılınca burada; zamanlanmış olanı Arka Plan’da çalışır.`
const laneLabels: Record<string, { label: string; hint: string; detail?: string }> = {
  USER: { label: 'Kullanıcı İşlemleri', hint: 'Paket, iade, etiket, fatura ve elle sipariş yenileme.', detail: 'Kullanıcının beklediği işlemler: paket ve iade işlemleri, etiket, elle sipariş yenileme, fatura kesme ve iletme. Toplu fatura işleri de burada, tekil isteklerin arkasında sıraya girer.' },
  INITIAL: { label: 'İlk Çekim', hint: 'Yeni bağlanan mağazanın ilk veri çekimi.' },
  ORDER_STATUS: { label: 'Sipariş Durumu Taraması', hint: 'Trendyol ve Shopify açık sipariş durumları.' },
  RETURNS_READ: { label: 'İade Okuma', hint: 'Bütün pazaryerlerinin iadeleri ve iade durumları.', detail: 'Bütün pazaryerlerinin yeni iade okumaları ve açık iade durum kontrolleri.' },
  PRODUCT_CATALOG: { label: 'Ürün Kataloğu', hint: 'Ürün çekme, yayın doğrulaması, varyant arama.', detail: 'Ürün çekme, ürün yapısı aktarımı, gece yayın doğrulaması ve varyant arama.' },
  HEPSIBURADA_STATUS: { label: 'Hepsiburada Durum Taraması', hint: 'Hepsiburada açık sipariş durumları.' },
  HEPSIBURADA_ORDERS: { label: 'Hepsiburada Siparişleri', hint: 'Yeni siparişler; geçmiş taraması Arka Plan’da.', detail: platformOrders('Hepsiburada', false) },
  PAZARAMA_ORDERS: { label: 'Pazarama Siparişleri', hint: 'Yeni siparişler, durumlar; geçmiş Arka Plan’da.', detail: platformOrders('Pazarama', true) },
  PTTAVM_ORDERS: { label: 'PTT AVM Siparişleri', hint: 'Yeni siparişler, durumlar; geçmiş Arka Plan’da.', detail: platformOrders('PTT AVM', true) },
  HOT: { label: 'Öncelikli İşler', hint: 'Trendyol, N11, Shopify siparişleri; webhook, yazmalar.', detail: 'Trendyol, N11 ve Shopify yeni sipariş okumaları, anlık bildirimler (webhook), pazaryerine yazmalar (paket işlemi, fiyat ve stok, ürün) ve elle başlatılan diğer okumalar.' },
  BACKGROUND: { label: 'Arka Plan', hint: 'Geçmiş taramaları, uzlaştırma, soru, finans, listeler.', detail: 'Zamanlanmış diğer okumalar: sipariş geçmişi taramaları (Hepsiburada, Pazarama ve PTT AVM dahil), uzlaştırmalar, müşteri soruları, finans, kategori ve marka listeleri, fatura süre takibi.' },
  // Queue phase 2: the dispatcher's lanes, one per kind of work (DispatcherLanes on the server). Their codes differ from the
  // earlier lanes' (job_attempts keeps both around a switch), and the Arka Plan code too.
  KULLANICI: { label: 'Kullanıcı', hint: 'Panelden başlatılan tekil işler.', detail: 'Kullanıcının beklediği tekil işlemler: paket ve iade işlemleri, etiket, elle sipariş yenileme, fatura kesme ve iletme; iki slotu yalnız bunlarındır. Fiyat ve stok gönderimleri ile otomatik fatura durum kontrolleri Pazaryerine Yazma, sıfırlamalar ve toplu işler Toplu İşler şeridinde çalışır.' },
  WEBHOOK: { label: 'Webhook', hint: 'Pazaryerlerinden gelen anlık bildirimler.', detail: 'Pazaryerlerinin gönderdiği anlık bildirimler (webhook): kısa işler, kendi ayrılmış slotunda bekletilmez.' },
  YAZMA: { label: 'Pazaryerine Yazma', hint: 'Fiyat ve stok, ürün, paket ve fatura iletimi.', detail: 'Pazaryerine yazmalar: fiyat ve stok gönderimi (panelden toplu yayınlananlar dahil), ürün açma ve güncelleme, otomatik paket ve fatura işlemleri, fatura ve sipariş durumunun geri okunması. Panelden tek tek başlatılan paket ve fatura işlemleri Kullanıcı şeridinde.' },
  TOPLU: { label: 'Toplu İşler', hint: 'Toplu fatura ve ürün işleri, dosya aktarımı, sıfırlama.', detail: 'Panelden toplu başlatılan işler (toplu fatura, toplu ürün güncelleme ve yayın), dosyadan içe aktarma, panel ve bağlantı sıfırlaması: payı kadar slotu önce alır, tekil kullanıcı işlerinin önüne geçmez.' },
  ACIL: { label: 'Elle Başlatılan Okuma', hint: 'Elle başlatılan kısa okumalar.', detail: 'Panelden elle başlatılan diğer kısa okumalar (sipariş geçmişi taraması, ürün veya iade yenileme gibi). Webhooklar ve pazaryerine yazmalar kendi şeritlerinde.' },
  SIPARIS_OKUMA: { label: 'Sipariş Okuma', hint: 'Bütün pazaryerlerinin yeni sipariş ve geçmiş okumaları.', detail: 'Trendyol, Hepsiburada, N11, Pazarama, PTT AVM ve Shopify yeni sipariş okumaları ve sipariş geçmişi taramaları.' },
  DURUM_TARAMASI: { label: 'Durum Taraması', hint: 'Bütün pazaryerlerinin açık sipariş durumları.', detail: 'Açık siparişlerin durumlarının yeniden okunması, bütün pazaryerleri.' },
  IADE: { label: 'İade', hint: 'Bütün pazaryerlerinin iadeleri ve iade durumları.', detail: 'Bütün pazaryerlerinin yeni iade okumaları ve açık iade durum kontrolleri.' },
  KATALOG: { label: 'Katalog', hint: 'Ürün çekme, yayın doğrulaması, varyant arama.', detail: 'Ürün çekme, ürün yapısı aktarımı, gece yayın doğrulaması ve varyant arama.' },
  ARKA_PLAN: { label: 'Arka Plan', hint: 'Uzlaştırma, soru, finans, kategori ve marka listeleri.', detail: 'Zamanlanmış diğer okumalar: uzlaştırmalar, müşteri soruları, finans, kategori ve marka listeleri, fatura süre takibi.' },
  ILK_CEKIM: { label: 'İlk Çekim', hint: 'Yeni bağlanan mağazanın ilk veri çekimi.' }
}

const dispatcherLaneCodes = new Set(['KULLANICI', 'WEBHOOK', 'YAZMA', 'ACIL', 'SIPARIS_OKUMA', 'DURUM_TARAMASI', 'IADE', 'TOPLU', 'KATALOG', 'ARKA_PLAN', 'ILK_CEKIM'])

/** Whether the lane metrics come from the dispatcher (Worker:LaneModel=dispatcher). */
export function isDispatcherModel(lanes: readonly LaneMetric[] | null | undefined) {
  return (lanes ?? []).some(lane => lane.model === 'dispatcher')
}

/** A dispatcher lane's slots in words: reserved ("Ayrılmış 2": Kullanıcı; "Ayrılmış 1": Webhook, Pazaryerine Yazma), a minimum ("Asgari 3":
 * Sipariş Okuma, Durum Taraması: the same rule, never lent), a share of the shared slots ("Pay 2": served first on that many
 * while it has work) or both ("Ayrılmış 1 · Pay 1"). Null with the earlier lane models (they do not send the figures). */
export function laneSlotKind(lane: LaneMetric): { text: string; title: string; stacked?: boolean } | null {
  if (lane.model !== 'dispatcher') return null
  const reserved = lane.reserved ?? 0
  const share = lane.share ?? 0
  const shareTitle = `İşi varken ortak slotlardan ${share} tanesini diğer şeritlerden önce alır; boşken başka şeritler kullanır.`
  if (reserved > 0) {
    const minimum = lane.lane === 'SIPARIS_OKUMA' || lane.lane === 'DURUM_TARAMASI'
    const reservedTitle = `${reserved} slot yalnız bu şeridindir, başka şeride verilmez. Bunlar doluysa ortak slotlardan biri boşsa onu alır.`
    return {
      text: `${minimum ? 'Asgari' : 'Ayrılmış'} ${reserved}${share > 0 ? `\nPay ${share}` : ''}`,
      stacked: share > 0,
      title: share > 0 ? `${reservedTitle}\n${shareTitle}` : reservedTitle
    }
  }
  if (share > 0) return { text: `Pay ${share}`, title: shareTitle }
  return { text: 'Ortak', title: 'Yalnız ortak slotları kullanır.' }
}

export type LanePlatform = NonNullable<LaneMetric['platforms']>[number]

/** Marketplace tabs above the lane table: "Tümü", then the marketplaces with queued or running jobs in some lane. */
export function lanePlatformTabs(lanes: readonly LaneMetric[]): JobPlatformTab[] {
  const counts = new Map<string, number>()
  for (const lane of lanes)
    for (const row of lane.platforms ?? [])
      if (row.platformCode && row.queued + row.running > 0) counts.set(row.platformCode, (counts.get(row.platformCode) ?? 0) + row.queued + row.running)
  const tabs: JobPlatformTab[] = [{ value: 'ALL', label: 'Tümü', count: lanes.reduce((sum, lane) => sum + lane.queued + lane.running, 0) }]
  for (const code of platformDisplayOrder) if (counts.has(code)) tabs.push({ value: code, label: platformDisplayName(code), count: counts.get(code)! })
  return tabs
}

/** A lane's running and queued jobs for one marketplace tab ("ALL": the lane's own figures). */
export function laneCountsFor(lane: LaneMetric, tab: string): { running: number; queued: number } {
  if (tab === 'ALL') return { running: lane.running, queued: lane.queued }
  const row = (lane.platforms ?? []).find(item => item.platformCode === tab)
  return { running: row?.running ?? 0, queued: row?.queued ?? 0 }
}

/** The lane's full description (tooltip of its short line). */
export function laneDetail(code: string) {
  return laneLabels[code]?.detail ?? laneLabels[code]?.hint ?? ''
}

export type JobTypeLane = NonNullable<JobTypeMetric['lanes']>[number]

/** The Şerit cell: the lanes that ran the type's attempts in the window ("Arka Plan" or "Arka Plan +1") and a tooltip with the counts. */
export function jobLaneText(lanes: readonly JobTypeLane[] | null | undefined): { text: string; title: string } {
  const list = lanes ?? []
  if (list.length === 0) return { text: '—', title: 'Bu aralıkta şerit kaydı yok (şerit 27 Kasım’dan beri kaydediliyor).' }
  const text = list.length === 1 ? laneLabel(list[0].lane) : `${laneLabel(list[0].lane)} +${list.length - 1}`
  // Borrowed: on another lane's idle slot (earlier lanes), or outside the lane's own share (the dispatcher's lanes).
  const borrowedText = (lane: JobTypeLane) => dispatcherLaneCodes.has(lane.lane) ? 'payının dışında' : 'ödünç slotta'
  const title = list.map(lane => `${laneLabel(lane.lane)}: ${lane.attempts.toLocaleString('tr-TR')} deneme${lane.borrowed > 0 ? ` (${lane.borrowed.toLocaleString('tr-TR')} ${borrowedText(lane)})` : ''}`).join('\n')
  return { text, title }
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

/** The lane's recorded wait causes as tooltip lines, over the same first attempts as "Ort. Bekleme"; nothing while no
 * attempt recorded them. A reason no attempt knows (e.g. the slot with legacy lanes) reads "—", never 0. */
export function laneWaitCauseLines(causes: LaneWaitCauses | null | undefined): string[] {
  if (!causes || causes.attempts === 0) return []
  const part = (label: string, seconds: number | null | undefined) => `${label}: ${formatSeconds(seconds)}`
  return [
    `İlk denemelerde bekleme nedeni (${causes.attempts.toLocaleString('tr-TR')} deneme, toplam ${formatSeconds(causes.waitSeconds)})`,
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

export type JobTypeFailures = ApiAnswer<'getJobsMetricsFailures'>

/** Rows per page of the Hata window's attempt list. */
export const failurePageSize = 20

/** GET /jobs/metrics/failures for one job type: the window (hours) of the table, so its total is the clicked number. */
export function failuresPath(jobType: string, hours: QueueWindow, page: number) {
  return `/jobs/metrics/failures?jobType=${encodeURIComponent(jobType)}&hours=${hours}&page=${page}&pageSize=${failurePageSize}`
}

/** What became of a failed attempt's job, from its stored status only: it succeeded later, still fails, or is retried. */
export function failureOutcome(jobStatus: string): { label: string; tone: 'ok' | 'bad' | 'wait' | 'muted' } {
  const status = jobStatus.trim().toUpperCase()
  if (status === 'SUCCEEDED') return { label: 'Sonra Başarılı', tone: 'ok' }
  if (status === 'DEAD' || status === 'MANUAL_REVIEW') return { label: 'Hâlâ Hatalı', tone: 'bad' }
  if (status === 'PENDING' || status === 'RETRY_SCHEDULED') return { label: 'Yeniden Denenecek', tone: 'wait' }
  if (status === 'LEASED') return { label: 'Yeniden Deneniyor', tone: 'wait' }
  return { label: jobStatusLabel(status), tone: 'muted' }
}

/** The reason a failure shows: the stored sentence, else its code, else that none was stored. */
export function failureReason(reason: string | null | undefined, errorCode: string | null | undefined) {
  return reason?.trim() || errorCode?.trim() || 'Sebep kaydedilmemiş'
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
