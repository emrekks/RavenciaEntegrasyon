import { describe, expect, it } from 'vitest'
import { isInvoiceCreationAvailable, matchesInvoiceActionFilter } from './invoice-creation-availability'

describe('invoice creation availability', () => {
  it('marks Trendyol rows unavailable when invoice creation or the provider credential is locked', () => {
    const eligible = { platformCode: 'TRENDYOL', invoiceId: null, invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: true, invoiceCreationEnabled: true }
    expect(isInvoiceCreationAvailable(eligible, false)).toBe(false)
    expect(isInvoiceCreationAvailable({ ...eligible, invoiceCreationEnabled: false }, true)).toBe(false)
    expect(matchesInvoiceActionFilter(eligible, 'NOT_CREATABLE', false)).toBe(true)
    expect(matchesInvoiceActionFilter(eligible, 'CREATABLE', false)).toBe(false)
  })

  it('marks eligible Shopify and Trendyol rows available', () => {
    const eligible = { platformCode: 'TRENDYOL', invoiceId: null, invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: true, invoiceCreationEnabled: true }
    expect(isInvoiceCreationAvailable(eligible, true)).toBe(true)
    expect(isInvoiceCreationAvailable({ ...eligible, platformCode: 'SHOPIFY' }, false)).toBe(true)
  })

  it('permits an enabled retry for failed Trendyol invoices, but not completed records', () => {
    const failed = { platformCode: 'TRENDYOL', invoiceId: 'invoice-1', invoiceStatus: 'MARKETPLACE_FAILED', canCreateInvoice: false, invoiceCreationEnabled: true }
    expect(isInvoiceCreationAvailable(failed, true)).toBe(true)
    expect(isInvoiceCreationAvailable({ ...failed, invoiceStatus: 'COMPLETED' }, true)).toBe(false)
  })
})
