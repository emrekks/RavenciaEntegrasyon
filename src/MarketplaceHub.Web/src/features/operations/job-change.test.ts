import { describe, expect, it } from 'vitest'
import { fallbackJobChange, jobRelatedLinks } from './job-change'

describe('fallbackJobChange (P2/P3)', () => {
  it('says a product publication, update or archive was sent to the marketplace', () => {
    for (const type of ['PRODUCT_CREATE', 'PRODUCT_UPDATE', 'PRODUCT_ARCHIVE']) {
      expect(fallbackJobChange(type).detail).toContain('pazaryerine gönderildi')
      expect(fallbackJobChange(type).detail).not.toContain('gönderilmedi')
    }
  })

  it('says a product read sent nothing', () => {
    for (const type of ['PRODUCT_SYNC', 'SHOPIFY_PRODUCT_SYNC', 'HEPSIBURADA_PRODUCT_SYNC', 'PRODUCT_APPROVAL_RECONCILE', 'CATALOG_STRUCTURE_IMPORT'])
      expect(fallbackJobChange(type).detail).toContain('gönderilmedi')
  })

  it('names the invoice sent to the marketplace only on a delivery', () => {
    expect(fallbackJobChange('INVOICE_MARKETPLACE_DELIVERY').detail).toBe('Fatura isteği pazaryerine gönderildi.')
    expect(fallbackJobChange('MANUAL_ORDER_INVOICE_DELIVERY').detail).toBe('Fatura isteği pazaryerine gönderildi.')
    for (const type of ['INVOICE_DUE_SCAN', 'INVOICE_RECONCILE', 'INVOICE_DOCUMENT_FETCH', 'INVOICE_SUBMIT', 'INVOICE_CANCELLATION', 'ORDER_INVOICE_RECONCILIATION'])
      expect(fallbackJobChange(type).detail).not.toContain('pazaryerine gönderildi')
    expect(fallbackJobChange('INVOICE_DUE_SCAN').detail).toContain('Panel içi tarama')
    expect(fallbackJobChange('INVOICE_RECONCILE').detail).toContain('E-Faturam’dan okundu')
    expect(fallbackJobChange('INVOICE_DOCUMENT_FETCH').detail).toContain('E-Faturam’dan okundu')
  })

  it('reads a reference sync by its marketplace', () => {
    expect(fallbackJobChange('PTTAVM_REFERENCE_SYNC').value).toBe('PTT AVM kategori verisi')
    expect(fallbackJobChange('TRENDYOL_REFERENCE_SYNC').value).toBe('Trendyol kategori ve eşleştirme verisi')
  })
})

describe('jobRelatedLinks', () => {
  it('links the product, the invoice and the order of a job', () => {
    expect(jobRelatedLinks({ productId: 'p-1', invoice: { invoiceId: 'i-1', orderNumber: '42' }, order: { orderNumber: '42' }, relatedOrders: [] })).toEqual([
      { href: '/products/p-1', label: 'Ürüne Git' },
      { href: '/invoices/i-1', label: 'Faturaya Git' },
      { href: '/orders?search=42', label: 'Sipariş #42' }
    ])
  })

  it('has no link for a job about no record', () => {
    expect(jobRelatedLinks({ productId: null, invoice: null, order: null, relatedOrders: [] })).toEqual([])
  })
})
