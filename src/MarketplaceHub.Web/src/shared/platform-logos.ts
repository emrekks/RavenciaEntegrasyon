const platformLogoSources: Record<string, string> = {
  N11: '/platforms/n11.png',
  PTTAVM: '/platforms/pttavm.png',
  PAZARAMA: '/platforms/pazarama.png',
  TRENDYOL: '/platforms/trendyol.png',
  TRENDYOL_EFATURAM: '/platforms/trendyol-efaturam.png',
  HEPSIBURADA: '/platforms/hepsiburada.png',
  SHOPIFY: '/platforms/shopify.png',
}

export function platformLogoSource(platformCode: string | null | undefined) {
  const normalized = platformCode?.trim().toUpperCase() ?? ''
  return platformLogoSources[normalized] ?? null
}

const orderPlatformLogoSources: Record<string, string> = {
  N11: '/platforms/orders/n11.png',
  PTTAVM: '/platforms/orders/pttavm.png',
  PAZARAMA: '/platforms/orders/pazarama.png',
  TRENDYOL: '/platforms/orders/trendyol.png',
  HEPSIBURADA: '/platforms/orders/hepsiburada.png',
  SHOPIFY: '/platforms/orders/shopify.png',
}

// Order cards retain the compact artwork set; catalog and integration views
// intentionally use the refreshed marketplace artwork.
export function orderPlatformLogoSource(platformCode: string | null | undefined) {
  const normalized = platformCode?.trim().toUpperCase() ?? ''
  return orderPlatformLogoSources[normalized] ?? platformLogoSource(platformCode)
}

export function platformLogoClass(platformCode: string | null | undefined) {
  const normalized = platformCode?.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-') ?? 'unknown'
  return `platform-logo-${normalized}`
}
