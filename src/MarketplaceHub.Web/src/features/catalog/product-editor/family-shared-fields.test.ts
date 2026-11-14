import { describe, expect, it } from 'vitest'
import { FAMILY_SHARED_FIELDS } from './family-shared-fields'

describe('the model fields every save names', () => {
  it('names every Temel Ürün Bilgileri field in the server names, and no variant field', () => {
    expect([...FAMILY_SHARED_FIELDS]).toEqual(['title', 'description', 'brand', 'category', 'vat', 'warranty', 'deliveryDays', 'attributes'])
    for (const variantField of ['barcode', 'sku', 'stock', 'salePrice', 'listPrice', 'desi', 'media']) expect(FAMILY_SHARED_FIELDS).not.toContain(variantField)
  })
})
