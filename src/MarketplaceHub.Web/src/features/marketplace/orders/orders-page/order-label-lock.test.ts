import { describe, expect, it } from 'vitest'
import { isMicroExportLabelBlocked, labelBlockedByInvoiceReason, microExportLabelBlockedText, unknownExportLabelBlockedText } from './order-page-helpers'

const marks = { microExport: false, microExportCountryCode: null, deliveryPoint: false, deliveryPointType: null, fastDeliveryType: null }

// No cargo label before the invoice: a micro export waits for its export invoice, and (review D-1, 27 Nov 2026) an order
// whose export status the rule could not determine waits for the owner's invoice review.
describe('cargo label lock before the invoice', () => {
  it('blocks a micro-export order and an unknown one while the invoice is pending', () => {
    expect(labelBlockedByInvoiceReason({ isMicroExport: true, invoiceStatus: 'FATURA_BEKLIYOR', marks: { ...marks, microExport: true } })).toBe(microExportLabelBlockedText)
    expect(labelBlockedByInvoiceReason({ isMicroExport: false, invoiceStatus: 'FATURA_INCELENECEK', marks: { ...marks, microExportUnknownReason: 'micro yok' } })).toBe(unknownExportLabelBlockedText)
    expect(unknownExportLabelBlockedText).toContain('ihracat durumu belirlenemedi')
    expect(unknownExportLabelBlockedText).toContain('fatura incelenmeden')
    expect(isMicroExportLabelBlocked({ isMicroExport: false, invoiceStatus: 'FATURA_BEKLIYOR', marks: { ...marks, microExportUnknownReason: 'micro yok' } })).toBe(true)
  })

  it('lets a domestic order, or any order whose invoice is done, print', () => {
    expect(labelBlockedByInvoiceReason({ isMicroExport: false, invoiceStatus: 'FATURA_BEKLIYOR', marks })).toBeNull()
    expect(labelBlockedByInvoiceReason({ isMicroExport: false, invoiceStatus: 'FATURA_BEKLIYOR', marks: null })).toBeNull()
    expect(labelBlockedByInvoiceReason({ isMicroExport: false, invoiceStatus: 'FATURA_KESILDI', marks: { ...marks, microExportUnknownReason: 'micro yok' } })).toBeNull()
    expect(labelBlockedByInvoiceReason({ isMicroExport: true, invoiceStatus: 'FATURA_KESILDI', marks: { ...marks, microExport: true } })).toBeNull()
  })
})
