import { describe, expect, it } from 'vitest'
import { invoiceFailureGuidance, invoiceFailureReason } from './invoice-failure-reasons'

describe('invoice failure reasons', () => {
  it('explains the fiscal gates that stop an invoice before it reaches the provider', () => {
    expect(invoiceFailureReason('EINVOICE_RECIPIENT_REQUIRES_EFATURA')).toContain('e-Fatura mükellefi')
    expect(invoiceFailureReason('INVOICE_EXPORT_FLOW_REQUIRED')).toContain('Mikro ihracat')
    expect(invoiceFailureReason('INVOICE_ADJUSTMENT_FLOW_UNAVAILABLE')).toContain('İade/düzeltme')
    expect(invoiceFailureReason('EFATURAM_RECIPIENT_TAX_ID_INVALID')).toContain('kontrol hanesi')
    expect(invoiceFailureGuidance('EINVOICE_RECIPIENT_REQUIRES_EFATURA')).toContain('manuel fatura')
  })

  it('explains provider refusals that left nothing at the provider', () => {
    expect(invoiceFailureReason('EFATURAM_SIGNIN_UNAVAILABLE')).toContain('fatura gönderilmedi')
    expect(invoiceFailureReason('EFATURAM_OPERATION_FORBIDDEN')).toContain('yetkili değil')
    expect(invoiceFailureReason('CAPABILITY_NOT_VERIFIED')).toContain('izni kapalı')
  })

  it('keeps the raw code visible for codes without a text', () => {
    expect(invoiceFailureReason('SOMETHING_NEW')).toBe('İletilemedi: SOMETHING_NEW')
    expect(invoiceFailureReason(null)).toBe('Ayrıntılar için faturayı açın.')
  })
})
