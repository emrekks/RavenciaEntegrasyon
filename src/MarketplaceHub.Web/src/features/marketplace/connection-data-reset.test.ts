import { describe, expect, it } from 'vitest'
import { connectionDataResetGroups, connectionDataResetOptions, connectionFullResetItems, connectionFullResetRunning, connectionFullResetSupported } from './connection-data-reset'

describe('connection full reset', () => {
  it('lists every count the server reports, invoices included', () => {
    expect(connectionFullResetItems.map(item => item.scope)).toEqual([
      'ORDERS', 'RETURNS', 'INVOICES', 'QUESTIONS', 'PRODUCTS', 'PRODUCT_LINKS', 'FINANCE', 'REFERENCES', 'MAPPINGS', 'COMMISSION_TARIFFS', 'PROMOTION_OFFERS', 'JOBS'
    ])
    expect(connectionFullResetItems.find(item => item.scope === 'INVOICES')?.description).toContain('Kesilmiş faturalar dahil')
  })

  it('is offered on marketplaces, not on the e-invoice connection', () => {
    expect(connectionFullResetSupported('TRENDYOL')).toBe(true)
    expect(connectionFullResetSupported('PTTAVM')).toBe(true)
    expect(connectionFullResetSupported('TRENDYOL_EFATURAM')).toBe(false)
  })

  it('treats a queued, running or retrying job as running', () => {
    expect(['PENDING', 'LEASED', 'RETRY_SCHEDULED'].every(connectionFullResetRunning)).toBe(true)
    expect(['SUCCEEDED', 'DEAD', 'BLOCKED', null].some(connectionFullResetRunning)).toBe(false)
  })
})

describe('connection data reset options', () => {
  it('offers marketplace data only for the selected connection type', () => {
    expect(connectionDataResetOptions('TRENDYOL').map(option => option.scope)).toEqual([
      'ORDERS', 'RETURNS', 'INVOICES', 'PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES', 'BRANDS', 'COMMISSION_TARIFFS', 'PROMOTION_OFFERS'
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
      { label: 'Sipariş ve Finans', scopes: ['ORDERS', 'RETURNS', 'INVOICES'] },
      { label: 'Ürün Kataloğu', scopes: ['PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES'] }
    ])
  })
})
