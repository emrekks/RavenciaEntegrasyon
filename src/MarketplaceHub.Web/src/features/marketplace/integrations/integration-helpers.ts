// Small pure pieces of the integrations pages, kept here so they can be tested on their own.

/** The body of PUT /connections/{id}/credential for the platform's credential form. */
export function credentialRequestBody(platformCode: string, data: FormData) {
  if (platformCode === 'TRENDYOL_EFATURAM') return { email: data.get('email') || null, password: data.get('password') || null }
  if (platformCode === 'SHOPIFY') return { shopifyAccessToken: data.get('shopifyAccessToken') }
  if (platformCode === 'HEPSIBURADA') return { hepsiburadaIntegratorUsername: data.get('hepsiburadaIntegratorUsername'), hepsiburadaServiceKey: data.get('hepsiburadaServiceKey') }
  return { apiKey: data.get('apiKey'), apiSecret: data.get('apiSecret') }
}

/** The environment in Turkish; Shopify, N11 and Pazarama connect only to the live store. */
export function connectionEnvironmentLabel(platformCode: string, environment: string | null | undefined) {
  if (platformCode === 'SHOPIFY' || platformCode === 'N11' || platformCode === 'PAZARAMA') return 'Canlı'
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
export type IntegrationPlatformFilter = 'ALL' | 'TRENDYOL' | 'TRENDYOL_EFATURAM' | 'SHOPIFY' | 'HEPSIBURADA' | 'N11' | 'PAZARAMA'
export type IntegrationListState = { view: IntegrationView; filter: IntegrationStatusFilter; platform: IntegrationPlatformFilter }

export const integrationPlatformFilters: readonly IntegrationPlatformFilter[] = ['ALL', 'TRENDYOL', 'TRENDYOL_EFATURAM', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA']
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
