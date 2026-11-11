import { describe, expect, it } from 'vitest'
import type { Product, Variant } from '../shared'
import { bulkPlatformUpdateMenuState, bulkPlatformUpdateRequests, bulkUpdateGroupSize, productRequiredGaps, type BulkUpdateRecord } from './bulk-platform-update'

const trendyol = { id: 'ty', platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol' }
const hepsiburada = { id: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Ravencia HB' }
const live = (platform: string, platformCode: string) => ({ platform, platformCode, status: 'ACTIVE', matchedVariantCount: 3, variantCount: 3 })
const unlinked = (platform: string, platformCode: string) => ({ platform, platformCode, status: 'UNLINKED' })

function record(id: string, statuses: Product['platformStatuses'], extra: Partial<BulkUpdateRecord> = {}): BulkUpdateRecord {
  return { id, title: `Ürün ${id}`, status: 'ACTIVE', platformStatuses: statuses, gaps: [], ...extra }
}

describe('Platformlara Güncelle menu item', () => {
  it('is off with its reason while none of the selected products is on a store', () => {
    expect(bulkPlatformUpdateMenuState([{ platformStatuses: [unlinked('Ravencia Trendyol', 'TRENDYOL')] }, { platformStatuses: [] }], [trendyol])).toEqual({ disabled: true, detail: 'Seçili ürünlerin hiçbiri pazar yerinde yayında değil' })
  })
  it('is on when one selected product is live on one store', () => {
    expect(bulkPlatformUpdateMenuState([{ platformStatuses: [] }, { platformStatuses: [live('Ravencia HB', 'HEPSIBURADA')] }], [trendyol, hepsiburada])).toEqual({ disabled: false, detail: 'Seçili ürünlerin güncellemesini gönder' })
  })
  it('is off when nothing is selected or no store takes an update', () => {
    expect(bulkPlatformUpdateMenuState([], [trendyol]).disabled).toBe(true)
    expect(bulkPlatformUpdateMenuState([{ platformStatuses: [live('Ravencia Trendyol', 'TRENDYOL')] }], []).disabled).toBe(true)
  })
})

describe('bulkPlatformUpdateRequests', () => {
  let groups = 0
  const newGroupId = () => `group-${++groups}`

  it('sends only the picked parts, only to stores the product is on, and holds back gaps and drafts', () => {
    groups = 0
    const records = [
      record('a', [live('Ravencia Trendyol', 'TRENDYOL'), live('Ravencia HB', 'HEPSIBURADA')]),
      record('b', [live('Ravencia Trendyol', 'TRENDYOL'), unlinked('Ravencia HB', 'HEPSIBURADA')]),
      record('c', [live('Ravencia Trendyol', 'TRENDYOL')], { gaps: ['desi'] }),
      record('d', [live('Ravencia Trendyol', 'TRENDYOL')], { status: 'DRAFT' })
    ]
    const requests = bulkPlatformUpdateRequests(records, [trendyol, hepsiburada], ['ty', 'hb'], ['IMAGES', 'PRICE'], newGroupId)
    expect(requests).toEqual([
      { kind: 'product', productId: 'a', connectionId: 'ty', fields: ['IMAGES'], sendGroupId: 'group-1' },
      { kind: 'product', productId: 'b', connectionId: 'ty', fields: ['IMAGES'], sendGroupId: 'group-1' },
      { kind: 'priceInventory', productId: 'a', connectionId: 'ty', price: true, stock: false },
      { kind: 'priceInventory', productId: 'b', connectionId: 'ty', price: true, stock: false },
      // Hepsiburada takes the pictures in its product package; only product a is on it.
      { kind: 'product', productId: 'a', connectionId: 'hb', fields: ['IMAGES'] },
      { kind: 'priceInventory', productId: 'a', connectionId: 'hb', price: true, stock: false }
    ])
  })

  it('sends nothing to a store that was not picked, and price and stock alone make no product job', () => {
    const requests = bulkPlatformUpdateRequests([record('a', [live('Ravencia Trendyol', 'TRENDYOL'), live('Ravencia HB', 'HEPSIBURADA')])], [trendyol, hepsiburada], ['hb'], ['PRICE', 'STOCK'], newGroupId)
    expect(requests).toEqual([{ kind: 'priceInventory', productId: 'a', connectionId: 'hb', price: true, stock: true }])
  })

  it('keeps a Trendyol send group small', () => {
    groups = 0
    const records = Array.from({ length: bulkUpdateGroupSize + 1 }, (_, index) => record(`p${index}`, [live('Ravencia Trendyol', 'TRENDYOL')]))
    const requests = bulkPlatformUpdateRequests(records, [trendyol], ['ty'], ['TITLE'], newGroupId)
    expect(requests.filter(request => request.kind === 'product' && request.sendGroupId === 'group-1')).toHaveLength(bulkUpdateGroupSize)
    // The one card left over goes alone, with no group to wait for.
    expect(requests.at(-1)).toEqual({ kind: 'product', productId: `p${bulkUpdateGroupSize}`, connectionId: 'ty', fields: ['TITLE'] })
  })
})

describe('productRequiredGaps', () => {
  const variant = (overrides: Partial<Variant> = {}) => ({ sku: 'RYP-1', barcode: '869000', modelCode: 'RYP', desi: 2, listPrice: 500, salePrice: 400, ...overrides }) as Variant
  const product = (overrides: Partial<Product> = {}) => ({ title: 'Bluz', brandId: 'b', categoryId: 'c', modelCode: 'RYP', variants: [variant()], defaultListPrice: 500, defaultSalePrice: 400, warrantyMonths: 0, deliveryDays: 2, ...overrides }) as Product

  it('reads the editor required fields from a saved product', () => {
    expect(productRequiredGaps(product())).toEqual([])
    expect(productRequiredGaps(product({ brandId: null, variants: [variant({ desi: null })] }))).toEqual(['brandId', 'desi'])
    expect(productRequiredGaps(product({ defaultSalePrice: 0 }))).toEqual(['salePrice'])
  })
  it('asks no card codes of a product with variants (each row holds its own)', () => {
    expect(productRequiredGaps(product({ variants: [variant({ sku: '', barcode: null }), variant({ sku: 'RYP-2' })] }))).toEqual([])
  })
})
