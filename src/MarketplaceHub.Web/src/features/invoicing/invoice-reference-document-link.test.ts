import { describe, expect, it } from 'vitest'
import { invoiceReferenceMarketplaceHref, platformInvoiceLinkUnavailable, safeInvoiceReferenceUrl } from './invoice-reference-document-link'

describe('invoiceReferenceMarketplaceHref', () => {
  it('opens the current marketplace invoice link when it is a safe HTTPS URL', () => {
    expect(invoiceReferenceMarketplaceHref({
      marketplaceInvoiceUrl: 'https://documents.example.test/current.pdf',
      platformInvoiceStatus: 'INVOICED',
    })).toBe('https://documents.example.test/current.pdf')
  })

  it('prefers the current platform invoice link over the stored marketplace link', () => {
    expect(invoiceReferenceMarketplaceHref({
      marketplaceInvoiceUrl: 'https://marketplace.example.test/old.pdf',
      currentPlatformInvoiceUrl: 'https://marketplace.example.test/current.pdf',
      platformInvoiceStatus: 'INVOICED',
    })).toBe('https://marketplace.example.test/current.pdf')
  })

  it('does not treat a stale marketplace URL as an invoice link when platform status is pending', () => {
    const item = { marketplaceInvoiceUrl: 'https://marketplace.example.test/old.pdf', platformInvoiceStatus: 'NOT_INVOICED' }
    expect(invoiceReferenceMarketplaceHref(item)).toBeNull()
    expect(platformInvoiceLinkUnavailable(item)).toBe(false)
  })

  it('reports an invoiced order whose marketplace exposes no usable link', () => {
    expect(invoiceReferenceMarketplaceHref({ platformInvoiceStatus: 'INVOICED' })).toBeNull()
    expect(platformInvoiceLinkUnavailable({ platformInvoiceStatus: 'INVOICED' })).toBe(true)
    expect(platformInvoiceLinkUnavailable({ platformInvoiceStatus: 'INVOICED', marketplaceInvoiceUrl: 'javascript:alert(1)' })).toBe(true)
    expect(platformInvoiceLinkUnavailable({ platformInvoiceStatus: 'INVOICED', currentPlatformInvoiceUrl: 'https://marketplace.example.test/current.pdf' })).toBe(false)
  })
})

describe('safeInvoiceReferenceUrl', () => {
  it('accepts a public HTTPS URL and rejects unsafe or private ones', () => {
    expect(safeInvoiceReferenceUrl(' https://documents.example.test/manual.pdf ')).toBe('https://documents.example.test/manual.pdf')
    expect(safeInvoiceReferenceUrl('javascript:alert(1)')).toBeNull()
    expect(safeInvoiceReferenceUrl('http://documents.example.test/manual.pdf')).toBeNull()
    expect(safeInvoiceReferenceUrl('https://localhost/invoice.pdf')).toBeNull()
    expect(safeInvoiceReferenceUrl('https://192.168.1.10/invoice.pdf')).toBeNull()
    expect(safeInvoiceReferenceUrl('https://user:pass@documents.example.test/manual.pdf')).toBeNull()
    expect(safeInvoiceReferenceUrl('')).toBeNull()
    expect(safeInvoiceReferenceUrl(null)).toBeNull()
  })
})
