import { describe, expect, it } from 'vitest'
import { familyPublicationWarnings, productPublicationTargets } from './product-publication-submit'

describe('family publication warnings', () => {
  it('names the marketplace that refused every colour card and says it got no job', () => {
    const failures = new Map([['Birden fazla varyant Shopify\'da ancak renk, beden gibi bir seçenekle ayrılabilir.', [0, 1, 2, 3, 4, 5, 6, 7]]])
    expect(familyPublicationWarnings('Shopify', { enqueued: 0, skipped: 0, failures })).toEqual([
      'Shopify: Birden fazla varyant Shopify\'da ancak renk, beden gibi bir seçenekle ayrılabilir. (8 renk kartında)',
      'Shopify için yayın işi açılamadı.'
    ])
  })

  it('says when every card was left as it was instead of skipping silently', () => {
    expect(familyPublicationWarnings('Shopify', { enqueued: 0, skipped: 8, failures: new Map() })).toEqual([
      'Shopify: yeni yayın işi açılmadı; 8 renk kartı zaten yayında, eşleşmiş veya yayın işi sürüyor.'
    ])
  })

  it('names a single refused card by its place and stays quiet when jobs were queued', () => {
    expect(familyPublicationWarnings('n11', { enqueued: 7, skipped: 0, failures: new Map([['Kategori eksik.', [2]]]) })).toEqual(['n11: Kategori eksik. (3. renk kartı)'])
    expect(familyPublicationWarnings('Pazarama', { enqueued: 8, skipped: 0, failures: new Map() })).toEqual([])
    expect(familyPublicationWarnings('PttAVM', { enqueued: 2, skipped: 6, failures: new Map() })).toEqual([])
  })
})

describe('product publication submit targets', () => {
  it('does not enqueue marketplace jobs for a panel-only save', () => {
    expect(productPublicationTargets(true, ['trendyol-connection'])).toEqual([])
  })

  it('keeps selected marketplace targets for an explicit publish/update action', () => {
    expect(productPublicationTargets(false, ['trendyol-connection'])).toEqual(['trendyol-connection'])
  })
})
