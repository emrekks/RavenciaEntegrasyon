import { describe, expect, it } from 'vitest'
import { availablePlatformUpdateFields, defaultPlatformUpdateFields, defaultPlatformUpdatePreferences, partialPlatformUpdateField, platformUpdateFieldsForStores, platformUpdateGroups, platformUpdatePlan, platformUpdatePreferencesKey, platformUpdateSummary, priceStockPlatformUpdateFields, readPlatformUpdatePreferences, writePlatformUpdatePreferences } from './platform-update-fields'

function memoryStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial))
  return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value) }, values }
}

describe('platform update fields', () => {
  it('groups every field that is really sent', () => {
    expect(platformUpdateGroups.map(group => [group.label, group.fields.map(field => field.label)])).toEqual([
      ['Ürün Bilgileri', ['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Garanti Süresi']],
      ['Kargo ve Teslimat', ['Desi', 'Teslimat Süresi']],
      ['Satış Bilgileri', ['Fiyat', 'Stok', 'KDV Oranı']]
    ])
    expect(platformUpdateGroups[0].linked?.map(item => [item.label, item.with])).toEqual([['Renk / Beden', 'ATTRIBUTES'], ['Menşei', 'ATTRIBUTES']])
  })

  it('follows the stores: each shows what it takes, the rest none', () => {
    expect(availablePlatformUpdateFields(['TRENDYOL'])).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'DELIVERY', 'PRICE', 'STOCK', 'VAT_RATE'])
    expect(availablePlatformUpdateFields(['HEPSIBURADA'])).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'DELIVERY', 'PRICE', 'STOCK', 'VAT_RATE'])
    expect(availablePlatformUpdateFields(['HEPSIBURADA', 'TRENDYOL'])).toContain('DELIVERY')
    expect(availablePlatformUpdateFields(['SHOPIFY'])).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'PRICE', 'STOCK'])
    expect(availablePlatformUpdateFields(['PAZARAMA'])).toEqual(['PRICE', 'STOCK', 'VAT_RATE'])
    expect(availablePlatformUpdateFields(['N11'])).toEqual(['DESCRIPTION', 'ATTRIBUTES', 'DELIVERY', 'PRICE', 'STOCK', 'VAT_RATE'])
    expect(availablePlatformUpdateFields(['PTTAVM'])).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'WARRANTY', 'DESI', 'DELIVERY', 'PRICE', 'STOCK', 'VAT_RATE'])
    expect(availablePlatformUpdateFields([])).toEqual([])
  })

  it('names the stores of a field only some of them take', () => {
    expect(partialPlatformUpdateField('DELIVERY', ['TRENDYOL', 'SHOPIFY'])).toEqual(['TRENDYOL'])
    expect(partialPlatformUpdateField('WARRANTY', ['TRENDYOL', 'HEPSIBURADA'])).toEqual(['HEPSIBURADA'])
    expect(partialPlatformUpdateField('DELIVERY', ['trendyol', 'TRENDYOL ', 'SHOPIFY'])).toEqual(['TRENDYOL'])
    expect(partialPlatformUpdateField('TITLE', ['TRENDYOL', 'HEPSIBURADA'])).toEqual([])
    // Hepsiburada now takes the delivery time too.
    expect(partialPlatformUpdateField('DELIVERY', ['TRENDYOL', 'HEPSIBURADA'])).toEqual([])
    expect(partialPlatformUpdateField('DELIVERY', ['TRENDYOL'])).toEqual([])
    expect(partialPlatformUpdateField('TITLE', ['TRENDYOL', 'PAZARAMA'])).toEqual(['TRENDYOL'])
    expect(partialPlatformUpdateField('VAT_RATE', ['SHOPIFY', 'PAZARAMA'])).toEqual(['PAZARAMA'])
  })

  it('drops a field no longer shown and ticks one shown only now', () => {
    const hepsiburada = availablePlatformUpdateFields(['HEPSIBURADA'])
    const both = availablePlatformUpdateFields(['HEPSIBURADA', 'SHOPIFY'])
    const shopify = availablePlatformUpdateFields(['SHOPIFY'])
    expect(platformUpdateFieldsForStores(['TITLE', 'PRICE'], shopify, both)).toEqual(['TITLE', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'DELIVERY', 'PRICE', 'VAT_RATE'])
    expect(platformUpdateFieldsForStores(['TITLE', 'DELIVERY', 'PRICE'], both, shopify)).toEqual(['TITLE', 'PRICE'])
    expect(platformUpdateFieldsForStores(['TITLE', 'DELIVERY'], both, hepsiburada)).toEqual(['TITLE', 'DELIVERY'])
    // A field the user unticked stays unticked while it stays shown.
    expect(platformUpdateFieldsForStores(['TITLE'], both, availablePlatformUpdateFields(['TRENDYOL']))).toEqual(['TITLE'])
    expect(platformUpdateFieldsForStores(['TITLE'], both, [])).toEqual([])
  })

  it('keeps the old switch as the default: on was every field, the quick update price and stock', () => {
    expect(defaultPlatformUpdatePreferences.fields).toEqual(defaultPlatformUpdateFields)
    const all = platformUpdatePlan('TRENDYOL', defaultPlatformUpdateFields)
    expect(all.productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'DELIVERY', 'VAT_RATE'])
    expect([all.price, all.stock]).toEqual([true, true])
    expect(platformUpdatePlan('HEPSIBURADA', defaultPlatformUpdateFields).productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'DELIVERY', 'VAT_RATE'])
    const quick = platformUpdatePlan('TRENDYOL', priceStockPlatformUpdateFields)
    expect(quick.productFields).toEqual([])
    expect([quick.price, quick.stock]).toEqual([true, true])
  })

  it('lists everything a Trendyol request carries, also what rides along', () => {
    const plan = platformUpdatePlan('TRENDYOL', ['TITLE', 'PRICE'])
    expect(plan.fields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'PRICE'])
    expect(plan.bundled).toEqual(['DESCRIPTION', 'IMAGES', 'ATTRIBUTES'])
    expect(plan.productFields).toEqual(['TITLE'])
    expect([plan.price, plan.stock]).toEqual([true, false])
    expect(plan.sends).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Liste Fiyatı', 'Satış Fiyatı'])
    expect(plan.extras).toEqual(['Renk / Beden', 'Menşei'])
    expect(platformUpdateSummary(plan)).toBe('Ürün Adı, Açıklama, Görseller, Kategori Özellikleri, Liste Fiyatı, Satış Fiyatı, Renk / Beden, Menşei')
    const desi = platformUpdatePlan('TRENDYOL', ['DESI'])
    expect(desi.sends).toEqual(['Desi', 'KDV Oranı'])
    expect(desi.extras).toEqual(['Menşei', 'Stok Kodu'])
    expect(desi.bundled).toEqual(['VAT_RATE'])
  })

  it('sends nothing to Trendyol for the warranty alone', () => {
    const warranty = platformUpdatePlan('TRENDYOL', ['WARRANTY'])
    expect(warranty.sends).toEqual([])
    expect(warranty.productFields).toEqual([])
  })

  it('sends the whole Hepsiburada product package for any product field, and the delivery time alone apart', () => {
    const desi = platformUpdatePlan('HEPSIBURADA', ['DESI'])
    expect(desi.fields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'VAT_RATE'])
    expect(desi.sends).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Garanti Süresi', 'Desi', 'KDV Oranı'])
    expect(desi.extras).toEqual(['Renk / Beden', 'Menşei', 'Barkod'])
    expect(desi.productFields).toEqual(['DESI'])
    expect(platformUpdatePlan('HEPSIBURADA', ['PRICE']).sends).toEqual(['Satış Fiyatı'])
    const delivery = platformUpdatePlan('HEPSIBURADA', ['DELIVERY'])
    expect(delivery.fields).toEqual(['DELIVERY'])
    expect(delivery.sends).toEqual(['Teslimat Süresi'])
    expect(delivery.extras).toEqual([])
    expect(delivery.productFields).toEqual(['DELIVERY'])
    expect([delivery.price, delivery.stock]).toEqual([false, false])
    expect(platformUpdateSummary(delivery)).toBe('Teslimat Süresi')
    expect(platformUpdatePlan('HEPSIBURADA', ['DESI', 'DELIVERY']).productFields).toEqual(['DESI', 'DELIVERY'])
  })

  it('sends Pazarama the VAT rate alone as its product update, and price and stock together', () => {
    const vat = platformUpdatePlan('PAZARAMA', ['VAT_RATE'])
    expect(vat.sends).toEqual(['KDV Oranı'])
    expect(vat.productFields).toEqual(['VAT_RATE'])
    expect([vat.price, vat.stock]).toEqual([false, false])
    // Content picks have no Pazarama API: nothing of them goes.
    const content = platformUpdatePlan('PAZARAMA', ['TITLE', 'DESCRIPTION', 'IMAGES', 'DESI', 'DELIVERY'])
    expect(content.sends).toEqual([])
    expect(content.productFields).toEqual([])
    const price = platformUpdatePlan('PAZARAMA', ['PRICE'])
    expect(price.sends).toEqual(['Liste Fiyatı', 'Satış Fiyatı', 'Stok'])
    expect(price.bundled).toEqual(['STOCK'])
    expect(price.productFields).toEqual([])
    expect([price.price, price.stock]).toEqual([true, true])
    const all = platformUpdatePlan('PAZARAMA', defaultPlatformUpdateFields)
    expect(all.productFields).toEqual(['VAT_RATE'])
    expect([all.price, all.stock]).toEqual([true, true])
  })

  it('sends the whole Shopify card as the product update for any pick, price and stock included', () => {
    for (const picked of [defaultPlatformUpdateFields, priceStockPlatformUpdateFields, ['TITLE'] as const]) {
      const plan = platformUpdatePlan('SHOPIFY', picked)
      expect(plan.productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'PRICE', 'STOCK'])
      expect(plan.sends).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Liste Fiyatı', 'Satış Fiyatı', 'Stok'])
      expect(plan.extras).toEqual(['Renk / Beden', 'Ürün Tipi'])
      expect([plan.price, plan.stock]).toEqual([false, false])
    }
    expect(platformUpdatePlan('SHOPIFY', ['DESI']).productFields).toEqual([])
  })

  it('sends PTT AVM only the picked product fields with its variant list, and price with stock', () => {
    const title = platformUpdatePlan('PTTAVM', ['TITLE'])
    expect(title.productFields).toEqual(['TITLE'])
    expect(title.bundled).toEqual([])
    expect(title.extras).toEqual(['Varyant Listesi'])
    expect([title.price, title.stock]).toEqual([false, false])
    // Category attributes never go to PTT AVM.
    expect(platformUpdatePlan('PTTAVM', ['ATTRIBUTES']).productFields).toEqual([])
    expect(platformUpdatePlan('PTTAVM', defaultPlatformUpdateFields).productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'WARRANTY', 'DESI', 'DELIVERY', 'VAT_RATE'])
    // One request takes price and stock: a pick of one sends both.
    const price = platformUpdatePlan('PTTAVM', ['PRICE'])
    expect([price.price, price.stock]).toEqual([true, true])
    expect(price.bundled).toEqual(['STOCK'])
    expect(price.productFields).toEqual([])
  })

  it('sends nothing to a read-only marketplace', () => {
    const plan = platformUpdatePlan('PAZARAMA', defaultPlatformUpdateFields)
    expect(plan.sends).toEqual([])
    expect([plan.price, plan.stock]).toEqual([false, false])
  })

  it('sends to n11 only the picked fields its product-update task takes, price and stock together', () => {
    const all = platformUpdatePlan('N11', defaultPlatformUpdateFields)
    expect(all.productFields).toEqual(['DESCRIPTION', 'ATTRIBUTES', 'DELIVERY', 'VAT_RATE'])
    expect([all.price, all.stock]).toEqual([true, true])
    expect(all.sends).toEqual(['Açıklama', 'Kategori Özellikleri', 'KDV Oranı', 'Teslimat Süresi', 'Liste Fiyatı', 'Satış Fiyatı', 'Stok'])
    expect(all.extras).toEqual(['Marka', 'Renk / Beden', 'Kargo Şablonu'])
    // Each field is its own part of the task: a pick does not bring the others along.
    const description = platformUpdatePlan('N11', ['DESCRIPTION'])
    expect(description.productFields).toEqual(['DESCRIPTION'])
    expect(description.bundled).toEqual([])
    expect(description.sends).toEqual(['Açıklama'])
    const delivery = platformUpdatePlan('N11', ['DELIVERY'])
    expect(delivery.sends).toEqual(['Teslimat Süresi'])
    expect(delivery.extras).toEqual(['Kargo Şablonu'])
    // The n11 price-stock task cannot leave one part out: a pick of the price carries the stock.
    const price = platformUpdatePlan('N11', ['PRICE'])
    expect(price.bundled).toEqual(['STOCK'])
    expect([price.price, price.stock]).toEqual([true, true])
    expect(price.productFields).toEqual([])
    // What n11 cannot change through its API is not sent.
    const title = platformUpdatePlan('N11', ['TITLE', 'IMAGES', 'DESI', 'WARRANTY'])
    expect(title.sends).toEqual([])
    expect(title.productFields).toEqual([])
    expect(partialPlatformUpdateField('TITLE', ['TRENDYOL', 'N11'])).toEqual(['TRENDYOL'])
  })

  it('remembers the pick and the open groups as they were left', () => {
    const storage = memoryStorage()
    expect(readPlatformUpdatePreferences(storage)).toEqual(defaultPlatformUpdatePreferences)
    writePlatformUpdatePreferences({ fields: ['STOCK', 'TITLE'], openGroups: ['sales'] }, storage)
    expect(readPlatformUpdatePreferences(storage)).toEqual({ fields: ['TITLE', 'STOCK'], openGroups: ['sales'] })
    writePlatformUpdatePreferences({ fields: [], openGroups: [] }, storage)
    expect(readPlatformUpdatePreferences(storage)).toEqual({ fields: [], openGroups: [] })
  })

  it('reads a record of the first version: Menşei is dropped, what it lacked starts picked and open, the rest stays as left', () => {
    const first = memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":["TITLE","ORIGIN","PRICE"],"openGroups":["sales"]}' })
    // Garanti Süresi is new, so it starts picked; Desi, Teslimat Süresi... were known and switched off, so they stay off.
    expect(readPlatformUpdatePreferences(first)).toEqual({ fields: ['TITLE', 'WARRANTY', 'PRICE'], openGroups: ['shipping', 'sales'] })
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":["ORIGIN","DESI"],"openGroups":[]}' })).fields).toEqual(['WARRANTY', 'DESI'])
  })

  it('falls back to the default on unreadable or blocked storage, and drops unknown fields', () => {
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{bozuk' }))).toEqual(defaultPlatformUpdatePreferences)
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":"TITLE"}' }))).toEqual(defaultPlatformUpdatePreferences)
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":["PRICE","VIDEO"],"openGroups":["product","other"],"knownFields":["TITLE","DESCRIPTION","IMAGES","ATTRIBUTES","WARRANTY","DESI","DELIVERY","PRICE","STOCK","VAT_RATE","VIDEO"],"knownGroups":["product","shipping","sales","other"]}' }))).toEqual({ fields: ['PRICE'], openGroups: ['product'] })
    const throwing = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') } }
    expect(readPlatformUpdatePreferences(throwing)).toEqual(defaultPlatformUpdatePreferences)
    expect(() => writePlatformUpdatePreferences(defaultPlatformUpdatePreferences, throwing)).not.toThrow()
    expect(readPlatformUpdatePreferences(undefined)).toEqual(defaultPlatformUpdatePreferences)
  })
})
