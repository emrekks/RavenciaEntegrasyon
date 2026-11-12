import { describe, expect, it } from 'vitest'
import {
  activityActor, activityChange, activityCountsQueryString, activityFieldValue, activityFilterCount, activityKindCounts, activityLink, activityMovements, activitySourceLabel, activityPanelFilterCount, activityProductLines, activityQueryString, activitySourceText,
  activityTypeLabel, activityVariantText, emptyActivityFilters, toggleActivityKind, type ProductActivityEntry
} from './product-activity'

function entry(patch: Partial<ProductActivityEntry>): ProductActivityEntry {
  return {
    id: 'a', occurredAt: '2026-11-04T09:02:26Z', kind: 'STOCK', type: 'MANUAL_ADJUSTMENT', productId: 'p', productTitle: 'Bluz', imageUrl: null, variantId: 'v', sku: 'S', barcode: 'B',
    modelCode: null, optionText: null, quantityDelta: null, previousSalePrice: null, newSalePrice: null, previousListPrice: null, newListPrice: null, currency: null,
    previousStatus: null, newStatus: null, source: 'MANUAL', note: null, connectionId: null, platformCode: null, storeName: null, orderId: null, orderNumber: null,
    returnId: null, returnNumber: null, actorUserId: null, actorName: null, ...patch
  }
}

describe('product activity change chip', () => {
  it('signs a stock change and says when a row changed no stock', () => {
    expect(activityChange(entry({ quantityDelta: 1 }))).toMatchObject({ text: '+1 Stok', tone: 'up' })
    expect(activityChange(entry({ quantityDelta: -2.5 }))).toMatchObject({ text: '−2,5 Stok', tone: 'down' })
    expect(activityChange(entry({ type: 'ORDER_STOCK_SHORTFALL', quantityDelta: 1 }))).toMatchObject({ text: '1 Adet Eksik', tone: 'info' })
    expect(activityChange(entry({ type: 'RETURN_QUARANTINE', kind: 'RETURN', quantityDelta: 1 }))).toMatchObject({ text: '1 Adet Karantinada', tone: 'info' })
    // Information rows carry no numbers change even if a balance came along.
    expect(activityChange(entry({ type: 'ORDER_STOCK_SHORTFALL', quantityDelta: 1, stockBefore: 0, stockAfter: 0 })).from).toBeUndefined()
  })

  it('shows the stock before → after with the amount, and only the after when the before is unknown', () => {
    expect(activityChange(entry({ quantityDelta: 1, stockBefore: 4, stockAfter: 5 }))).toMatchObject({ text: '+1 · 4 → 5', delta: '+1', from: '4', to: '5', tone: 'up' })
    expect(activityChange(entry({ type: 'ORDER_RECEIVED', quantityDelta: -2, stockBefore: 3, stockAfter: 1 }))).toMatchObject({ text: '−2 · 3 → 1', from: '3', to: '1', tone: 'down' })
    const unknownBefore = activityChange(entry({ quantityDelta: 6, stockBefore: null, stockAfter: 2 }))
    expect(unknownBefore).toMatchObject({ text: '+6 · Stok 2', to: '2', tone: 'up' })
    expect(unknownBefore.from).toBeUndefined()
  })

  it('shows a price before → after, a first price as İlk Fiyat, and a status in the panel words', () => {
    expect(activityChange(entry({ type: 'OFFER_PRICE', kind: 'PRICE', previousSalePrice: 899, newSalePrice: 950, currency: 'TRY' }))).toMatchObject({ text: '₺899 → ₺950', from: '₺899', to: '₺950', tone: 'up' })
    expect(activityChange(entry({ type: 'DEFAULT_PRICE', kind: 'PRICE', previousSalePrice: 100, newSalePrice: 89.9, previousListPrice: 150, newListPrice: 120, currency: 'TRY' })))
      .toMatchObject({ text: '₺100 → ₺89,90', tone: 'down', title: 'Liste fiyatı: ₺150 → ₺120' })
    expect(activityChange(entry({ type: 'OFFER_PRICE', kind: 'PRICE', newSalePrice: 950, currency: 'TRY' }))).toMatchObject({ text: 'İlk Fiyat ₺950', label: 'İlk Fiyat', to: '₺950', tone: 'neutral' })
    expect(activityChange(entry({ type: 'STATUS', kind: 'STATUS', previousStatus: 'ACTIVE', newStatus: 'ARCHIVED' }))).toMatchObject({ text: 'Satışa Açık → Satışa Kapalı', from: 'Satışa Açık', to: 'Satışa Kapalı' })
    expect(activityChange(entry({ type: 'STATUS', kind: 'STATUS', previousStatus: 'ARCHIVED', newStatus: 'ACTIVE' })).text).toBe('Satışa Kapalı → Satışa Açık')
    expect(activityChange(entry({ type: 'STATUS', kind: 'STATUS', previousStatus: 'DRAFT', newStatus: 'ACTIVE' })).text).toBe('Taslak → Satışa Açık')
  })

  it('names an order sale and an order release apart', () => {
    expect(activityTypeLabel(entry({ type: 'ORDER_RECEIVED', quantityDelta: -1 }))).toBe('Satış')
    expect(activityTypeLabel(entry({ type: 'ORDER_RECEIVED', quantityDelta: 1 }))).toBe('Sipariş İptali / Değişikliği')
  })

  it('names a first stock Açılış Stoğu, the opening row and the editor\'s older manual first stock alike', () => {
    expect(activityTypeLabel(entry({ type: 'IMPORT_OPENING', quantityDelta: 5 }))).toBe('Açılış Stoğu')
    expect(activityTypeLabel(entry({ type: 'MANUAL_ADJUSTMENT', quantityDelta: 5, note: 'İlk ürün stoğu' }))).toBe('Açılış Stoğu')
    expect(activityTypeLabel(entry({ type: 'MANUAL_ADJUSTMENT', quantityDelta: 5, note: 'Ürün düzenleme stoğu' }))).toBe('Stok Düzenleme')
  })
})

