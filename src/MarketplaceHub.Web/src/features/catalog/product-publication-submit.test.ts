import { describe, expect, it } from 'vitest'
import {
  marketplacePublishLines,
  productPublicationTargets,
  productSaveFeedback,
  publicationCancelMessage,
  publicationRefreshMessage,
  type MarketplacePublishOutcome,
  type ProductSaveOutcome
} from './product-publication-submit'

const publish = (overrides: Partial<MarketplacePublishOutcome>): MarketplacePublishOutcome => ({
  platformName: 'Trendyol',
  platformCode: 'TRENDYOL',
  familyCards: 1,
  enqueuedCards: 0,
  enqueuedVariants: 0,
  skipped: { live: 0, running: 0, matched: 0 },
  failures: new Map(),
  ...overrides
})

const save = (overrides: Partial<ProductSaveOutcome>): ProductSaveOutcome => ({
  created: false,
  productChanged: true,
  savedParts: [],
  versionRetried: false,
  saveWarnings: [],
  publishRequested: true,
  publications: [],
  publicationErrors: [],
  ...overrides
})

describe('marketplace publish lines', () => {
  it('says a family a marketplace holds as one product was queued as one product', () => {
    expect(marketplacePublishLines(publish({ platformName: 'Shopify', platformCode: 'SHOPIFY', familyCards: 8, enqueuedCards: 8, enqueuedVariants: 24 })))
      .toEqual(['Shopify: model ailesi (8 renk, 24 varyant) tek ürün olarak gönderim kuyruğuna alındı.'])
    expect(marketplacePublishLines(publish({ platformName: 'PttAVM', platformCode: 'PTTAVM', familyCards: 8, enqueuedCards: 8, enqueuedVariants: 24 })))
      .toEqual(['PttAVM: model ailesi (8 renk, 24 varyant) tek ürün olarak gönderim kuyruğuna alındı.'])
  })

  it('counts cards and variants for a marketplace that lists every card on its own, with the cards it skipped', () => {
    expect(marketplacePublishLines(publish({ familyCards: 8, enqueuedCards: 6, enqueuedVariants: 18, skipped: { live: 2, running: 0, matched: 0 } })))
      .toEqual(['Trendyol: 6 renk kartı (18 varyant) gönderim kuyruğuna alındı; 2 renk kartı zaten yayında, atlandı.'])
  })

  it('names a single product and its variants', () => {
    expect(marketplacePublishLines(publish({ platformName: 'n11', platformCode: 'N11', enqueuedCards: 1, enqueuedVariants: 3 })))
      .toEqual(['n11: ürün (3 varyant) gönderim kuyruğuna alındı.'])
  })

  it('says why nothing new went when every card was skipped', () => {
    expect(marketplacePublishLines(publish({ skipped: { live: 1, running: 0, matched: 0 } }))).toEqual(['Trendyol: zaten yayında, atlandı; yeni gönderim yapılmadı.'])
    expect(marketplacePublishLines(publish({ platformName: 'Shopify', platformCode: 'SHOPIFY', familyCards: 8, skipped: { live: 5, running: 3, matched: 0 } })))
      .toEqual(['Shopify: 5 renk kartı zaten yayında, 3 renk kartı için yayın işi zaten sürüyor, atlandı; yeni gönderim yapılmadı.'])
  })

  it('names the marketplace and the cards on every refusal', () => {
    const failures = new Map([['Birden fazla varyant Shopify\'da ancak renk, beden gibi bir seçenekle ayrılabilir.', [0, 1, 2, 3, 4, 5, 6, 7]]])
    expect(marketplacePublishLines(publish({ platformName: 'Shopify', platformCode: 'SHOPIFY', familyCards: 8, failures }))).toEqual([
      'Shopify: reddedildi — Birden fazla varyant Shopify\'da ancak renk, beden gibi bir seçenekle ayrılabilir. (8 renk kartında)'
    ])
    expect(marketplacePublishLines(publish({ platformName: 'n11', platformCode: 'N11', familyCards: 8, enqueuedCards: 7, enqueuedVariants: 21, failures: new Map([['Kategori eksik.', [2]]]) }))).toEqual([
      'n11: 7 renk kartı (21 varyant) gönderim kuyruğuna alındı.',
      'n11: reddedildi — Kategori eksik. (3. renk kartı)'
    ])
  })
})

