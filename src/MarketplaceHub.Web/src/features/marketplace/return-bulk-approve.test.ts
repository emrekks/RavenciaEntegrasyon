import { describe, expect, it } from 'vitest'
import { bulkApproveConfirmText, bulkApproveCounts, bulkApproveFailureText } from './return-bulk-approve'

describe('bulk return approval texts', () => {
  it('counts each order once and adds up the product units', () => {
    expect(bulkApproveConfirmText([
      { platformCode: 'TRENDYOL', orderNumber: '#100', units: 1 },
      { platformCode: 'HEPSIBURADA', orderNumber: '#200', units: 1 }
    ])).toBe('2 sipariş ve 2 ürün onaylanacak. Onaylamak istiyor musunuz?')
    // Two returns of the same order count as one order; their units add up.
    expect(bulkApproveCounts([
      { platformCode: 'TRENDYOL', orderNumber: '#100', units: 2 },
      { platformCode: 'trendyol', orderNumber: '#100 ', units: 1 },
      { platformCode: 'N11', orderNumber: '#100', units: 3 }
    ])).toEqual({ orders: 2, units: 6 })
  })

  it('names the failed orders and a reason they all share', () => {
    expect(bulkApproveFailureText([])).toBe('')
    expect(bulkApproveFailureText([{ orderNumber: '#100', message: 'Yazma kapalı.' }])).toBe('Onaylanamayan sipariş: #100. Neden: Yazma kapalı.')
    expect(bulkApproveFailureText([{ orderNumber: '#100', message: 'Sürüm eski' }, { orderNumber: '#200', message: 'Yazma kapalı' }]))
      .toBe('Onaylanamayan siparişler: #100, #200.')
  })
})
