import { describe, expect, it } from 'vitest'
import { dataResetSummary } from './data-reset-summary'

const result = { products: 1200, orders: 35, returns: 4, invoices: 9, categories: 2, categoryAttributes: 0, brands: 1, options: 3 }

describe('data reset message', () => {
  it('lists the count of every picked scope, orders, returns and invoices included', () => {
    expect(dataResetSummary(result, ['ORDERS', 'RETURNS', 'INVOICES', 'PRODUCTS'])).toBe('Temizlik tamamlandı: 35 sipariş, 4 iade, 9 fatura, 1.200 ürün.')
  })

  it('leaves out scopes that were not picked', () => {
    expect(dataResetSummary(result, ['BRANDS'])).toBe('Temizlik tamamlandı: 1 marka.')
  })
})
