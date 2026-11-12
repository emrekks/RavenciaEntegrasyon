import { describe, expect, it } from 'vitest'
import { connectionFullResetItems, connectionFullResetRunning, connectionFullResetSupported } from './connection-data-reset'

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
