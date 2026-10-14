import { describe, expect, it } from 'vitest'
import { invoiceReferenceDocumentHref, invoiceReferenceDocumentLabel, invoiceReferenceMarketplaceHref, platformInvoiceLinkUnavailable } from './invoice-reference-document-link'

describe('invoiceReferenceDocumentHref', () => {
  it('opens the current marketplace invoice link when it is a safe HTTPS URL', () => {
    expect(invoiceReferenceDocumentHref({
      invoiceId: 'invoice-1',
      invoiceDocumentAvailable: true,
      marketplaceInvoiceUrl: 'https://documents.example.test/current.pdf',
      platformInvoiceStatus: 'INVOICED',
    })).toBe('https://documents.example.test/current.pdf')
  })

  it('uses a manual invoice document when no current marketplace link exists', () => {
    expect(invoiceReferenceDocumentHref({
      invoiceId: null,
      invoiceDocumentAvailable: false,
      manualInvoiceDocumentUrl: 'https://documents.example.test/manual.pdf',
    })).toBe('https://documents.example.test/manual.pdf')
  })

  it('prefers the current marketplace invoice link over a stored manual document', () => {
    expect(invoiceReferenceDocumentHref({
      invoiceId: 'invoice-1',
      invoiceDocumentAvailable: true,
      marketplaceInvoiceUrl: 'https://marketplace.example.test/current.pdf',
      manualInvoiceDocumentUrl: 'https://documents.example.test/manual.pdf',
      platformInvoiceStatus: 'INVOICED',
    })).toBe('https://marketplace.example.test/current.pdf')
  })

  it('falls back to the local PDF when the marketplace link is missing or unsafe', () => {
    const item = { invoiceId: 'invoice/1', invoiceDocumentAvailable: true }
    expect(invoiceReferenceDocumentHref(item)).toBe('/api/v1/invoices/invoice%2F1/documents/latest/content')
    expect(invoiceReferenceDocumentHref({ ...item, marketplaceInvoiceUrl: 'javascript:alert(1)' }))
      .toBe('/api/v1/invoices/invoice%2F1/documents/latest/content')
    expect(invoiceReferenceDocumentHref({ ...item, marketplaceInvoiceUrl: 'https://localhost/invoice.pdf' }))
      .toBe('/api/v1/invoices/invoice%2F1/documents/latest/content')
    expect(invoiceReferenceDocumentHref({ ...item, marketplaceInvoiceUrl: 'https://localhost/invoice.pdf', manualInvoiceDocumentUrl: 'https://documents.example.test/manual.pdf' }))
      .toBe('https://documents.example.test/manual.pdf')
  })

  it('does not render a document action when no usable document exists', () => {
    expect(invoiceReferenceDocumentHref({ invoiceId: 'invoice-1', invoiceDocumentAvailable: false })).toBeNull()
  })

  it('does not treat a stale marketplace URL as an invoice link when platform status is pending', () => {
    const item = {
      invoiceId: null,
      invoiceDocumentAvailable: false,
      marketplaceInvoiceUrl: 'https://marketplace.example.test/old.pdf',
      platformInvoiceStatus: 'NOT_INVOICED',
    }
    expect(invoiceReferenceMarketplaceHref(item)).toBeNull()
    expect(invoiceReferenceDocumentHref(item)).toBeNull()
    expect(invoiceReferenceDocumentLabel(item)).toBeNull()
  })

  it('opens the platform link first and labels stored documents by their actual source', () => {
    const item = {
      invoiceId: 'invoice-1',
      invoiceDocumentAvailable: true,
      currentPlatformInvoiceUrl: 'https://marketplace.example.test/current.pdf',
      manualInvoiceDocumentUrl: 'https://documents.example.test/manual.pdf',
      platformInvoiceStatus: 'INVOICED',
    }
    expect(invoiceReferenceDocumentHref(item)).toBe('https://marketplace.example.test/current.pdf')
    expect(invoiceReferenceDocumentLabel(item)).toBe('Fatura linki')
    expect(invoiceReferenceMarketplaceHref(item)).toBe('https://marketplace.example.test/current.pdf')
    expect(platformInvoiceLinkUnavailable(item)).toBe(false)
  })

  it('labels local fallback as a local PDF when marketplace says invoiced but exposes no link', () => {
    const item = { invoiceId: 'invoice-1', invoiceDocumentAvailable: true, platformInvoiceStatus: 'INVOICED' }
    expect(invoiceReferenceDocumentHref(item)).toBe('/api/v1/invoices/invoice-1/documents/latest/content')
    expect(invoiceReferenceDocumentLabel(item)).toBe('Yerel PDF')
    expect(invoiceReferenceMarketplaceHref(item)).toBeNull()
    expect(platformInvoiceLinkUnavailable(item)).toBe(true)
  })
})
