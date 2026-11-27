import { describe, expect, it } from 'vitest'
import { invoiceFailureGuidance, invoiceFailureReason } from './invoice-failure-reasons'

describe('invoice failure reasons', () => {
  it('explains the fiscal gates that stop an invoice before it reaches the provider', () => {
    expect(invoiceFailureReason('EINVOICE_RECIPIENT_REQUIRES_EFATURA')).toContain('e-Fatura mükellefi')
    expect(invoiceFailureReason('INVOICE_EXPORT_FLOW_REQUIRED')).toContain('Mikro ihracat')
    expect(invoiceFailureReason('INVOICE_ADJUSTMENT_FLOW_UNAVAILABLE')).toBe('Panel iade ya da düzeltme faturası kesmez.')
    expect(invoiceFailureReason('EFATURAM_RECIPIENT_TAX_ID_INVALID')).toContain('kontrol hanesi')
    expect(invoiceFailureGuidance('EINVOICE_RECIPIENT_REQUIRES_EFATURA')).toContain('manuel fatura')
    expect(invoiceFailureReason('EINVOICE_RECIPIENT_REVIEW_REQUIRED')).toContain('mükellef sorgusu yapılamadı')
    expect(invoiceFailureReason('EINVOICE_TARGET_ALIAS_UNRESOLVED')).toContain('etiket')
    expect(invoiceFailureReason('EINVOICE_TAXPAYER_CONFLICT')).toContain('kesilmedi')
    expect(invoiceFailureReason('EINVOICE_TAXPAYER_STATUS_CHANGED')).toContain('gönderilmedi')
    expect(invoiceFailureGuidance('EINVOICE_RECIPIENT_REVIEW_REQUIRED')).toContain('manuel fatura')
    // 16 Nov 2026: an unclear Hepsiburada buyer, and a company VKN sent to 11111111111, stop the invoice.
    expect(invoiceFailureReason('EFATURAM_RECIPIENT_KIND_UNKNOWN')).toContain('fatura kesilmedi')
    expect(invoiceFailureReason('EFATURAM_RECIPIENT_PLACEHOLDER_FOR_CORPORATE')).toContain('11111111111')
    expect(invoiceFailureGuidance('EFATURAM_RECIPIENT_KIND_UNKNOWN')).toContain('manuel belge')
    expect(invoiceFailureGuidance('EFATURAM_RECIPIENT_PLACEHOLDER_FOR_CORPORATE')).toContain('manuel belge')
    // 27 Nov 2026: an order whose micro-export kind is unknown waits; its type is not guessed.
    expect(invoiceFailureReason('INVOICE_MICRO_EXPORT_UNKNOWN')).toContain('tahmin edilmedi')
    expect(invoiceFailureGuidance('INVOICE_MICRO_EXPORT_UNKNOWN')).toContain('manuel belge')
  })

  it('explains provider refusals that left nothing at the provider', () => {
    expect(invoiceFailureReason('EFATURAM_SIGNIN_UNAVAILABLE')).toContain('fatura gönderilmedi')
    expect(invoiceFailureReason('EFATURAM_OPERATION_FORBIDDEN')).toContain('yetkili değil')
    expect(invoiceFailureReason('CAPABILITY_NOT_VERIFIED')).toContain('izni kapalı')
  })

  it('says an invoice number or ETTN another invoice holds stopped the invoice without sending it again (VERITABANI-DAGITIM-11)', () => {
    expect(invoiceFailureReason('INVOICE_PROVIDER_IDENTITY_TAKEN')).toContain('yeniden gönderilmedi')
  })

  it('says why an issued invoice was not sent to the marketplace by itself', () => {
    for (const code of ['PLATFORM_UNSUPPORTED', 'LINK_MISSING', 'WRITES_OFF', 'CONNECTION_NOT_LIVE', 'CONNECTION_WRITES_OFF', 'PACKAGE_MISSING', 'SHOPIFY_SCOPE_MISSING'])
      expect(invoiceFailureReason(`INVOICE_AUTO_DELIVERY_${code}`)).toMatch(/^Pazaryerine gönderilmedi: /)
    expect(invoiceFailureReason('INVOICE_AUTO_DELIVERY_CONNECTION_WRITES_OFF')).toContain('dış yazma kapalı')
  })

  it('keeps the raw code visible for codes without a text', () => {
    expect(invoiceFailureReason('SOMETHING_NEW')).toBe('İletilemedi: SOMETHING_NEW')
    expect(invoiceFailureReason(null)).toBe('Ayrıntılar için faturayı açın.')
  })
})
