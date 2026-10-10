import { describe, expect, it } from 'vitest'
import { invoiceReferenceDocumentHref } from './invoice-reference-document-link'

describe('invoiceReferenceDocumentHref', () => {
  it('opens the current marketplace invoice link when it is a safe HTTPS URL', () => {
    expect(invoiceReferenceDocumentHref({
      invoiceId: 'invoice-1',
      invoiceDocumentAvailable: true,
      marketplaceInvoiceUrl: 'https://documents.example.test/current.pdf',
    })).toBe('https://documents.example.test/current.pdf')
  })

  it('falls back to the local PDF when the marketplace link is missing or unsafe', () => {
    const item = { invoiceId: 'invoice/1', invoiceDocumentAvailable: true }
    expect(invoiceReferenceDocumentHref(item)).toBe('/api/v1/invoices/invoice%2F1/documents/latest/content')
    expect(invoiceReferenceDocumentHref({ ...item, marketplaceInvoiceUrl: 'javascript:alert(1)' }))
      .toBe('/api/v1/invoices/invoice%2F1/documents/latest/content')
    expect(invoiceReferenceDocumentHref({ ...item, marketplaceInvoiceUrl: 'https://localhost/invoice.pdf' }))
      .toBe('/api/v1/invoices/invoice%2F1/documents/latest/content')
  })

  it('does not render a document action when no usable document exists', () => {
    expect(invoiceReferenceDocumentHref({ invoiceId: 'invoice-1', invoiceDocumentAvailable: false })).toBeNull()
  })
})
