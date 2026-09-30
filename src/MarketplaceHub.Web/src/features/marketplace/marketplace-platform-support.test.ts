import { describe, expect, it } from 'vitest'
import { isOrderMarketplacePlatform, supportsSyncPolicy, supportsSyncPolicyManagement, syncPolicyIntervalChoices } from './marketplace-platform-support'

describe('Hepsiburada marketplace workspace support', () => {
  it('includes Hepsiburada in order platform filters', () => {
    expect(isOrderMarketplacePlatform('HEPSIBURADA')).toBe(true)
    expect(isOrderMarketplacePlatform('TRENDYOL')).toBe(true)
    expect(isOrderMarketplacePlatform('TRENDYOL_EFATURAM')).toBe(false)
  })

  it('exposes only supported read-only Hepsiburada synchronization intervals', () => {
    expect(supportsSyncPolicyManagement('HEPSIBURADA')).toBe(true)
    expect(['ORDERS', 'ORDER_RECOVERY', 'ORDER_INVOICE_RECONCILIATION', 'RETURNS'].every(resource => supportsSyncPolicy('HEPSIBURADA', resource))).toBe(true)
    expect(supportsSyncPolicy('HEPSIBURADA', 'ORDER_LIFECYCLE')).toBe(false)
    expect(supportsSyncPolicy('HEPSIBURADA', 'PRICE_WRITE')).toBe(false)
  })

  it('does not offer immediate runs for read-only sync policies', () => {
    const intervals: Array<[number, string]> = [[0, 'Anında'], [30, '30 saniye'], [60, '1 dakika']]

    expect(syncPolicyIntervalChoices(intervals, false)).toEqual([[30, '30 saniye'], [60, '1 dakika']])
    expect(syncPolicyIntervalChoices(intervals, true)).toEqual(intervals)
  })
})
