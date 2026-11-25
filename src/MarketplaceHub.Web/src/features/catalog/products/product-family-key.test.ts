import { describe, expect, it } from 'vitest'
import { bulkPublishChunks } from './bulk-publish-run'
import { groupByFamily, productFamilyKey } from './product-family-key'

// Scan 25 Nov 2026 (D-3): a model is BRAND + MODEL CODE. The same model code under another brand is another model, so
// the list never folds the two brands' cards into one card and a bulk action on one never reaches the other.
const card = (id: string, brandId: string | null, modelCode: string | null, familyKey?: string) =>
  ({ id, brandId, modelCode, familyKey, variants: [{ modelCode }] })

describe('product family key', () => {
  it('keeps the same model code under two brands apart and folds case and separators like the server', () => {
    const cards = [card('a1', 'brand-a', 'MZ036-YNC'), card('b1', 'brand-b', 'MZ036YNC'), card('a2', 'brand-a', 'mz036 ync'), card('b2', 'brand-b', 'mz036ync')]
    expect(groupByFamily(cards).map(group => group.members.map(item => item.id))).toEqual([['a1', 'a2'], ['b1', 'b2']])
    expect(productFamilyKey(cards[0])).toBe('model:brand-a:MZ036-YNC')
  })

  it('takes the key the server sends over its own reading', () => {
    expect(productFamilyKey(card('a1', 'brand-a', 'X1', 'model:brand-z:X1'))).toBe('model:brand-z:X1')
    expect(productFamilyKey(card('a1', 'brand-a', null))).toBe('product:a1')
  })

  it('sends two brands with the same model code in separate bulk publish families', () => {
    const records = [card('a1', 'brand-a', 'MZ036YNC'), card('b1', 'brand-b', 'MZ036YNC'), card('a2', 'brand-a', 'MZ036YNC')] as never[]
    expect(bulkPublishChunks(['a1', 'b1', 'a2'], records, 2)).toEqual([['a1', 'a2'], ['b1']])
  })
})
