// Small pure pieces of the integrations pages, kept here so they can be tested on their own.

/** Owner decision B: what switching the automatic writes (or the master switch) on means for the changes made while off. */
export const automaticWritesBaselineWarning = 'Kapalıyken yapılan değişiklikler pazaryerine gönderilmedi; stok ve fiyatları elle kontrol edin.'

export function automaticWritesBaselineMessage(externalWriteTo: string) {
  return `${automaticWritesBaselineWarning} Açıldığı andaki fiyat ve stoklar gönderilmiş sayılır; bundan sonraki değişiklikler ${externalWriteTo} otomatik gönderilir.`
}

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
}

export type PriceStockMonitor = { generatedAt: string; connections: PriceStockConnectionMonitor[] }

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
