import { describe, expect, it } from 'vitest'
import { returnedLineText, saleReturnTitle, workspaceInvoiceStatusFilterLabel } from './invoice-copy'
import { invoiceGlyphKind, invoiceStatusProgress } from './invoice-state-glyph'
import { fullyReturnedNoInvoiceStatus, invoiceStatusLabel } from '../../shared/status-labels'

// Owner's rule of 6 Nov 2026: returned units never go on a sale invoice not issued yet.
describe('returns before an order is invoiced', () => {
  it('names an order returned in full as one that gets no invoice', () => {
    expect(invoiceStatusLabel(fullyReturnedNoInvoiceStatus)).toBe('İade Edildi, Fatura Kesilmez')
    expect(workspaceInvoiceStatusFilterLabel(fullyReturnedNoInvoiceStatus)).toBe('İade Edildi, Fatura Kesilmez')
    expect(invoiceGlyphKind(fullyReturnedNoInvoiceStatus)).toBe('cancelled')
    expect(invoiceStatusProgress(fullyReturnedNoInvoiceStatus)).toBe(1)
    expect(saleReturnTitle('FULL')).toBe('Fatura Kesilmez')
    expect(saleReturnTitle('PARTIAL')).toBe('Kısmi İade')
  })

  it('tells which units of a line the invoice leaves out', () => {
    expect(returnedLineText({ quantity: 3 })).toBeNull()
    expect(returnedLineText({ quantity: 3, returnedQuantity: 0 })).toBeNull()
    expect(returnedLineText({ quantity: 3, returnedQuantity: 1 })).toBe('1 adet iade edildi · Faturaya 2 adet girer')
    expect(returnedLineText({ quantity: 2, returnedQuantity: 2 })).toBe('Tümü iade edildi · Faturaya girmez')
  })
})
