import { describe, expect, it } from 'vitest'
import { settleTrackedProductSyncJobs, trackProductSyncJobs } from '../product-sync-tracking'
import { productDeleteListing, productDeleteMoreText } from './product-delete-text'
import { loadProductsByIds, quickStockCellValue } from './quick-edit-products'

describe('quick edit products', () => {
  it('reads every product by id and keeps the page copy of one that cannot be read', async () => {
    const page = { a: { id: 'a', version: 1 }, b: { id: 'b', version: 1 } } as Record<string, { id: string; version: number }>
    const read = async (id: string) => { if (id === 'b') throw new Error('404'); return { id, version: 5 } }
    expect(await loadProductsByIds(['a', 'b', 'c'], read, id => page[id])).toEqual([{ id: 'a', version: 5 }, { id: 'b', version: 1 }, { id: 'c', version: 5 }])
  })

  it('holds a saved stock until the product read again carries a new one', () => {
    const saved = { value: 8, previousOnHand: 3 }
    // The variant still holds the stock from before the save: the cell keeps 8 (and saves against 8 next time).
    expect(quickStockCellValue(3, saved)).toEqual({ value: 8, saved })
    // The refetch arrived.
    expect(quickStockCellValue(8, saved)).toEqual({ value: 8, saved: null })
    // Someone else changed it meanwhile: the server value wins.
    expect(quickStockCellValue(5, saved)).toEqual({ value: 5, saved: null })
    expect(quickStockCellValue(4, null)).toEqual({ value: 4, saved: null })
  })
})

describe('delete confirmation listing', () => {
  it('names the first ten records with their model codes and counts the rest', () => {
    const ids = Array.from({ length: 13 }, (_, index) => `p${index}`)
    const listing = productDeleteListing(ids, id => id === 'p1' ? { title: 'Keten Gömlek', modelCode: null, variants: [{ modelCode: 'KG-01' }] } : id === 'p2' ? undefined : { title: `Ürün ${id}`, modelCode: `MC-${id}` })
    expect(listing.shown).toHaveLength(10)
    expect(listing.moreCount).toBe(3)
    expect(listing.shown[0]).toEqual({ id: 'p0', title: 'Ürün p0', modelCode: 'MC-p0' })
    expect(listing.shown[1]).toEqual({ id: 'p1', title: 'Keten Gömlek', modelCode: 'KG-01' })
    expect(listing.shown[2]).toEqual({ id: 'p2', title: 'Adı okunamayan kayıt', modelCode: null })
    expect(productDeleteMoreText(listing.moreCount)).toBe('ve 3 ürün daha')
    expect(productDeleteMoreText(0)).toBeNull()
  })
})

describe('product import tracking', () => {
  it('adds a second import to the first instead of replacing it, and refreshes as each job ends', () => {
    const tracked = trackProductSyncJobs(trackProductSyncJobs([], ['j1']), ['j2', 'j1'])
    expect(tracked).toEqual(['j1', 'j2'])
    const jobs = [{ id: 'j1', jobType: 'PRODUCT_SYNC', status: 'SUCCEEDED' }, { id: 'j2', jobType: 'PRODUCT_SYNC', status: 'RUNNING' }]
    expect(settleTrackedProductSyncJobs(tracked, jobs)).toEqual({ finished: ['j1'], remaining: ['j2'] })
    expect(settleTrackedProductSyncJobs(['j2'], jobs)).toEqual({ finished: [], remaining: ['j2'] })
  })
})
