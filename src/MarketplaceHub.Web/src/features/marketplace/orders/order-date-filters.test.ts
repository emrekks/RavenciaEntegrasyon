import { describe, expect, it } from 'vitest'
import { isCancelTab, tabDateFilters } from './order-date-filters'

const both = { dateFrom: '2026-11-01', dateTo: '2026-11-02', cancelFrom: '2026-10-30', cancelTo: '2026-10-31' }

describe('order date filters by tab', () => {
  it('offers İptal Tarihi only in İptal and Kısmi İptal', () => {
    expect(['CANCELLED', 'PARTIALLY_CANCELLED'].every(isCancelTab)).toBe(true)
    expect(['ALL', 'NEW', 'PROCESSING', 'SHIPPED', 'DELIVERED', 'RESENT'].some(isCancelTab)).toBe(false)
  })

  it('keeps only the date filter the tab offers, so no hidden date narrows the list', () => {
    expect(tabDateFilters('CANCELLED', both)).toEqual({ dateFrom: '', dateTo: '', cancelFrom: '2026-10-30', cancelTo: '2026-10-31' })
    expect(tabDateFilters('PARTIALLY_CANCELLED', both)).toEqual({ dateFrom: '', dateTo: '', cancelFrom: '2026-10-30', cancelTo: '2026-10-31' })
    expect(tabDateFilters('NEW', both)).toEqual({ dateFrom: '2026-11-01', dateTo: '2026-11-02', cancelFrom: '', cancelTo: '' })
    expect(tabDateFilters('ALL', both)).toEqual({ dateFrom: '2026-11-01', dateTo: '2026-11-02', cancelFrom: '', cancelTo: '' })
  })

  it('leaves a tab change between two cancel tabs, or two other tabs, as it was', () => {
    const cancelOnly = { dateFrom: '', dateTo: '', cancelFrom: '2026-10-30', cancelTo: '2026-10-30' }
    expect(tabDateFilters('PARTIALLY_CANCELLED', cancelOnly)).toEqual(cancelOnly)
    const orderOnly = { dateFrom: '2026-11-01', dateTo: '', cancelFrom: '', cancelTo: '' }
    expect(tabDateFilters('SHIPPED', orderOnly)).toEqual(orderOnly)
  })
})
