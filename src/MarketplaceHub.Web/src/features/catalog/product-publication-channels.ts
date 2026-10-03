export type ProductPublicationChannelMode = 'PUBLISH' | 'READ_ONLY'

export function productPublicationChannelMode(platformCode: string, status: string): ProductPublicationChannelMode | null {
  if (!['ACTIVE', 'VERIFIED'].includes(status.trim().toUpperCase())) return null

  const code = platformCode.trim().toUpperCase()
  if (code === 'TRENDYOL' || code === 'HEPSIBURADA') return 'PUBLISH'
  if (code === 'SHOPIFY') return 'READ_ONLY'
  return null
}
