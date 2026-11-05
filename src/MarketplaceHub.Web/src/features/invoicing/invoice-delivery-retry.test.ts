import { describe, expect, it } from 'vitest'
import { invoiceDeliveryRetryDetail, invoiceDeliveryRetryText, invoiceDetailActionLabel } from './invoice-copy'
import { invoiceFailureGuidance, invoiceFailureReason } from './invoice-failure-reasons'

describe('invoice delivery retry copy', () => {
  it('shows the next attempt in Istanbul time while attempts remain', () => {
    expect(invoiceDeliveryRetryText({ state: 'SCHEDULED', attempts: 1, maxAttempts: 5, nextAttemptAt: '2026-11-02T21:30:00Z', lastErrorCode: 'DOCUMENT_CONTENT_TYPE_INVALID', need: 'FETCH_DOCUMENT' }))
      .toMatch(/^Yeniden denenecek: 03\.11\.2026 00:30/)
    expect(invoiceDeliveryRetryDetail({ state: 'SCHEDULED', attempts: 1, maxAttempts: 5, nextAttemptAt: null, lastErrorCode: 'DOCUMENT_CONTENT_TYPE_INVALID', need: 'FETCH_DOCUMENT' }))
      .toBe('1/5 otomatik deneme · Son hata: Belge alınamadı: E-Faturam PDF yerine başka bir yanıt verdi.')
  })

  it('asks for the manual retry once the attempts are used up', () => {
    expect(invoiceDeliveryRetryText({ state: 'EXHAUSTED', attempts: 5, maxAttempts: 5, nextAttemptAt: null, lastErrorCode: null, need: 'FETCH_DOCUMENT' }))
      .toBe('Denemeler bitti — Tekrar Gönder ile elle gönderin')
    expect(invoiceDeliveryRetryText({ state: 'IN_PROGRESS', attempts: 2, maxAttempts: 5, nextAttemptAt: null, lastErrorCode: null, need: 'DELIVER' })).toBe('Yeniden deneniyor…')
    expect(invoiceDeliveryRetryText(null)).toBeNull()
    expect(invoiceDetailActionLabel('RETRY_DELIVERY')).toBe('Tekrar Gönder')
  })

  // T2-Y4 (5 Nov 2026): an issued invoice the marketplace refused, or one that sent nothing, is sent again by the owner.
  it('offers Tekrar Gönder for a delivery only the owner sends again', () => {
    expect(invoiceDeliveryRetryText({ state: 'MANUAL_ONLY', attempts: 0, maxAttempts: 5, nextAttemptAt: null, lastErrorCode: 'REMOTE_INVOICE_REJECTED', need: 'DELIVER' }))
      .toBe('Pazaryerine iletilmedi — Tekrar Gönder ile gönderin')
    expect(invoiceFailureReason('INVOICE_PRE_ISSUE_ORDER_CHANGED')).toContain('yeniden onayınızı bekliyor')
    expect(invoiceFailureReason('HEPSIBURADA_INVOICE_INFO_MISSING')).toContain('fatura bilgisi')
    expect(invoiceFailureReason('ORDER_LINE_VAT_RATE_UNKNOWN')).toContain('KDV Oranı Bilinmiyor')
    expect(invoiceFailureReason('ORDER_LINE_UNREADABLE')).toContain('Okunamayan Ürün Satırı')
  })

  it('no longer promises a retry in the error text itself', () => {
    expect(invoiceFailureReason('DOCUMENT_CONTENT_TYPE_INVALID')).not.toContain('Yeniden denenecek')
    expect(invoiceFailureReason('DOCUMENT_PDF_SIGNATURE_INVALID')).not.toContain('Yeniden denenecek')
    expect(invoiceFailureGuidance('INVOICE_DOCUMENT_MISSING_NOT_DELIVERED')).toContain('Tekrar Gönder')
    expect(invoiceFailureGuidance('INVOICE_DOCUMENT_MISSING_NOT_DELIVERED')).toContain('yeni fatura kesilmez')
  })
})
