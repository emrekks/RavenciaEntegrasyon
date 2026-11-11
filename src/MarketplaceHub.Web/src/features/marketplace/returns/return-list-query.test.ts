import { describe, expect, it } from 'vitest'
import { effectiveReturnSort, returnListParams, returnSortOptions, returnSummaryParams } from './return-list-query'

const none = { platforms: [], cargo: 'ALL', reason: 'ALL', invoiceStatus: 'ALL' }

describe('returns list requests', () => {
  it('sends every filter of the page to the server', () => {
    const params = returnSummaryParams({ status: 'APPROVED', search: '  Ayşe ', from: '', to: '' }, { platforms: ['TRENDYOL', 'N11'], cargo: 'Aras Kargo', reason: 'SIZE', invoiceStatus: 'FATURA_KONTROLDE' })

    expect(Object.fromEntries(params)).toEqual({ status: 'APPROVED', search: 'Ayşe', platforms: 'TRENDYOL,N11', cargo: 'Aras Kargo', reasonKey: 'SIZE', invoiceStatus: 'FATURA_KONTROLDE' })
  })

  it('leaves out filters set to all and adds the dates as Istanbul day boundaries', () => {
    const params = returnSummaryParams({ status: 'ALL', search: '', from: '2026-10-01', to: '2026-10-02' }, none)

    expect(params.has('status')).toBe(false)
    expect(params.get('dateFrom')).toBe('2026-10-01T00:00:00.000+03:00')
    expect(params.get('dateTo')).toMatch(/^2026-10-02T23:59:59/)
  })

  it('pages by cursor when one is known and by number otherwise', () => {
    const filters = { status: 'APPROVED', search: '', from: '', to: '' }

    expect(returnListParams(filters, none, { sort: 'ORDERED_ASC', limit: 50, after: 'c-2', page: 2 }).toString()).toBe('status=APPROVED&sort=ORDERED_ASC&limit=50&after=c-2')
    expect(returnListParams(filters, none, { sort: null, limit: 20, after: null, page: 4 }).toString()).toBe('status=APPROVED&limit=20&page=4')
    expect(returnListParams(filters, none, { sort: null, limit: 20, after: null, page: 1 }).toString()).toBe('status=APPROVED&limit=20')
  })

  it('offers the return request and order dates on every tab and the approval date on Onaylanan only', () => {
    expect(returnSortOptions('ALL').map(option => option[1])).toEqual(['En Yeni İade Talebi', 'En Eski İade Talebi', 'En Yeni Sipariş', 'En Eski Sipariş'])
    expect(returnSortOptions('APPROVED').map(option => option[0])).toEqual(['REQUESTED_DESC', 'REQUESTED_ASC', 'ORDERED_DESC', 'ORDERED_ASC', 'APPROVED_DESC', 'APPROVED_ASC'])
    expect(effectiveReturnSort('ACTION_REQUIRED', 'APPROVED_ASC')).toBe('REQUESTED_DESC')
    expect(effectiveReturnSort('APPROVED', 'APPROVED_ASC')).toBe('APPROVED_ASC')
    expect(effectiveReturnSort('ALL', 'ORDERED_ASC')).toBe('ORDERED_ASC')
  })
})
