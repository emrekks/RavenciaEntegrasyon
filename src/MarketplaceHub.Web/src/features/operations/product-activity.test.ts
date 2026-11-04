import { describe, expect, it } from 'vitest'
import {
  activityActor, activityChange, activityCountsQueryString, activityFilterCount, activityKindCounts, activityLink, activityQueryString, activitySourceText,
  activityTypeLabel, emptyActivityFilters, toggleActivityKind, type ProductActivityEntry
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
    expect(activityChange(entry({ type: 'ORDER_STOCK_SHORTFALL', quantityDelta: 1 }))).toMatchObject({ text: 'Stok Yetersiz · 1 Eksik', tone: 'info' })
    expect(activityChange(entry({ type: 'RETURN_QUARANTINE', kind: 'RETURN', quantityDelta: 1 }))).toMatchObject({ text: '1 Adet Karantinada', tone: 'info' })
  })

  it('shows a price before → after, a first price alone, and a status in the panel words', () => {
    expect(activityChange(entry({ type: 'OFFER_PRICE', kind: 'PRICE', previousSalePrice: 899, newSalePrice: 950, currency: 'TRY' }))).toMatchObject({ text: '₺899 → ₺950', tone: 'up' })
    expect(activityChange(entry({ type: 'DEFAULT_PRICE', kind: 'PRICE', previousSalePrice: 100, newSalePrice: 89.9, previousListPrice: 150, newListPrice: 120, currency: 'TRY' })))
      .toMatchObject({ text: '₺100 → ₺89,90', tone: 'down', title: 'Liste fiyatı: ₺150 → ₺120' })
    expect(activityChange(entry({ type: 'OFFER_PRICE', kind: 'PRICE', newSalePrice: 950, currency: 'TRY' })).text).toBe('₺950')
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
    expect(activityKindCounts([{ kind: 'STOCK', count: 2 }, { kind: 'RETURN', count: 1 }, { kind: 'OTHER', count: 9 }])).toEqual({ STOCK: 2, PRICE: 0, STATUS: 0, RETURN: 1 })
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
