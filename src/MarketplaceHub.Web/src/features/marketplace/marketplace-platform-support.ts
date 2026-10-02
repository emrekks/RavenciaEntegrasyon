export const orderMarketplacePlatformCodes = ['TRENDYOL', 'SHOPIFY', 'HEPSIBURADA'] as const

export type MarketplacePlatformOption = { value: string; label: string }
export type MarketplacePlatformOptionSource = { platformCode: string; displayName?: string | null }

const marketplacePlatformLabels: Record<typeof orderMarketplacePlatformCodes[number], string> = {
  TRENDYOL: 'Trendyol',
  SHOPIFY: 'Shopify',
  HEPSIBURADA: 'Hepsiburada'
}

const hepsiburadaReadSyncPolicies = new Set([
  'ORDERS',
  'ORDER_RECOVERY',
  'ORDER_LIFECYCLE',
  'ORDER_INVOICE_RECONCILIATION',
  'RETURNS',
  'REFERENCE_DATA',
  'PRICE_WRITE',
  'STOCK_WRITE',
  'SHIPMENT_WRITE',
  'RETURN_WRITE'
])

export function isOrderMarketplacePlatform(platformCode: string) {
  return orderMarketplacePlatformCodes.includes(normalizeMarketplacePlatformCode(platformCode) as typeof orderMarketplacePlatformCodes[number])
}

export function marketplacePlatformLabel(platformCode: string) {
  const normalizedCode = normalizeMarketplacePlatformCode(platformCode)
  return marketplacePlatformLabels[normalizedCode as typeof orderMarketplacePlatformCodes[number]] ?? (platformCode.trim() || platformCode)
}

export function normalizeMarketplacePlatformCode(platformCode: string) {
  return platformCode.trim().toUpperCase()
}

export function isActiveMarketplaceConnection(connection: { platformCode: string; status: string }) {
  const status = connection.status.trim().toUpperCase()
  return isOrderMarketplacePlatform(connection.platformCode) && (status === 'ACTIVE' || status === 'VERIFIED')
}

export function marketplacePlatformOptions(...sources: ReadonlyArray<readonly MarketplacePlatformOptionSource[]>): MarketplacePlatformOption[] {
  const options = new Map<string, MarketplacePlatformOption>(orderMarketplacePlatformCodes.map(platformCode => [
    platformCode,
    { value: platformCode, label: marketplacePlatformLabels[platformCode] }
  ]))

  for (const source of sources) {
    for (const item of source) {
      const rawCode = item.platformCode.trim()
      if (!rawCode) continue
      const normalizedCode = normalizeMarketplacePlatformCode(rawCode)
      if (normalizedCode === 'TRENDYOL_EFATURAM') continue
      const value = isOrderMarketplacePlatform(normalizedCode) ? normalizedCode : rawCode
      options.set(value, {
        value,
        label: item.displayName?.trim() || marketplacePlatformLabels[normalizedCode as typeof orderMarketplacePlatformCodes[number]] || rawCode
      })
    }
  }

  return Array.from(options.values()).sort((left, right) => left.label.localeCompare(right.label, 'tr-TR'))
}

export function isMarketplacePlatformSelected(selectedCodes: string[] | null, platformCode: string) {
  if (!selectedCodes?.length) return true
  const normalizedCode = normalizeMarketplacePlatformCode(platformCode)
  return selectedCodes.some(selectedCode => normalizeMarketplacePlatformCode(selectedCode) === normalizedCode)
}

export function supportsSyncPolicyManagement(platformCode: string) {
  return platformCode === 'TRENDYOL' || platformCode === 'SHOPIFY' || platformCode === 'HEPSIBURADA'
}

export function supportsSyncPolicy(platformCode: string, resourceType: string) {
  if (!supportsSyncPolicyManagement(platformCode)) return false
  return platformCode !== 'HEPSIBURADA' || hepsiburadaReadSyncPolicies.has(resourceType)
}

export function syncPolicyIntervalChoices(intervals: Array<[number, string]>, requiresExternalWrites: boolean) {
  return intervals.filter(([seconds]) => requiresExternalWrites || seconds >= 30)
}
