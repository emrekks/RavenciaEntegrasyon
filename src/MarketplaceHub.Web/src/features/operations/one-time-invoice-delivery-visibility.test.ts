import { describe, expect, it } from 'vitest'
import { canOfferOneTimeInvoiceDelivery, hasEligibleOneTimeInvoiceSourceFailure } from './one-time-invoice-delivery-visibility'
import { oneTimeInvoiceDeliveryIdempotencyKey } from './one-time-invoice-delivery-idempotency'

describe('oneTimeInvoiceDeliveryIdempotencyKey', () => {
  it('uses a fresh versioned key for the repaired stage delivery attempt', () => {
    expect(oneTimeInvoiceDeliveryIdempotencyKey('job-123')).toBe('one-time-invoice-delivery:stage-test-v2:job-123')
  })
})

describe('canOfferOneTimeInvoiceDelivery', () => {
  const candidate = {
    elevated: true,
    jobType: 'INVOICE_MARKETPLACE_DELIVERY',
    jobStatus: 'DEAD',
    lastErrorCode: 'HEPSIBURADA_CAPABILITY_NOT_ENABLED',
    attempts: [],
    invoice: { status: 'Accepted', invoiceNumber: 'RVN2026000000001', externalPackageId: 'pkg-1' }
  }

  it('no longer depends on a hard-coded order number (the server decides which orders qualify)', () => {
    expect(canOfferOneTimeInvoiceDelivery(candidate)).toBe(true)
  })

  it('still needs an elevated user, a finished failed delivery and a complete invoice', () => {
    expect(canOfferOneTimeInvoiceDelivery({ ...candidate, elevated: false })).toBe(false)
    expect(canOfferOneTimeInvoiceDelivery({ ...candidate, jobStatus: 'LEASED' })).toBe(false)
    expect(canOfferOneTimeInvoiceDelivery({ ...candidate, invoice: { ...candidate.invoice, externalPackageId: null } })).toBe(false)
    expect(canOfferOneTimeInvoiceDelivery({ ...candidate, invoice: null })).toBe(false)
  })
})

describe('hasEligibleOneTimeInvoiceSourceFailure', () => {
  it('shows the one-time action when the latest error proves no write', () => {
    expect(hasEligibleOneTimeInvoiceSourceFailure('HEPSIBURADA_CAPABILITY_NOT_ENABLED', [])).toBe(true)
  })

  it('keeps the one-time action available when a later guard error follows a no-write attempt', () => {
    expect(hasEligibleOneTimeInvoiceSourceFailure('DELIVERY_ALREADY_FAILED', [
      { succeeded: false, errorCode: 'DELIVERY_ALREADY_FAILED' },
      { succeeded: false, errorCode: 'HEPSIBURADA_CAPABILITY_NOT_ENABLED' },
    ])).toBe(true)
  })

  it('hides the one-time action when the later guard has no recorded no-write attempt', () => {
    expect(hasEligibleOneTimeInvoiceSourceFailure('DELIVERY_ALREADY_FAILED', [
      { succeeded: false, errorCode: 'DELIVERY_ALREADY_FAILED' },
    ])).toBe(false)
  })

  it('does not allow unrelated remote failures to use the one-time action', () => {
    expect(hasEligibleOneTimeInvoiceSourceFailure('REMOTE_REJECTED', [
      { succeeded: false, errorCode: 'HEPSIBURADA_CAPABILITY_NOT_ENABLED' },
    ])).toBe(false)
  })
})
