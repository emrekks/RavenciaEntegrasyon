import { describe, expect, it } from 'vitest'
import { invoiceCompletionOutcome } from './invoice-completion'

describe('invoice completion', () => {
  it('waits for marketplace confirmation after provider acceptance', () => {
    expect(invoiceCompletionOutcome('ACCEPTED')).toBe('pending')
    expect(invoiceCompletionOutcome('MARKETPLACE_PENDING')).toBe('pending')
    expect(invoiceCompletionOutcome('SUBMITTED')).toBe('pending')
  })

  it('completes only after the delivery pipeline confirms the invoice', () => {
    expect(invoiceCompletionOutcome('COMPLETED')).toBe('complete')
    expect(invoiceCompletionOutcome('completed')).toBe('complete')
  })

  it('does not treat delivery errors as successful invoice creation', () => {
    expect(invoiceCompletionOutcome('MANUAL_REVIEW')).toBe('failed')
    expect(invoiceCompletionOutcome('MARKETPLACE_FAILED')).toBe('failed')
    expect(invoiceCompletionOutcome('REJECTED')).toBe('failed')
  })
})
