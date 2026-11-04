// Small pure pieces of the integrations pages, kept here so they can be tested on their own.

/** Owner decision B: what switching the automatic writes (or the master switch) on means for the changes made while off. */
export const automaticWritesBaselineWarning = 'Kapalıyken yapılan değişiklikler pazaryerine gönderilmedi. Açmadan önce pazaryerlerindeki stok ve fiyatları güncellemenizi öneririz.'

export function automaticWritesBaselineMessage(externalWriteTo: string) {
  return `${automaticWritesBaselineWarning} Açıldığı andaki fiyat ve stoklar gönderilmiş sayılır; bundan sonraki değişiklikler ${externalWriteTo} otomatik gönderilir.`
}

/** The query of GET /price-stock-monitor (API Sağlık); a change to the master switch or a write flow reads it again. */
export const priceStockMonitorQueryKey = ['price-stock-monitor'] as const

/** One connection of GET /price-stock-monitor (PriceStockConnectionMonitorView). */
export type PriceStockConnectionMonitor = {
  connectionId: string
  platformCode: string
  displayName: string
  status: string
  masterSwitchOn: boolean
  priceWriteEnabled: boolean
  stockWriteEnabled: boolean
  automaticWritesOpen: boolean
  lastSuccessfulPushAt: string | null
  queuedJobs: number
  waitingForOrderRead: number
  failedPushes7d: number
  manualReviewPushes: number
  invalidOffers: number
  notFoundLinks: number
  rejectedLinks: number
  latestFailure: { jobId: string; errorCode: string | null; errorSummary: string | null; status: string; at: string | null } | null
  invalidOfferSamples: Array<{ code: string; summary: string; lastSeenAt: string }>
  /** Where new orders come from: WEBHOOK (notifications), WEBHOOK_FALLBACK (notifications unhealthy, read on the short cadence) or POLLING. */
  orderSource?: 'WEBHOOK' | 'WEBHOOK_FALLBACK' | 'POLLING' | string
  lastNotificationAt?: string | null
  /** The cadence the scheduled order read actually runs at; null when the order read is off. */
  orderReadIntervalSeconds?: number | null
}

function cadenceText(seconds: number | null | undefined) {
  if (seconds == null) return null
  return seconds % 60 === 0 ? `${seconds / 60} dk` : `${seconds} sn`
}

/** How the connection's new orders reach the panel, in words, with a warning tone while notifications are unhealthy. */
export function orderSourceState(row: Pick<PriceStockConnectionMonitor, 'orderSource' | 'orderReadIntervalSeconds'>): { label: string; detail: string | null; tone: 'on' | 'partial' | 'off' } {
  const cadence = cadenceText(row.orderReadIntervalSeconds)
  if (row.orderSource === 'WEBHOOK') return { label: 'Webhook', detail: cadence ? `Yedek okuma ${cadence}` : null, tone: 'on' }
  if (row.orderSource === 'WEBHOOK_FALLBACK') return { label: 'Webhook Sorunlu', detail: cadence ? `Sık okuma ${cadence}` : 'Sipariş okuması kapalı', tone: 'partial' }
  if (row.orderSource === 'POLLING') return { label: 'Düzenli Okuma', detail: cadence ? `Her ${cadence}` : 'Sipariş okuması kapalı', tone: 'off' }
  // A source the API did not report stays unknown; it is not taken for polling.
  return { label: 'Bilinmiyor', detail: null, tone: 'off' }
}

export type PriceStockMonitor = { generatedAt: string; connections: PriceStockConnectionMonitor[] }

/** How a count of the monitor reads: zero is muted, a failure is bad, anything else waiting or held back is a warning. */
export type PriceStockMetricTone = 'muted' | 'warn' | 'bad'
export function priceStockMetricTone(value: number, kind: 'queue' | 'failed' | 'invalid' | 'lost'): PriceStockMetricTone {
  if (!(value > 0)) return 'muted'
  return kind === 'failed' ? 'bad' : 'warn'
}

/** A connection needs a look when a push failed or waits for review, an offer is held back, a listing is lost, or the
 *  webhook is unhealthy. A queue alone is ordinary work in progress. */
export function priceStockNeedsAttention(row: Pick<PriceStockConnectionMonitor, 'failedPushes7d' | 'manualReviewPushes' | 'invalidOffers' | 'notFoundLinks' | 'rejectedLinks' | 'orderSource'>) {
  return row.failedPushes7d + row.manualReviewPushes + row.invalidOffers + row.notFoundLinks + row.rejectedLinks > 0 || row.orderSource === 'WEBHOOK_FALLBACK'
}

