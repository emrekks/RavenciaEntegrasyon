import { describe, expect, it } from 'vitest'
import { readVariantMediaAssignmentDraft, updateVariantMediaAssignmentDraft, variantMediaAssignmentKey, variantMediaReconcileRequests, variantOwnerProductId } from './variant-media-assignments'

describe('variant media assignment drafts', () => {
  it('keeps each option value selection while switching between values', () => {
    let drafts = updateVariantMediaAssignmentDraft({}, variantMediaAssignmentKey('color', 'burgundy'), ['front', 'detail'])
    drafts = updateVariantMediaAssignmentDraft(drafts, variantMediaAssignmentKey('color', 'black'), ['black-front'])

    expect(readVariantMediaAssignmentDraft(drafts, variantMediaAssignmentKey('color', 'burgundy'))).toEqual(['front', 'detail'])
    expect(readVariantMediaAssignmentDraft(drafts, variantMediaAssignmentKey('color', 'black'))).toEqual(['black-front'])
  })

  it('starts untouched values from their existing assignments', () => {
    expect(readVariantMediaAssignmentDraft({}, variantMediaAssignmentKey('color', 'gray'), ['gray-front'])).toEqual(['gray-front'])
  })
})

describe('variant media reconcile requests', () => {
  const saved = { red: ['url|https://cdn/a.jpg'], blue: [] as string[] }

  it('sends only the saved variants whose pictures changed, in their order', () => {
    expect(variantMediaReconcileRequests([
      { key: 'red', optionSignature: 'Kırmızı', mediaRefs: ['url|https://cdn/a.jpg'] },
      { key: 'blue', optionSignature: 'Mavi', mediaRefs: ['url|https://cdn/b.jpg', 'url|https://cdn/a.jpg'] }
    ], saved)).toEqual([{ variantId: 'blue', optionSignature: 'Mavi', items: [{ url: 'https://cdn/b.jpg', sortOrder: 0 }, { url: 'https://cdn/a.jpg', sortOrder: 1 }] }])
  })

  it('waits for the product save when a variant is new or a picture is not uploaded yet', () => {
    expect(variantMediaReconcileRequests([{ key: 'new-row', optionSignature: 'Yeşil', mediaRefs: ['url|https://cdn/a.jpg'] }], saved)).toBeNull()
    expect(variantMediaReconcileRequests([{ key: 'blue', optionSignature: 'Mavi', mediaRefs: ['file|local-1'] }], saved)).toBeNull()
  })
})

describe('variantOwnerProductId', () => {
  const familyVariants = [{ id: 'v-indigo-s', productId: 'p-indigo' }, { id: 'v-red-s', productId: 'p-red' }, { id: 'v-legacy', productId: null }]

  it('saves a variant picture on the colour card the variant belongs to', () => {
    expect(variantOwnerProductId(familyVariants, 'v-red-s', 'p-indigo')).toBe('p-red')
    expect(variantOwnerProductId(familyVariants, 'v-indigo-s', 'p-indigo')).toBe('p-indigo')
  })

  it('falls back to the opened card when the variant names no card', () => {
    expect(variantOwnerProductId(familyVariants, 'v-legacy', 'p-indigo')).toBe('p-indigo')
    expect(variantOwnerProductId(familyVariants, 'v-unknown', 'p-indigo')).toBe('p-indigo')
  })
})
