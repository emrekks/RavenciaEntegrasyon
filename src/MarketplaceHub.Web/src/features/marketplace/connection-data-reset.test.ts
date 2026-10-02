import { describe, expect, it } from 'vitest'
import { connectionDataResetOptions } from './connection-data-reset'

describe('connection data reset options', () => {
  it('offers marketplace data only for the selected connection type', () => {
    expect(connectionDataResetOptions('TRENDYOL').map(option => option.scope)).toEqual([
      'ORDERS', 'RETURNS', 'INVOICES', 'PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES', 'BRANDS'
    ])
    expect(connectionDataResetOptions('HEPSIBURADA').map(option => option.scope)).toEqual([
      'ORDERS', 'RETURNS', 'INVOICES', 'PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES'
    ])
    expect(connectionDataResetOptions('SHOPIFY').map(option => option.scope)).toEqual([
      'ORDERS', 'RETURNS', 'INVOICES', 'PRODUCTS'
    ])
  })

  it('limits an e-invoice connection to its invoice data', () => {
    expect(connectionDataResetOptions('TRENDYOL_EFATURAM').map(option => option.scope)).toEqual(['INVOICES'])
  })
})
