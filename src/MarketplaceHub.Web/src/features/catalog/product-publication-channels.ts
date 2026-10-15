export type ProductPublicationChannelMode = 'PUBLISH' | 'READ_ONLY'

export function productPublicationChannelMode(platformCode: string, status: string, externalWritesEnabled = false): ProductPublicationChannelMode | null {
  if (!['ACTIVE', 'VERIFIED'].includes(status.trim().toUpperCase())) return null

  const code = platformCode.trim().toUpperCase()
  if (code === 'TRENDYOL' || code === 'HEPSIBURADA') return 'PUBLISH'
  // n11 has only a live store, so product create (n11 supports create only) waits for the external-write switch like Shopify.
  if (code === 'SHOPIFY' || code === 'N11' || code === 'PAZARAMA') return externalWritesEnabled ? 'PUBLISH' : 'READ_ONLY'
  return null
}