describe('product activity source', () => {
  it('links an order, a return or the product', () => {
    const order = entry({ source: 'ORDER', orderId: 'o', orderNumber: '#10931' })
    expect(activitySourceText(order)).toBe('Sipariş #10931')
    expect(activityLink(order)).toBe('/orders?search=10931')
    const restock = entry({ source: 'RETURN', returnId: 'r-1', returnNumber: 'R-9', orderNumber: '10931' })
    expect(activitySourceText(restock)).toBe('İade #R-9')
    expect(activityLink(restock)).toBe('/returns/r-1')
    expect(activityLink(entry({ source: 'BULK_EDIT' }))).toBe('/products/p')
    expect(activityLink(entry({ productId: null }))).toBeNull()
    expect(activitySourceText(entry({ source: 'FILE_IMPORT' }))).toBe('Excel')
  })

  it('says who made it, the system for what the panel read, and nothing it does not know', () => {
    expect(activityActor(entry({ actorUserId: 'u', actorName: 'Emre' }))).toBe('Emre')
    expect(activityActor(entry({ actorUserId: 'u' }))).toBe('Bilinmeyen Kullanıcı')
    expect(activityActor(entry({ source: 'MARKETPLACE_IMPORT' }))).toBe('Sistem')
    expect(activityActor(entry({ source: 'MANUAL' }))).toBe('—')
  })
})

