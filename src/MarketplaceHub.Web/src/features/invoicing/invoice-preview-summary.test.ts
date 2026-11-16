import { describe, expect, it } from 'vitest'
import { formatMoney } from '../../shared/format-money'
import { invoicePreviewSummary } from './invoice-preview-summary'

describe('invoicePreviewSummary', () => {
  it('counts every order read, the ones the approval invoices with their total, and the refused ones', () => {
    const text = invoicePreviewSummary([
      { canConfirm: true, payableTotal: 100.1, currency: 'TRY' },
      { canConfirm: true, payableTotal: 0.2, currency: 'TRY' },
      { canConfirm: false, payableTotal: 999, currency: 'TRY' }
    ])
    expect(text).toBe(`3 sipariş okundu · 2 sipariş faturalanacak · Fatura Toplamı ${formatMoney(100.3, 'TRY')} · 1 sipariş faturalanamaz`)
  })
  it('keeps currencies apart and names no total when nothing can be invoiced', () => {
    expect(invoicePreviewSummary([{ canConfirm: true, payableTotal: 10, currency: 'TRY' }, { canConfirm: true, payableTotal: 5, currency: 'EUR' }]))
      .toBe(`2 sipariş okundu · 2 sipariş faturalanacak · Fatura Toplamı ${formatMoney(10, 'TRY')} + ${formatMoney(5, 'EUR')}`)
    expect(invoicePreviewSummary([{ canConfirm: false, payableTotal: 10, currency: 'TRY' }])).toBe('1 sipariş okundu · 0 sipariş faturalanacak · 1 sipariş faturalanamaz')
  })
})
