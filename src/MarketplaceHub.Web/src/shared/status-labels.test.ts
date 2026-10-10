import { describe, expect, it } from 'vitest'
import { invoiceStatusLabel, invoiceStatusTone } from './status-labels'

describe('invoice status labels', () => {
  it('keeps review and marketplace delivery failures distinct from rejected invoices', () => {
    expect(invoiceStatusLabel('MANUAL_REVIEW')).toBe('İnceleme gerekli')
    expect(invoiceStatusLabel('MARKETPLACE_FAILED')).toBe('Pazaryeri aktarımı başarısız')
    expect(invoiceStatusLabel('REJECTED')).toBe('Fatura reddedildi')
    expect(invoiceStatusTone('MANUAL_REVIEW')).toBe('warning')
  })
})
