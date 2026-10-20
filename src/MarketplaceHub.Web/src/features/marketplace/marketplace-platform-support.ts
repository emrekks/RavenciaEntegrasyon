export const orderMarketplacePlatformCodes = ['TRENDYOL', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA'] as const

export type MarketplacePlatformOption = { value: string; label: string }
export type MarketplacePlatformOptionSource = { platformCode: string; displayName?: string | null; status?: string | null }

const marketplacePlatformLabels: Record<typeof orderMarketplacePlatformCodes[number], string> = {
  TRENDYOL: 'Trendyol',
  SHOPIFY: 'Shopify',
  HEPSIBURADA: 'Hepsiburada',
  N11: 'N11',
  PAZARAMA: 'Pazarama'
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

// n11 flows join phase by phase.
const n11ReadSyncPolicies = new Set(['ORDERS', 'ORDER_RECOVERY', 'ORDER_RECONCILE_SHORT', 'ORDER_RECONCILE_MEDIUM', 'ORDER_RECONCILE_DAILY', 'RETURNS', 'QUESTIONS', 'REFERENCE_DATA', 'STOCK_RECONCILE_SHORT', 'STOCK_RECONCILE_MEDIUM', 'STOCK_RECONCILE_DAILY', 'PRICE_WRITE', 'STOCK_WRITE', 'SHIPMENT_WRITE', 'RETURN_WRITE'])

// Pazarama: the order reads (new orders, history, open-order status scan); its write flows join with their phases.
const pazaramaReadSyncPolicies = new Set(['ORDERS', 'ORDER_RECOVERY', 'ORDER_LIFECYCLE', 'REFERENCE_DATA', 'PRICE_WRITE', 'STOCK_WRITE', 'RETURNS', 'QUESTIONS', 'SHIPMENT_WRITE', 'RETURN_WRITE'])

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
      // A passive (draft, disabled, hidden) store neither gets an option nor lends its name to one: a passive
      // "SIT Hepsiburada" connection used to rename the Hepsiburada option.
      const status = item.status?.trim().toUpperCase()
      if (status && status !== 'ACTIVE' && status !== 'VERIFIED') continue
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
  return platformCode === 'TRENDYOL' || platformCode === 'SHOPIFY' || platformCode === 'HEPSIBURADA' || platformCode === 'N11' || platformCode === 'PAZARAMA'
}

export function supportsSyncPolicy(platformCode: string, resourceType: string) {
  if (!supportsSyncPolicyManagement(platformCode)) return false
  if (platformCode === 'N11') return n11ReadSyncPolicies.has(resourceType)
  if (platformCode === 'PAZARAMA') return pazaramaReadSyncPolicies.has(resourceType)
  return platformCode !== 'HEPSIBURADA' || hepsiburadaReadSyncPolicies.has(resourceType)
}

// A check interval in the largest whole unit: 21600 → "6 saat", 2700 → "45 dakika", 90 → "90 saniye".
export function syncIntervalText(seconds: number) {
  if (seconds > 0 && seconds % 86400 === 0) return `${seconds / 86400} gün`
  if (seconds > 0 && seconds % 3600 === 0) return `${seconds / 3600} saat`
  if (seconds > 0 && seconds % 60 === 0) return `${seconds / 60} dakika`
  return `${seconds} saniye`
}

export function syncPolicyIntervalChoices(intervals: Array<[number, string]>, requiresExternalWrites: boolean) {
  return intervals.filter(([seconds]) => requiresExternalWrites || seconds >= 30)
}
