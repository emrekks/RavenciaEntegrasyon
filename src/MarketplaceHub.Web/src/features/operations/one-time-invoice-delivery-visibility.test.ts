import { describe, expect, it } from 'vitest'
import { hasEligibleOneTimeInvoiceSourceFailure } from './one-time-invoice-delivery-visibility'
import { oneTimeInvoiceDeliveryIdempotencyKey } from './one-time-invoice-delivery-idempotency'

describe('oneTimeInvoiceDeliveryIdempotencyKey', () => {
  it('uses a fresh versioned key for the repaired stage delivery attempt', () => {
    expect(oneTimeInvoiceDeliveryIdempotencyKey('job-123')).toBe('one-time-invoice-delivery:stage-test-v2:job-123')
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
