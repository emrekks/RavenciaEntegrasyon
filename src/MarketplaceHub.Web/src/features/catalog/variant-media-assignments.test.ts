import { describe, expect, it } from 'vitest'
import { readVariantMediaAssignmentDraft, updateVariantMediaAssignmentDraft, variantMediaAssignmentKey } from './variant-media-assignments'

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
