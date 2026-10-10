import { describe, expect, it } from 'vitest'
import { invoiceProviderForEnvironment, invoiceSubmissionAction, isInvoiceCreationAvailable, isValidatedInvoiceReadyToSubmit, matchesInvoiceActionFilter } from './invoice-creation-availability'

describe('invoice creation availability', () => {
  it('selects only a credentialed provider from the marketplace environment', () => {
    const providers = [
      { id: 'stage', platformCode: 'TRENDYOL_EFATURAM', environment: 'STAGE', status: 'ACTIVE', hasCredential: true },
      { id: 'production', platformCode: 'TRENDYOL_EFATURAM', environment: 'PRODUCTION', status: 'VERIFIED', hasCredential: true }
    ]
    expect(invoiceProviderForEnvironment(providers, 'production')?.id).toBe('production')
    expect(invoiceProviderForEnvironment(providers, 'STAGE')?.id).toBe('stage')
    expect(invoiceProviderForEnvironment(providers.slice(0, 1), 'PRODUCTION')).toBeUndefined()
  })

  it('rejects providers without credentials, an active status, or a known environment', () => {
    const base = { id: 'provider', platformCode: 'TRENDYOL_EFATURAM', environment: 'PRODUCTION', status: 'ACTIVE', hasCredential: true }
    expect(invoiceProviderForEnvironment([{ ...base, hasCredential: false }], 'PRODUCTION')).toBeUndefined()
    expect(invoiceProviderForEnvironment([{ ...base, status: 'DRAFT' }], 'PRODUCTION')).toBeUndefined()
    expect(invoiceProviderForEnvironment([base], 'UNKNOWN')).toBeUndefined()
  })

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

  it('shows a delivery-only retry for an issued invoice that was not confirmed at the marketplace', () => {
    const issuedButNotDelivered = { platformCode: 'TRENDYOL', invoiceId: 'invoice-1', invoiceStatus: 'FATURA_PLATFORMA_AKTARILMADI', canCreateInvoice: false, invoiceCreationEnabled: true }
    expect(isInvoiceCreationAvailable(issuedButNotDelivered, true)).toBe(true)
    expect(matchesInvoiceActionFilter(issuedButNotDelivered, 'CREATABLE', true)).toBe(true)
    expect(isInvoiceCreationAvailable({ ...issuedButNotDelivered, invoiceCreationEnabled: false }, false)).toBe(true)
  })

  it('revalidates a failed invoice and never queues it while validation still fails', () => {
    expect(invoiceSubmissionAction('VALIDATION_FAILED', ['VALIDATE'])).toBe('VALIDATE')
    expect(invoiceSubmissionAction('REJECTED', ['SUBMIT'])).toBe('SUBMIT')
    expect(isValidatedInvoiceReadyToSubmit('VALIDATION_FAILED', ['VALIDATE'])).toBe(false)
    expect(isValidatedInvoiceReadyToSubmit('READY', ['SUBMIT'])).toBe(true)
  })
})
