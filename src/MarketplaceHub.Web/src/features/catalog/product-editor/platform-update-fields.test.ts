import { describe, expect, it } from 'vitest'
import { availablePlatformUpdateFields, defaultPlatformUpdatePreferences, platformUpdateGroups, platformUpdatePlan, platformUpdatePreferencesKey, platformUpdateSummary, priceStockPlatformUpdateFields, readPlatformUpdatePreferences, writePlatformUpdatePreferences, defaultPlatformUpdateFields } from './platform-update-fields'

function memoryStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial))
  return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value) }, values }
}

describe('platform update fields', () => {
  it('groups every field that is really sent', () => {
    expect(platformUpdateGroups.map(group => [group.label, group.fields.map(field => field.label)])).toEqual([
      ['Ürün Bilgileri', ['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Garanti Süresi']],
      ['Kargo ve Teslimat', ['Desi', 'Menşei', 'Teslimat Süresi']],
      ['Satış Bilgileri', ['Fiyat', 'Stok', 'KDV Oranı']]
    ])
    expect(platformUpdateGroups[0].linked?.map(item => [item.label, item.with])).toEqual([['Renk / Beden', 'ATTRIBUTES']])
  })

  it('shows only the fields a selected marketplace can take', () => {
    expect(availablePlatformUpdateFields(['HEPSIBURADA'])).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'PRICE', 'STOCK', 'VAT_RATE'])
    expect(availablePlatformUpdateFields(['TRENDYOL'])).not.toContain('WARRANTY')
    expect(availablePlatformUpdateFields(['HEPSIBURADA', 'TRENDYOL'])).toContain('DELIVERY')
    expect(availablePlatformUpdateFields(['SHOPIFY'])).toEqual([])
    expect(availablePlatformUpdateFields([])).toEqual([])
  })

  it('keeps the old switch as the default: on was every field, the quick update price and stock', () => {
    expect(defaultPlatformUpdatePreferences.fields).toEqual(defaultPlatformUpdateFields)
    const all = platformUpdatePlan('TRENDYOL', defaultPlatformUpdateFields)
    expect(all.productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'ORIGIN', 'DELIVERY', 'VAT_RATE'])
    expect([all.price, all.stock]).toEqual([true, true])
    expect(platformUpdatePlan('HEPSIBURADA', defaultPlatformUpdateFields).productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'VAT_RATE'])
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
    expect(platformUpdateSummary(plan)).toBe('Ürün Adı, Açıklama, Görseller, Kategori Özellikleri, Renk / Beden, Liste Fiyatı, Satış Fiyatı')
    const desi = platformUpdatePlan('TRENDYOL', ['DESI'])
    expect(desi.sends).toEqual(['Desi', 'KDV Oranı', 'Menşei', 'Stok Kodu'])
    expect(desi.bundled).toEqual(['ORIGIN', 'VAT_RATE'])
  })

  it('sends nothing to Trendyol for the warranty alone', () => {
    const warranty = platformUpdatePlan('TRENDYOL', ['WARRANTY'])
    expect(warranty.sends).toEqual([])
    expect(warranty.productFields).toEqual([])
  })

  it('sends the whole Hepsiburada product package for any product field, and nothing for origin or delivery alone', () => {
    const desi = platformUpdatePlan('HEPSIBURADA', ['DESI'])
    expect(desi.fields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'VAT_RATE'])
    expect(desi.sends).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Renk / Beden', 'Garanti Süresi', 'Desi', 'KDV Oranı', 'Barkod'])
    expect(desi.productFields).toEqual(['DESI'])
    expect(platformUpdatePlan('HEPSIBURADA', ['PRICE']).sends).toEqual(['Satış Fiyatı'])
    const delivery = platformUpdatePlan('HEPSIBURADA', ['ORIGIN', 'DELIVERY'])
    expect(delivery.sends).toEqual([])
    expect(delivery.productFields).toEqual([])
    expect(platformUpdateSummary(delivery)).toBe('Seçili alanlardan hiçbiri bu platforma gönderilmiyor')
  })

  it('sends nothing to a read-only marketplace', () => {
    const plan = platformUpdatePlan('SHOPIFY', defaultPlatformUpdateFields)
    expect(plan.sends).toEqual([])
    expect([plan.price, plan.stock]).toEqual([false, false])
  })

  it('remembers the pick and the open groups as they were left', () => {
    const storage = memoryStorage()
    expect(readPlatformUpdatePreferences(storage)).toEqual(defaultPlatformUpdatePreferences)
    writePlatformUpdatePreferences({ fields: ['STOCK', 'TITLE'], openGroups: ['sales'] }, storage)
    expect(readPlatformUpdatePreferences(storage)).toEqual({ fields: ['TITLE', 'STOCK'], openGroups: ['sales'] })
    writePlatformUpdatePreferences({ fields: [], openGroups: [] }, storage)
    expect(readPlatformUpdatePreferences(storage)).toEqual({ fields: [], openGroups: [] })
  })

  it('reads a record of the first version: what it lacked starts picked and open, the rest stays as left', () => {
    const first = memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":["TITLE","PRICE"],"openGroups":["sales"]}' })
    // Garanti Süresi is new, so it starts picked; Desi, Menşei... were known and switched off, so they stay off.
    expect(readPlatformUpdatePreferences(first)).toEqual({ fields: ['TITLE', 'WARRANTY', 'PRICE'], openGroups: ['shipping', 'sales'] })
  })

  it('falls back to the default on unreadable or blocked storage, and drops unknown fields', () => {
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{bozuk' }))).toEqual(defaultPlatformUpdatePreferences)
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":"TITLE"}' }))).toEqual(defaultPlatformUpdatePreferences)
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":["PRICE","VIDEO"],"openGroups":["product","other"],"knownFields":["TITLE","DESCRIPTION","IMAGES","ATTRIBUTES","WARRANTY","DESI","ORIGIN","DELIVERY","PRICE","STOCK","VAT_RATE","VIDEO"],"knownGroups":["product","shipping","sales","other"]}' }))).toEqual({ fields: ['PRICE'], openGroups: ['product'] })
    const throwing = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') } }
    expect(readPlatformUpdatePreferences(throwing)).toEqual(defaultPlatformUpdatePreferences)
    expect(() => writePlatformUpdatePreferences(defaultPlatformUpdatePreferences, throwing)).not.toThrow()
    expect(readPlatformUpdatePreferences(undefined)).toEqual(defaultPlatformUpdatePreferences)
  })
})
