export const orderMarketplacePlatformCodes = ['TRENDYOL', 'SHOPIFY', 'HEPSIBURADA'] as const

const hepsiburadaReadSyncPolicies = new Set([
  'ORDERS',
  'ORDER_RECOVERY',
  'ORDER_INVOICE_RECONCILIATION',
  'RETURNS'
])

export function isOrderMarketplacePlatform(platformCode: string) {
  return orderMarketplacePlatformCodes.includes(platformCode as typeof orderMarketplacePlatformCodes[number])
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
