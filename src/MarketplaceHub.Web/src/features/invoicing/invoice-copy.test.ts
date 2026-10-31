import { describe, expect, it } from 'vitest'
import { handSetInvoiceStatusPlatforms, invoiceAwaitsProviderAnswer, invoiceConfirmResultLabel, invoiceReopenNote, invoiceDetailActionLabel, invoiceEnvironmentLabel, invoiceErrorText, invoiceTypeLabel, manualDeliveryIdempotencyKey, taxpayerStatusLabel, providerAnswerAwaitedStatus, workspaceInvoiceStatusFilterLabel } from './invoice-copy'
import { invoiceGlyphKind } from './invoice-state-glyph'

import { invoiceStatusLabel, invoiceStatusTone } from '../../shared/status-labels'

describe('invoice copy', () => {
  it('names the taxpayer lookup result and shows nothing when no lookup ran', () => {
    expect(taxpayerStatusLabel('REGISTERED')).toBe('e-Fatura Mükellefi')
    expect(taxpayerStatusLabel('NOT_REGISTERED')).toBe('e-Fatura Mükellefi Değil')
    expect(taxpayerStatusLabel('UNKNOWN')).toBe('Sorgulanamadı')
    expect(taxpayerStatusLabel(null)).toBeNull()
  })

  it('names the delivery action after the marketplace, not Trendyol', () => {
    expect(invoiceDetailActionLabel('DELIVER')).toBe('Pazaryerine Fatura Linkini İlet')
    expect(invoiceDetailActionLabel('CANCEL')).toBe('E-Arşiv İptal İsteği Gönder')
    expect(invoiceDetailActionLabel('SOME_NEW_ACTION')).toBe('Some new action')
  })

  it('shows an unreadable E-Faturam answer as awaited, not as an uncertain result', () => {
    expect(invoiceAwaitsProviderAnswer('FATURA_BELIRSIZ', 'EFATURAM_SUBMIT_ANSWER_UNREADABLE')).toBe(true)
    expect(invoiceAwaitsProviderAnswer('UNKNOWN_RESULT', 'EFATURAM_SUBMIT_ANSWER_UNREADABLE')).toBe(true)
    expect(invoiceAwaitsProviderAnswer('FATURA_BELIRSIZ', 'INVOICE_SUBMISSION_TRANSPORT_UNKNOWN')).toBe(false)
    expect(invoiceStatusLabel(providerAnswerAwaitedStatus)).toBe('E-Faturam Cevap Bekleniyor')
    expect(invoiceStatusTone(providerAnswerAwaitedStatus)).toBe('warning')
    expect(invoiceGlyphKind(providerAnswerAwaitedStatus)).toBe('sending')
  })

  it('says per marketplace what "Fatura Bekliyor" does to an order whose invoice was cancelled', () => {
    expect(invoiceReopenNote(['TRENDYOL', 'SHOPIFY'])).toBeNull()
    expect(invoiceReopenNote(['N11'])).toContain('eskisinin yerine geçer')
    expect(invoiceReopenNote(['PAZARAMA'])).not.toContain('PTT AVM')
    expect(invoiceReopenNote(['PTTAVM'])).toContain('PTT AVM satıcı panelinde')
    expect(invoiceReopenNote(['PTTAVM'])).not.toContain('n11 ve Pazarama')
    expect([...handSetInvoiceStatusPlatforms].sort()).toEqual(['N11', 'PAZARAMA', 'PTTAVM'])
  })

  it('offers a refused or stalled e-Archive cancellation again', () => {
    expect(invoiceDetailActionLabel('CANCEL', 'CANCELLATION_REJECTED')).toBe('E-Arşiv İptalini Yeniden İste')
    expect(invoiceDetailActionLabel('CANCEL', 'CANCELLATION_PENDING')).toBe('E-Arşiv İptalini Yeniden İste')
    expect(invoiceDetailActionLabel('CANCEL', 'COMPLETED')).toBe('E-Arşiv İptal İsteği Gönder')
  })

  it('shows Turkish text for the preview codes', () => {
    expect(invoiceEnvironmentLabel('PRODUCTION')).toBe('Canlı')
    expect(invoiceEnvironmentLabel('STAGE')).toBe('Test')
    expect(invoiceTypeLabel('EARSIVFATURA')).toBe('e-Arşiv Fatura')
    expect(invoiceTypeLabel('TEMELFATURA')).toBe('e-Fatura (Temel)')
    expect(invoiceConfirmResultLabel('QUEUED')).toBe('Sırada')
    expect(invoiceConfirmResultLabel('CONFLICT')).toBe('Eşzamanlı Değişiklik')
  })

  it('explains a known error code and labels an unknown one', () => {
    expect(invoiceErrorText('EFATURAM_OPERATION_FORBIDDEN')).toContain('yetkili değil')
    expect(invoiceErrorText('SOMETHING_NEW')).toBe('Hata kodu: SOMETHING_NEW')
    expect(invoiceErrorText(null)).toBe('Yok')
  })

  it('offers the adjustment state as "Düzeltme Gerekiyor" in the filter', () => {
    expect(workspaceInvoiceStatusFilterLabel('FATURA_DUZELTME_GEREKLI')).toBe('Düzeltme Gerekiyor')
    expect(workspaceInvoiceStatusFilterLabel('FATURA_BEKLIYOR')).toBe('Fatura Bekliyor')
    expect(invoiceStatusLabel('FATURA_DUZELTME_GEREKLI')).toBe('Fatura Düzeltme Gerekli')
    expect(invoiceStatusTone('FATURA_DUZELTME_GEREKLI')).toBe('warning')
  })

  it('gives every manual delivery attempt its own key, so a retry after a second failure is queued again', () => {
    const first = manualDeliveryIdempotencyKey('doc-1')
    const second = manualDeliveryIdempotencyKey('doc-1')
    expect(first).toMatch(/^manual-invoice-delivery:doc-1:[0-9a-f-]{36}$/)
    expect(second).not.toBe(first)
  })
})
