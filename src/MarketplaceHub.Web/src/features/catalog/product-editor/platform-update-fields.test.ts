import { describe, expect, it } from 'vitest'
import { availablePlatformUpdateFields, defaultPlatformUpdatePreferences, platformUpdatePlan, platformUpdatePreferencesKey, platformUpdateSummary, priceStockPlatformUpdateFields, readPlatformUpdatePreferences, writePlatformUpdatePreferences, defaultPlatformUpdateFields } from './platform-update-fields'

function memoryStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial))
  return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value) }, values }
}

describe('platform update fields', () => {
  it('shows only the fields a selected marketplace can take', () => {
    expect(availablePlatformUpdateFields(['HEPSIBURADA'])).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'VAT_RATE', 'PRICE', 'STOCK'])
    expect(availablePlatformUpdateFields(['HEPSIBURADA', 'TRENDYOL'])).toContain('DELIVERY')
    expect(availablePlatformUpdateFields(['SHOPIFY'])).toEqual([])
    expect(availablePlatformUpdateFields([])).toEqual([])
  })

  it('keeps the old switch as the default: on was every field, the quick update price and stock', () => {
    expect(defaultPlatformUpdatePreferences.fields).toEqual(defaultPlatformUpdateFields)
    const all = platformUpdatePlan('TRENDYOL', defaultPlatformUpdateFields)
    expect(all.productFields).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'VAT_RATE', 'ORIGIN', 'DELIVERY'])
    expect([all.price, all.stock]).toEqual([true, true])
    const quick = platformUpdatePlan('TRENDYOL', priceStockPlatformUpdateFields)
    expect(quick.productFields).toEqual([])
    expect([quick.price, quick.stock]).toEqual([true, true])
  })

  it('says honestly what rides in the same Trendyol request', () => {
    const plan = platformUpdatePlan('TRENDYOL', ['TITLE', 'PRICE'])
    expect(plan.sends).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'PRICE'])
    expect(plan.bundled).toEqual(['DESCRIPTION', 'IMAGES', 'ATTRIBUTES'])
    expect(plan.productFields).toEqual(['TITLE'])
    expect([plan.price, plan.stock]).toEqual([true, false])
    expect(platformUpdateSummary(plan)).toBe('Ürün Adı, Açıklama, Görseller, Kategori Özellikleri, Fiyat')
  })

  it('sends the whole Hepsiburada product package for any product field, and nothing for origin or delivery alone', () => {
    const desi = platformUpdatePlan('HEPSIBURADA', ['DESI'])
    expect(desi.sends).toEqual(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'VAT_RATE'])
    expect(desi.extras).toEqual(['garanti süresi, barkod ve video'])
    expect(desi.productFields).toEqual(['DESI'])
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

  it('falls back to the default on unreadable or blocked storage, and drops unknown fields', () => {
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{bozuk' }))).toEqual(defaultPlatformUpdatePreferences)
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":"TITLE"}' }))).toEqual(defaultPlatformUpdatePreferences)
    expect(readPlatformUpdatePreferences(memoryStorage({ [platformUpdatePreferencesKey]: '{"fields":["PRICE","WARRANTY"],"openGroups":["product","other"]}' }))).toEqual({ fields: ['PRICE'], openGroups: ['product'] })
    const throwing = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') } }
    expect(readPlatformUpdatePreferences(throwing)).toEqual(defaultPlatformUpdatePreferences)
    expect(() => writePlatformUpdatePreferences(defaultPlatformUpdatePreferences, throwing)).not.toThrow()
    expect(readPlatformUpdatePreferences(undefined)).toEqual(defaultPlatformUpdatePreferences)
  })
})