/** The strip above the connections: how many there are, writes open/held/off, where orders come from, how many need a look. */
export function priceStockMonitorSummary(rows: PriceStockConnectionMonitor[]) {
  const summary = { connections: rows.length, writesOpen: 0, writesHeld: 0, writesOff: 0, webhook: 0, webhookTrouble: 0, polling: 0, unknownSource: 0, needsAttention: 0 }
  for (const row of rows) {
    const write = priceStockWriteState(row).tone
    if (write === 'on') summary.writesOpen++
    else if (write === 'partial') summary.writesHeld++
    else summary.writesOff++
    if (row.orderSource === 'WEBHOOK') summary.webhook++
    else if (row.orderSource === 'WEBHOOK_FALLBACK') summary.webhookTrouble++
    else if (row.orderSource === 'POLLING') summary.polling++
    else summary.unknownSource++
    if (priceStockNeedsAttention(row)) summary.needsAttention++
  }
  return summary
}

/** "Az önce", "12 dk önce", "5 sa önce", "3 gün önce"; null when the time is unknown or unreadable. */
export function relativeAgeText(value: string | null | undefined, now = Date.now()) {
  if (!value) return null
  const at = new Date(value).getTime()
  if (Number.isNaN(at)) return null
  const minutes = Math.max(0, Math.floor((now - at) / 60_000))
  if (minutes < 1) return 'Az önce'
  if (minutes < 60) return `${minutes} dk önce`
  const hours = Math.floor(minutes / 60)
  return hours < 48 ? `${hours} sa önce` : `${Math.floor(hours / 24)} gün önce`
}

/** The automatic write state of a connection in words: open, held (one policy on) or off, and why. */
export function priceStockWriteState(row: Pick<PriceStockConnectionMonitor, 'masterSwitchOn' | 'priceWriteEnabled' | 'stockWriteEnabled' | 'automaticWritesOpen'>): { label: string; tone: 'on' | 'partial' | 'off' } {
  if (row.automaticWritesOpen) return { label: 'Açık', tone: 'on' }
  if (!row.masterSwitchOn) return { label: 'Kapalı · Ana Anahtar', tone: 'off' }
  if (row.priceWriteEnabled !== row.stockWriteEnabled) return { label: row.stockWriteEnabled ? 'Bekletiliyor · Fiyat Kapalı' : 'Bekletiliyor · Stok Kapalı', tone: 'partial' }
  return { label: 'Kapalı', tone: 'off' }
}

/** Problems the monitor reports for a connection; a notification is raised when this signature changes. */
export function priceStockProblemSignature(row: Pick<PriceStockConnectionMonitor, 'connectionId' | 'failedPushes7d' | 'manualReviewPushes' | 'invalidOffers' | 'latestFailure'>) {
  if (row.failedPushes7d + row.manualReviewPushes + row.invalidOffers === 0) return null
  return `${row.connectionId}:${row.latestFailure?.jobId ?? '-'}:${row.failedPushes7d}:${row.manualReviewPushes}:${row.invalidOffers}`
}

/** The body of PUT /connections/{id}/credential for the platform's credential form. */
export function credentialRequestBody(platformCode: string, data: FormData) {
  if (platformCode === 'TRENDYOL_EFATURAM') return { email: data.get('email') || null, password: data.get('password') || null }
  if (platformCode === 'SHOPIFY') return { shopifyAccessToken: data.get('shopifyAccessToken') }
  if (platformCode === 'HEPSIBURADA') return { hepsiburadaIntegratorUsername: data.get('hepsiburadaIntegratorUsername'), hepsiburadaServiceKey: data.get('hepsiburadaServiceKey') }
  // PTT AVM: the API user name and password of the seller panel’s Entegrasyon Bilgileri screen (the Mağaza ID is the store field).
  if (platformCode === 'PTTAVM') return { pttAvmUsername: data.get('apiKey'), pttAvmPassword: data.get('apiSecret') }
  return { apiKey: data.get('apiKey'), apiSecret: data.get('apiSecret') }
}

/** The environment in Turkish; Shopify, N11, Pazarama and PTT AVM connect only to the live store. */
export function connectionEnvironmentLabel(platformCode: string, environment: string | null | undefined) {
  if (platformCode === 'SHOPIFY' || platformCode === 'N11' || platformCode === 'PAZARAMA' || platformCode === 'PTTAVM') return 'Canlı'
  return environment === 'PRODUCTION' ? 'Canlı' : 'Test Ortamı'
}

