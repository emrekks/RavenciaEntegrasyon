import { describe, expect, it } from 'vitest'
import { invoiceStatusLabel, invoiceStatusTone, statusLabel } from './status-labels'

describe('invoice status labels', () => {
  it('keeps review and marketplace delivery failures distinct from rejected invoices', () => {
    expect(invoiceStatusLabel('MANUAL_REVIEW')).toBe('İnceleme gerekli')
    expect(invoiceStatusLabel('MARKETPLACE_FAILED')).toBe('Pazaryeri aktarımı başarısız')
    expect(invoiceStatusLabel('REJECTED')).toBe('Fatura reddedildi')
    expect(invoiceStatusTone('MANUAL_REVIEW')).toBe('warning')
  })

  it('shows a marketplace invoice awaiting reconciliation as issued and successful', () => {
    expect(invoiceStatusLabel('FATURA_KONTROLDE')).toBe('Faturası Mevcut')
    expect(invoiceStatusTone('FATURA_KONTROLDE')).toBe('success')
  })

  it('translates invoice workspace values returned in English', () => {
    expect(statusLabel('None')).toBe('Yok')
    expect(statusLabel('NotInvoiced')).toBe('Fatura kesilmedi')
    expect(statusLabel('Not Sent')).toBe('Gönderilmedi')
  })
})