describe('product activity request', () => {
  it('sends the kinds with the list only and counts each kind', () => {
    const filters = { ...emptyActivityFilters, kinds: ['PRICE'], platforms: ['trendyol'], search: ' KG-1 ' }
    const list = new URLSearchParams(activityQueryString(filters, 'cursor-1').slice(1))
    expect(Object.fromEntries(list)).toEqual({ limit: '50', kinds: 'PRICE', search: 'KG-1', platforms: 'TRENDYOL', after: 'cursor-1' })
    expect(activityCountsQueryString(filters)).toBe('?search=KG-1&platforms=TRENDYOL')
    expect(activityFilterCount(filters)).toBe(3)
    expect(toggleActivityKind(['PRICE'], 'PRICE')).toEqual([])
    expect(toggleActivityKind([], 'STOCK')).toEqual(['STOCK'])
    expect(activityKindCounts([{ kind: 'STOCK', count: 2 }, { kind: 'RETURN', count: 1 }, { kind: 'OTHER', count: 9 }])).toEqual({ STOCK: 2, PRICE: 0, STATUS: 0, RETURN: 1, INFO: 0 })
  })

  it('bounds the time by the chosen Istanbul days, else by the range', () => {
    const now = Date.parse('2026-11-04T12:00:00Z')
    const days = new URLSearchParams(activityQueryString({ ...emptyActivityFilters, from: '2026-11-04', to: '2026-11-04' }, null, 50, now).slice(1))
    expect(days.get('from')).toBe('2026-11-03T21:00:00.000Z')
    expect(days.get('to')).toBe('2026-11-04T20:59:59.999Z')
    const range = new URLSearchParams(activityQueryString({ ...emptyActivityFilters, range: '24h' }, null, 50, now).slice(1))
    expect(range.get('from')).toBe('2026-11-03T12:00:00.000Z')
  })
})

describe('product activity movement and first-price filters', () => {
  it('sends the movement types and the first-price switch to the list and to the counts, and leaves them out when off', () => {
    const off = new URLSearchParams(activityQueryString(emptyActivityFilters).slice(1))
    expect(off.has('movements')).toBe(false)
    expect(off.has('includeInitialPrices')).toBe(false)
    expect(activityCountsQueryString(emptyActivityFilters)).toBe('')
    const filters = { ...emptyActivityFilters, kinds: ['STOCK'], movements: ['SALE', 'SALE', 'MANUAL_STOCK'], initialPrices: true }
    expect(Object.fromEntries(new URLSearchParams(activityQueryString(filters).slice(1)))).toEqual({ limit: '50', kinds: 'STOCK', movements: 'SALE,MANUAL_STOCK', includeInitialPrices: 'true' })
    // The summary keeps the same movement and first-price filters, only the kind filter is left out.
    expect(Object.fromEntries(new URLSearchParams(activityCountsQueryString(filters)))).toEqual({ movements: 'SALE,MANUAL_STOCK', includeInitialPrices: 'true' })
  })

  it('counts them as panel filters and offers every label the change column shows', () => {
    expect(activityPanelFilterCount({ ...emptyActivityFilters, movements: ['SALE'] })).toBe(1)
    expect(activityPanelFilterCount({ ...emptyActivityFilters, initialPrices: true })).toBe(1)
    expect(activityFilterCount({ ...emptyActivityFilters, movements: ['SALE'], initialPrices: true })).toBe(2)
    expect(activityMovements.map(item => item.label)).toEqual([
      'Satış', 'Sipariş İptali / Değişikliği', 'Stok Yetersiz', 'Stok Düzenleme', 'Açılış Stoğu', 'Pazaryeri Stoğu', 'İade Stoğa Eklendi', 'İade Stoğa Eklenmedi', 'Mağaza Fiyatı', 'Panel Fiyatı', 'Satış Durumu',
      'Maliyet', 'Desi', 'Ürün Adı', 'Barkod', 'Model Kodu', 'Stok Kodu', 'KDV', 'Marka', 'Kategori', 'Garanti Süresi', 'Teslimat Süresi', 'Açıklama'
    ])
    const labels = new Set([
      activityTypeLabel(entry({ type: 'ORDER_RECEIVED', quantityDelta: -1 })), activityTypeLabel(entry({ type: 'ORDER_RECEIVED', quantityDelta: 1 })), activityTypeLabel(entry({ type: 'ORDER_STOCK_SHORTFALL' })),
      activityTypeLabel(entry({ type: 'MANUAL_ADJUSTMENT' })), activityTypeLabel(entry({ type: 'IMPORT_OPENING' })), activityTypeLabel(entry({ type: 'MARKETPLACE_STOCK_IMPORT' })),
      activityTypeLabel(entry({ type: 'RETURN_PASS' })), activityTypeLabel(entry({ type: 'RETURN_DAMAGED' })), activityTypeLabel(entry({ type: 'OFFER_PRICE' })), activityTypeLabel(entry({ type: 'DEFAULT_PRICE' })), activityTypeLabel(entry({ type: 'STATUS' }))
    ])
    for (const field of ['COST', 'DESI', 'TITLE', 'BARCODE', 'MODEL_CODE', 'SKU', 'BRAND', 'CATEGORY', 'WARRANTY', 'DELIVERY_DAYS', 'DESCRIPTION']) labels.add(activityTypeLabel(entry({ type: 'FIELD', kind: 'INFO', field })))
    // KDV is the one movement of two labels, KDV Oranı and KDV Dahil.
    expect(activityMovements.filter(item => !labels.has(item.label)).map(item => item.value)).toEqual(['VAT'])
    expect([activityTypeLabel(entry({ type: 'FIELD', field: 'VAT_RATE' })), activityTypeLabel(entry({ type: 'FIELD', field: 'VAT_INCLUSION' }))]).toEqual(['KDV Oranı', 'KDV Dahil'])
  })
})

