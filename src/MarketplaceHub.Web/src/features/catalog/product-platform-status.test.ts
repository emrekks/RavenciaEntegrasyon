import { describe, expect, it } from 'vitest'
import { productPlatformDisplayLabel, productPlatformDisplayState } from './product-platform-status'

describe('product platform list status', () => {
  it('does not present full variant links as a published listing', () => {
    const state = productPlatformDisplayState(['LINKED'], false)

    expect(state).toBe('inactive')
    expect(productPlatformDisplayLabel('Ravencia Canlı', ['LINKED'], 35, 35, state))
      .toBe('35/35 varyant eşleşmesi var; yayın durumu doğrulanmadı')
  })

  it('shows a confirmed live listing as active', () => {
    const state = productPlatformDisplayState(['LIVE'], false)

    expect(state).toBe('active')
    expect(productPlatformDisplayLabel('Trendyol', ['LIVE'], 35, 35, state))
      .toBe('Trendyol üzerinde tüm varyantlar yayında (35/35)')
  })

  it('shows partial publication only when the marketplace reports live variants', () => {
    const state = productPlatformDisplayState(['LIVE', 'UNKNOWN'], false)

    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Trendyol', ['LIVE', 'UNKNOWN'], 1, 2, state))
      .toBe('Trendyol üzerinde ürün kısmen yayında (1/2)')
  })

  it('does not let a rejected sibling hide a confirmed live listing', () => {
    const statuses = ['LIVE', 'CREATE_REJECTED']
    const state = productPlatformDisplayState(statuses, false)

    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Trendyol', statuses, 35, 70, state))
      .toBe('Trendyol üzerinde ürün kısmen yayında (35/70)')
  })

  it('keeps unmatched variants neutral until publication is confirmed', () => {
    expect(productPlatformDisplayState(['UNLINKED'], false)).toBe('inactive')
    expect(productPlatformDisplayState(['BATCH_IN_PROGRESS'], false)).toBe('processing')
    expect(productPlatformDisplayState(['CREATE_REJECTED'], false)).toBe('error')
  })
})