describe('product save feedback', () => {
  it('titles a queued publication "Yayın Başlatıldı" and says the product was saved first', () => {
    const feedback = productSaveFeedback(save({
      publications: [
        publish({ platformName: 'Shopify', platformCode: 'SHOPIFY', familyCards: 8, enqueuedCards: 8, enqueuedVariants: 24 }),
        publish({ familyCards: 8, skipped: { live: 8, running: 0, matched: 0 } })
      ]
    }))
    expect(feedback).toEqual({
      title: 'Yayın Başlatıldı',
      lines: [
        'Ürün bilgileri kaydedildi.',
        'Shopify: model ailesi (8 renk, 24 varyant) tek ürün olarak gönderim kuyruğuna alındı.',
        'Trendyol: 8 renk kartı zaten yayında, atlandı; yeni gönderim yapılmadı.'
      ],
      kind: 'success',
      persistent: false,
      durationMs: 6000
    })
  })

  it('does not claim a save when the form had no changes', () => {
    const feedback = productSaveFeedback(save({ productChanged: false, publications: [publish({ enqueuedCards: 1, enqueuedVariants: 3 })] }))
    expect(feedback.lines).toEqual(['Trendyol: ürün (3 varyant) gönderim kuyruğuna alındı.'])
    expect(feedback.durationMs).toBe(3500)
  })

  it('keeps a partly refused publication on screen as an error', () => {
    const feedback = productSaveFeedback(save({
      publications: [publish({ enqueuedCards: 1, enqueuedVariants: 3 })],
      publicationErrors: [{ platformName: 'Pazarama', message: 'Marka eşleşmesi yok.' }]
    }))
    expect(feedback.title).toBe('Yayın Kısmen Başlatıldı')
    expect(feedback.lines).toContain('Pazarama: reddedildi — Marka eşleşmesi yok.')
    expect(feedback).toMatchObject({ kind: 'error', persistent: true })
  })

  it('titles a publication that queued nothing and was refused "Yayın Başlatılamadı"', () => {
    const feedback = productSaveFeedback(save({ productChanged: false, publications: [publish({ failures: new Map([['Kategori eksik.', [0]]]) })] }))
    expect(feedback).toMatchObject({ title: 'Yayın Başlatılamadı', lines: ['Trendyol: reddedildi — Kategori eksik.'], kind: 'error', persistent: true })
  })

  it('says "Ürün Kaydedildi" when every marketplace was already live, as information', () => {
    const feedback = productSaveFeedback(save({ publications: [publish({ skipped: { live: 1, running: 0, matched: 0 } })] }))
    expect(feedback).toMatchObject({ title: 'Ürün Kaydedildi', kind: 'info', persistent: false, durationMs: 8000 })
  })

  it('says a plain save saved the product, and when Yayınla had no marketplace selected', () => {
    expect(productSaveFeedback(save({ publishRequested: false, savedParts: ['stok'] }))).toEqual({
      title: 'Ürün Kaydedildi', lines: ['Ürün bilgileri kaydedildi; stok güncellendi.'], kind: 'success', persistent: false, durationMs: 3500
    })
    expect(productSaveFeedback(save({ publishRequested: false, productChanged: false })).lines).toEqual(['Kaydedilecek yeni değişiklik yoktu.'])
    expect(productSaveFeedback(save({})).lines).toEqual(['Ürün bilgileri kaydedildi.', 'Yayın için pazaryeri seçilmedi; ürün pazaryerlerine gönderilmedi.'])
  })

  it('names what a new product saved', () => {
    const feedback = productSaveFeedback(save({ created: true, savedParts: ['görseller', 'stok', 'kanal fiyatları'], publications: [publish({ enqueuedCards: 1, enqueuedVariants: 2 })] }))
    expect(feedback.lines[0]).toBe('Ürün oluşturuldu; görseller, stok, kanal fiyatları kaydedildi.')
    expect(feedback.title).toBe('Yayın Başlatıldı')
  })

  it('makes a stock that was not saved an error even when the publication started', () => {
    const feedback = productSaveFeedback(save({ saveWarnings: ['Stok kaydedilemedi: SKU-1: stok değişti.'], publications: [publish({ enqueuedCards: 1, enqueuedVariants: 1 })] }))
    expect(feedback).toMatchObject({ kind: 'error', persistent: true })
    expect(feedback.lines[1]).toBe('Stok kaydedilemedi: SKU-1: stok değişti.')
  })

  it('lists the update jobs per marketplace and the one that failed', () => {
    const feedback = productSaveFeedback(save({
      updates: [
        { platformName: 'Trendyol', queued: { productInformation: true, priceInventory: true } },
        { platformName: 'Hepsiburada', queued: { productInformation: true, priceInventory: false }, error: 'Bağlantı pasif.' }
      ]
    }))
    expect(feedback.title).toBe('Güncelleme Kısmen Başlatıldı')
    expect(feedback.lines).toEqual([
      'Ürün bilgileri kaydedildi.',
      'Trendyol: ürün bilgisi ve fiyat-stok güncelleme işi kuyruğa alındı.',
      'Hepsiburada: ürün bilgisi güncelleme işi kuyruğa alındı; ardından güncelleme başlatılamadı — Bağlantı pasif.'
    ])
    expect(productSaveFeedback(save({ updates: [{ platformName: 'Shopify', queued: { productInformation: false, priceInventory: true } }] })).title).toBe('Güncelleme Başlatıldı')
  })
})

describe('publication card messages', () => {
  it('says a family held as one marketplace product is read for every card', () => {
    expect(publicationRefreshMessage('Shopify', 'SHOPIFY', 8)).toBe('Shopify: 8 renk kartının yayın durumu pazaryerinden yeniden sorgulanıyor.')
    expect(publicationRefreshMessage('Trendyol', 'TRENDYOL', 8)).toBe('Trendyol: yayın durumu pazaryerinden yeniden sorgulanıyor.')
  })

  it('says whether a stopped job stopped', () => {
    expect(publicationCancelMessage('n11', 'CANCELLED')).toContain('n11: yayın işi durduruldu.')
    expect(publicationCancelMessage('n11', 'RUNNING')).toBe('n11: yayın işinin durdurulması istendi; çalışan adım bitince durur.')
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
