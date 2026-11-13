import { describe, expect, it } from 'vitest'
import { invoiceWorkspaceStatus, type InvoiceWorkspaceStatusInput } from './invoice-workspace-status'
import { invoiceStatusLabel } from '../../shared/status-labels'

const row = (patch: Partial<InvoiceWorkspaceStatusInput>): InvoiceWorkspaceStatusInput => ({ invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: false, platformInvoiceStatus: 'NOT_INVOICED', invoiceErrorCode: null, ...patch })

describe('invoiceWorkspaceStatus', () => {
  it('keeps an invoice that needs correcting off green while the marketplace still reports it invoiced', () => {
    const status = invoiceWorkspaceStatus(row({ invoiceStatus: 'FATURA_DUZELTME_GEREKLI', platformInvoiceStatus: 'INVOICED' }))
    expect(status).toEqual({ value: 'FATURA_DUZELTME_GEREKLI', tone: 'warning' })
    expect(invoiceStatusLabel(status.value)).toBe('Fatura Düzeltme Gerekli')
  })

  it('shows a failed invoice as failed even when the marketplace reports an invoice', () => {
    expect(invoiceWorkspaceStatus(row({ invoiceStatus: 'FATURA_PLATFORMA_AKTARILMADI', platformInvoiceStatus: 'INVOICED' }))).toEqual({ value: 'FATURA_PLATFORMA_AKTARILMADI', tone: 'danger' })
    expect(invoiceWorkspaceStatus(row({ invoiceStatus: 'FATURA_REDDEDILDI', platformInvoiceStatus: 'RECEIVED' })).tone).toBe('danger')
  })

  it('reads a marketplace invoice as present for an otherwise pending order', () => {
    expect(invoiceWorkspaceStatus(row({ invoiceStatus: 'FATURA_BEKLIYOR', canCreateInvoice: true, platformInvoiceStatus: 'INVOICED' }))).toEqual({ value: 'FATURA_KONTROLDE', tone: 'success' })
  })

  it('keeps a failed invoice that can be created again as pending', () => {
    expect(invoiceWorkspaceStatus(row({ invoiceStatus: 'FATURA_REDDEDILDI', canCreateInvoice: true })).value).toBe('FATURA_BEKLIYOR')
  })

  it('falls back to unknown for an empty status and does not guess', () => {
    expect(invoiceWorkspaceStatus(row({ invoiceStatus: '', platformInvoiceStatus: null }))).toEqual({ value: 'FATURA_BILINMIYOR', tone: 'info' })
  })
})
