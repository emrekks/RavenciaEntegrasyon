import { describe, expect, it } from 'vitest'
import { withProductPlatformSaleState } from './product-platform-status'
import { publishStatusSteps } from './product-editor/publish-status-steps'
import { isOffSale, offSaleSummary, saleReasonLabel, saleStateLabel } from './sale-state'

describe('sale state', () => {
  it('names the three states, unknown included', () => {
    expect(saleStateLabel('ON_SALE')).toBe('Satışta')
    expect(saleStateLabel('OFF_SALE')).toBe('Satışa Kapalı')
    expect(saleStateLabel(null)).toBe('Bilinmiyor')
    expect(saleStateLabel('SOMETHING')).toBe('Bilinmiyor')
    expect(saleReasonLabel('STOCK_ZERO')).toBe('Stok 0')
    expect(saleReasonLabel(null)).toBeNull()
    expect(isOffSale('OFF_SALE')).toBe(true)
    expect(isOffSale(null)).toBe(false)
  })

  it('summarizes closed variants among the matched ones, and nothing when none is known closed', () => {
    expect(offSaleSummary(4, 0)).toBeNull()
    expect(offSaleSummary(0, 2)).toBeNull()
    expect(offSaleSummary(4, 4)).toMatchObject({ allClosed: true, stockOnly: false, label: 'Satışa Kapalı' })
    expect(offSaleSummary(4, 4, 4)).toMatchObject({ allClosed: true, stockOnly: true, label: 'Satışa Kapalı · Stok 0' })
    expect(offSaleSummary(4, 1)).toMatchObject({ allClosed: false, label: '1/4 varyant satışa kapalı' })
    expect(offSaleSummary(4, 2, 2)).toMatchObject({ allClosed: false, label: '2/4 varyant satışa kapalı (stok 0)' })
  })

  it('shows a live listing closed to sale as Satışa Kapalı, not Yayında, on every marketplace', () => {
    for (const platform of ['Trendyol', 'Hepsiburada', 'n11', 'Pazarama', 'PTT AVM', 'Shopify']) {
      const closed = withProductPlatformSaleState(platform, 'active', `${platform} üzerinde tüm varyantlar yayında (3/3)`, offSaleSummary(3, 3))
      expect(closed).toEqual({ state: 'closed', label: `${platform}: Satışa Kapalı (3/3 varyant)` })
    }
    const some = withProductPlatformSaleState('Trendyol', 'active', 'Trendyol üzerinde tüm varyantlar yayında (3/3)', offSaleSummary(3, 1))
    expect(some).toEqual({ state: 'active', label: 'Trendyol üzerinde tüm varyantlar yayında (3/3); 1/3 varyant satışa kapalı' })
  })

  it('leaves the publication state alone when the sale state is unknown or the card is not there', () => {
    expect(withProductPlatformSaleState('Trendyol', 'active', 'x', null)).toEqual({ state: 'active', label: 'x' })
    expect(withProductPlatformSaleState('Trendyol', 'processing', 'x', offSaleSummary(2, 2))).toEqual({ state: 'processing', label: 'x' })
    expect(withProductPlatformSaleState('Trendyol', 'missing', 'x', offSaleSummary(2, 2))).toEqual({ state: 'missing', label: 'x' })
  })

  it('keeps the publish steps done and reads Satışa Kapalı on a live card closed to sale', () => {
    const base = { requiredGaps: 0, optionalWarnings: 0, live: true, fallbackLabel: '' }
    expect(publishStatusSteps(base).label).toBe('Yayında')
    const closed = publishStatusSteps({ ...base, saleClosedLabel: 'Satışa Kapalı' })
    expect(closed.label).toBe('Satışa Kapalı')
    expect(closed.steps).toEqual(['done', 'done', 'done', 'done'])
  })
})
