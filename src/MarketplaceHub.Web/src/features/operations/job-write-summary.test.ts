import { describe, expect, it } from 'vitest'
import { isProductLevelJob, writeAttributeNames, writeFieldLabels, writeModeLabel, writePartCount, writePhaseLabel, writeRequestLabel, type JobWritePart } from './job-write-summary'

const part = (overrides: Partial<JobWritePart>): JobWritePart => ({ key: 'CONTENT', itemCount: 1, fields: [], barcodes: [], contentCount: 0, attributes: [], ...overrides })

describe('job write summary', () => {
  it('names the fields in Turkish without the identifiers, once each', () => {
    expect(writeFieldLabels(['contentId', 'title', 'description', 'images', 'attributes', 'deliveryOption', 'deliveryDuration', 'barcode', 'customField']))
      .toEqual(['Başlık', 'Açıklama', 'Görseller', 'Özellikler', 'Teslim Süresi', 'customField'])
  })

  it('names an attribute it has no name for by its id', () => {
    expect(writeAttributeNames([{ id: '338', name: 'Beden' }, { id: '47', name: null }])).toEqual(['Beden', 'Özellik #47'])
  })

  it('counts what a request carried and says when it was skipped', () => {
    expect(writePartCount(part({ itemCount: 0 }))).toBe('Gönderilecek kalem yoktu; istek yapılmadı')
    expect(writePartCount(part({ itemCount: 6, contentCount: 6 }))).toBe('6 içerik kartı')
    expect(writePartCount(part({ key: 'UNAPPROVED', itemCount: 18, barcodes: Array.from({ length: 18 }, (_, i) => `B-${i}`) }))).toBe('18 barkod')
  })

  it('labels the mode, the running step and the request ids', () => {
    expect(writeModeLabel('APPROVED')).toBe('Onaylı ürün: içerik, varyant ve teslimat ayrı isteklerle')
    expect(writeModeLabel(null)).toBeNull()
    expect(writePhaseLabel('POLL_CONTENT')).toBe('İçerik sonucu bekleniyor')
    expect(writePhaseLabel('SUBMIT_DELIVERY')).toBe('Teslimat gönderilecek')
    expect(writeRequestLabel('PRODUCT')).toBe('Ürün İsteği No')
    expect(writeRequestLabel('POLL_VARIANTS')).toBe('Varyant İsteği No')
  })

  it('tells product card jobs from store-wide reads', () => {
    expect(isProductLevelJob('PRODUCT_UPDATE')).toBe(true)
    expect(isProductLevelJob('product_approval_reconcile')).toBe(true)
    expect(isProductLevelJob('PRODUCT_SYNC')).toBe(false)
  })
})