/**
 * Whether the connection offers a test-environment order. Only test (STAGE) connections of Trendyol and Hepsiburada
 * do; which Trendyol test store may really create one is the server's rule (it refuses any other), so the page no
 * longer carries a store number of its own.
 */
export function stageTestOrderAvailable(item: { platformCode: string; environment: string | null | undefined }) {
  return (item.platformCode === 'TRENDYOL' || item.platformCode === 'HEPSIBURADA') && item.environment === 'STAGE'
}

// The integrations list's filters live in the address (W16):
//   ?gorunum=bagli&durum=dikkat&platform=hepsiburada
export type IntegrationView = 'ALL' | 'ACTIVE'
export type IntegrationStatusFilter = 'ALL' | 'ACTIVE' | 'ATTENTION'
export type IntegrationPlatformFilter = 'ALL' | 'TRENDYOL' | 'TRENDYOL_EFATURAM' | 'SHOPIFY' | 'HEPSIBURADA' | 'N11' | 'PAZARAMA' | 'PTTAVM'
export type IntegrationListState = { view: IntegrationView; filter: IntegrationStatusFilter; platform: IntegrationPlatformFilter }

export const integrationPlatformFilters: readonly IntegrationPlatformFilter[] = ['ALL', 'TRENDYOL', 'TRENDYOL_EFATURAM', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM']
const statusSlugs: Record<IntegrationStatusFilter, string> = { ALL: '', ACTIVE: 'aktif', ATTENTION: 'dikkat' }

export function readIntegrationListState(params: URLSearchParams): IntegrationListState {
  const platform = (params.get('platform') ?? '').toUpperCase()
  const filter = (Object.keys(statusSlugs) as IntegrationStatusFilter[]).find(key => statusSlugs[key] && statusSlugs[key] === params.get('durum')) ?? 'ALL'
  return {
    view: params.get('gorunum') === 'bagli' ? 'ACTIVE' : 'ALL',
    filter,
    platform: (integrationPlatformFilters as readonly string[]).includes(platform) ? platform as IntegrationPlatformFilter : 'ALL'
  }
}

export function writeIntegrationListState(current: URLSearchParams, patch: Partial<IntegrationListState>) {
  const next = { ...readIntegrationListState(current), ...patch }
  const params = new URLSearchParams(current)
  const set = (name: string, value: string) => { if (value) params.set(name, value); else params.delete(name) }
  set('gorunum', next.view === 'ACTIVE' ? 'bagli' : '')
  set('durum', statusSlugs[next.filter])
  set('platform', next.platform === 'ALL' ? '' : next.platform.toLowerCase())
  return params
}

// Trendyol asks for "User-Agent: {sellerId} - {IntegratorName}" (name: up to 30 ASCII letters or digits). The
// worker only logs a warning for another format, so this is advice; a non-ASCII identity cannot be sent at all, so
// that 'error' blocks the new-connection form (the server refuses it on save as well).
export function trendyolUserAgentNotice(identity: string, sellerId: string): { kind: 'warning' | 'error', message: string } | null {
  const value = identity.trim()
  if (!value) return null
  if (![...value].every(character => character >= ' ' && character <= '~')) return { kind: 'error', message: 'Türkçe karakter ve özel işaret kullanılamaz; bu kimlikle Trendyol’a istek gönderilemez.' }
  const seller = sellerId.trim()
  const match = /^([0-9]+) - [A-Za-z0-9]{1,30}$/.exec(value)
  if (match && (!seller || match[1] === seller)) return null
  return { kind: 'warning', message: `Trendyol “${seller || 'SatıcıID'} - FirmaAdı” biçimini istiyor (firma adı en çok 30 harf/rakam, boşluksuz). Bağlantı yine kaydedilir.` }
}

/**
 * The notice of a connection test in the seller's words: which store was reached and that nothing was changed there (a
 * test only reads). A failure keeps the marketplace's own reason in the sentence and its code for the Ayrıntı rows.
 */
export function connectionTestFeedback(platformName: string, result: { succeeded?: boolean; errorCode?: string; errorSummary?: string }) {
  const context = { operation: 'Bağlantı testi', record: true, code: result.errorCode || undefined }
  if (result.succeeded === true) {
    return { kind: 'success' as const, title: 'Bağlantı Testi Başarılı', message: `${platformName} mağazasına erişim doğrulandı. Mağazada hiçbir ürün, stok, fiyat veya sipariş değiştirilmedi.`, details: [{ label: 'Kontrol', value: 'Bağlantının yapabildiği işlemler yeniden doğrulandı' }], context }
  }
  const reason = result.errorSummary?.trim()
  return { kind: 'error' as const, title: 'Bağlantı Testi Başarısız', message: reason ? `${platformName} mağazasına erişilemedi: ${reason}` : `${platformName} mağazasına erişilemedi. Kimlik bilgilerini ve mağaza kimliğini kontrol edin.`, context }
}

/** The cadence the scheduler keeps for a sync flow (SyncCadenceView): this pass's interval, the bounds and the idle rule. */
export type SyncCadence = {
  effectiveIntervalSeconds: number
  defaultIntervalSeconds: number
  minimumIntervalSeconds: number
  maximumIntervalSeconds: number
  basis: string
  idleKind: 'WEBHOOK' | 'QUIET' | 'RETURNS' | null
  idleBackoffEnabled: boolean
  idleIntervalSeconds: number | null
  defaultIdleIntervalSeconds: number | null
  idleActive: boolean
  stageFloor: boolean
  /** Why the scheduler does not queue the flow right now; null while it runs. */
  heldReason?: string | null
}

/** A short interval for the flow table: "2 dk", "1 sa", "1 gün". */
export function cadenceShortText(seconds: number) {
  if (seconds >= 86400 && seconds % 86400 === 0) return `${seconds / 86400} gün`
  if (seconds >= 3600 && seconds % 3600 === 0) return `${seconds / 3600} sa`
  if (seconds >= 60 && seconds % 60 === 0) return `${seconds / 60} dk`
  return `${seconds} sn`
}

/** The choices of a cadence select within the flow's bounds; the current value stays listed even when outside them. */
export function cadenceChoices(intervals: Array<[number, string]>, minimum: number, maximum: number, current: number) {
  const choices = intervals.filter(([seconds]) => seconds >= minimum && seconds <= maximum)
  return choices.some(([seconds]) => seconds === current) ? choices : [...choices, [current, `${cadenceShortText(current)} (sınır dışı)`] as [number, string]].sort((a, b) => a[0] - b[0])
}

/** Why the flow runs at a different interval than its own this pass, or null. */
export function cadenceNote(cadence: SyncCadence) {
  if (cadence.idleActive) return cadence.idleKind === 'WEBHOOK' ? 'Bildirimler sağlıklı; seyrek okunuyor' : cadence.idleKind === 'RETURNS' ? 'İadeler sakin; seyrek okunuyor' : 'Yeni soru yok; seyrek okunuyor'
  if (cadence.stageFloor) return 'Test ortamı en çok 10 dakikada bir okunur'
  return null
}

/** What the idle rule of a flow does, for the editor. */
export function idleRuleText(kind: SyncCadence['idleKind'], platformCode?: string) {
  if (kind === 'RETURNS') return platformCode === 'HEPSIBURADA'
    ? 'Bildirimler sağlıklıyken açık iade varsa en çok 30 dakikada, yoksa seyrek sıklıkta okunur; bildirimler aksarsa açık iade yokken bir gün sessizlikten sonra 20 dakikaya, bir hafta sonra 30 dakikaya uzar. Açık iade varken kendi sıklığında okunur.'
    : 'Açık iade varken kendi sıklığında okunur. Açık iade yokken bir gün yeni iade gelmezse 20 dakikaya, bir hafta gelmezse seyrek sıklığa uzar; yeni iade gelince kendi sıklığına döner.'
  return kind === 'WEBHOOK'
    ? 'Pazaryeri bildirimleri sağlıklı geldikçe bu okuma yalnız güvenlik ağıdır ve seyrek sıklıkta çalışır; bildirim aksarsa hemen kendi sıklığına döner.'
    : 'Yeni soru gelmedikçe aralık her yarım saatte ikiye katlanır ve seyrek sıklıkta durur; yeni soru gelince kendi sıklığına döner.'
}

/**
 * The toast after the Faturalama switch is saved. It follows what the server stored; when that is not what was asked
 * for it says so instead of a "kapatıldı" success (N11 used to accept "on" and keep "off").
 */
export function invoiceCreationFeedback(requested: boolean, saved: boolean) {
  if (requested !== saved)
    return { kind: 'error' as const, message: requested ? 'Fatura oluşturma açılamadı: ayar sunucuda kapalı kaldı. Sayfayı yenileyip tekrar deneyin; sorun sürerse destek ekibine bildirin.' : 'Fatura oluşturma kapatılamadı: ayar sunucuda açık kaldı. Sayfayı yenileyip tekrar deneyin.' }
  return { kind: 'success' as const, message: saved ? 'Bu bağlantı için fatura oluşturma açıldı.' : 'Bu bağlantı için fatura oluşturma kapatıldı; mevcut belgeler ve fatura kayıtları korunuyor.' }
}