describe('activityProductLines', () => {
  it('names the model code and the barcode, and gives the variant its own line', () => {
    expect(activityProductLines({ modelCode: 'MZ029YD', barcode: '8680000000012', sku: 'MZ029YD69-B', optionText: 'Renk: Mürdüm | Beden: 5XL' }))
      .toEqual({ codes: 'Model: MZ029YD · Barkod: 8680000000012', variant: 'Varyant: Mürdüm / 5XL' })
    expect(activityProductLines({ modelCode: null, barcode: null, sku: 'S-1', optionText: null })).toEqual({ codes: 'Stok Kodu: S-1', variant: null })
    expect(activityProductLines({ modelCode: null, barcode: null, sku: null, optionText: null })).toEqual({ codes: null, variant: null })
  })
})

describe('activityVariantText', () => {
  it('reads the option values only, joined by a slash', () => {
    expect(activityVariantText('Renk: Siyah | Beden: M')).toBe('Varyant: Siyah / M')
    expect(activityVariantText('Standart')).toBe('Varyant: Standart')
    expect(activityVariantText(null)).toBeNull()
  })
})

describe('product information rows', () => {
  it('shows a field change before → after, an empty side as Boş, a long value cut with the whole change as its title', () => {
    expect(activityChange(entry({ type: 'FIELD', kind: 'INFO', field: 'COST', previousValue: '100,00 ₺', newValue: '120,50 ₺' })))
      .toMatchObject({ text: '100,00 ₺ → 120,50 ₺', from: '100,00 ₺', to: '120,50 ₺', tone: 'neutral', title: 'Maliyet: 100,00 ₺ → 120,50 ₺' })
    expect(activityChange(entry({ type: 'FIELD', kind: 'INFO', field: 'BARCODE', previousValue: null, newValue: '8680000000012' }))).toMatchObject({ from: 'Boş', to: '8680000000012' })
    const title = 'Kadın Uzun Kollu Pamuklu Basic Bluz Siyah Renk Yeni Sezon'
    const long = activityChange(entry({ type: 'FIELD', kind: 'INFO', field: 'TITLE', previousValue: 'Bluz', newValue: title }))
    expect(long.to).toBe(activityFieldValue(title))
    expect(long.to!.endsWith('…')).toBe(true)
    expect(long.title).toBe(`Ürün Adı: Bluz → ${title}`)
    expect(activityTypeLabel(entry({ type: 'FIELD', kind: 'INFO', field: 'MODEL_CODE' }))).toBe('Model Kodu')
    expect(activityTypeLabel(entry({ type: 'FIELD', kind: 'INFO', field: 'NEW_FIELD' }))).toBe('NEW_FIELD')
  })

  it('counts the Ürün Bilgisi kind and names the platform update sources', () => {
    expect(activityKindCounts([{ kind: 'INFO', count: 3 }]).INFO).toBe(3)
    expect(activitySourceLabel('QUICK_UPDATE')).toBe('Hızlı Güncelle')
    expect(activitySourceLabel('PLATFORM_UPDATE')).toBe('Platformlarda Güncelle')
    expect(activitySourceLabel('PRICE_EDIT')).toBe('Fiyat Düzenleme')
  })
})
