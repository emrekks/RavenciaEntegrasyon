import { describe, expect, it } from 'vitest'
import { connectionDataResetGroups, connectionDataResetOptions } from './connection-data-reset'

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

  it('groups Hepsiburada reset choices into order-finance and catalog details', () => {
    expect(connectionDataResetGroups('HEPSIBURADA').map(group => ({
      label: group.label,
      scopes: group.options.map(option => option.scope)
    }))).toEqual([
      { label: 'Sipariş ve finans', scopes: ['ORDERS', 'RETURNS', 'INVOICES'] },
      { label: 'Ürün kataloğu', scopes: ['PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES'] }
    ])
  })
})
