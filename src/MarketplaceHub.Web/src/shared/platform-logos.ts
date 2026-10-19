const platformLogoSources: Record<string, string> = {
  N11: '/platforms/n11.webp',
  PTTAVM: '/platforms/pttavm.webp',
  PAZARAMA: '/platforms/pazarama.webp',
  TRENDYOL: '/platforms/trendyol.webp',
  TRENDYOL_EFATURAM: '/platforms/trendyol-efaturam.webp',
  HEPSIBURADA: '/platforms/hepsiburada.webp',
  SHOPIFY: '/platforms/shopify.webp',
}

export function platformLogoSource(platformCode: string | null | undefined) {
  const normalized = platformCode?.trim().toUpperCase() ?? ''
  return platformLogoSources[normalized] ?? null
}

const orderPlatformLogoSources: Record<string, string> = {
  N11: '/platforms/orders/n11.webp',
  PTTAVM: '/platforms/orders/pttavm.webp',
  PAZARAMA: '/platforms/orders/pazarama.webp',
  TRENDYOL: '/platforms/orders/trendyol.webp',
  HEPSIBURADA: '/platforms/orders/hepsiburada.webp',
  SHOPIFY: '/platforms/orders/shopify.webp',
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
