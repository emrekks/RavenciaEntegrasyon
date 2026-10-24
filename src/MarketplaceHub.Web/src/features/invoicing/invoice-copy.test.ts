import { describe, expect, it } from 'vitest'
import { invoiceConfirmResultLabel, invoiceDetailActionLabel, invoiceEnvironmentLabel, invoiceErrorText, invoiceTypeLabel, manualDeliveryIdempotencyKey, workspaceInvoiceStatusFilterLabel } from './invoice-copy'

import { invoiceStatusLabel, invoiceStatusTone } from '../../shared/status-labels'

describe('invoice copy', () => {
  it('names the delivery action after the marketplace, not Trendyol', () => {
    expect(invoiceDetailActionLabel('DELIVER')).toBe('Pazaryerine Fatura Linkini İlet')
    expect(invoiceDetailActionLabel('CANCEL')).toBe('E-Arşiv İptal İsteği Gönder')
    expect(invoiceDetailActionLabel('SOME_NEW_ACTION')).toBe('Some new action')
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
