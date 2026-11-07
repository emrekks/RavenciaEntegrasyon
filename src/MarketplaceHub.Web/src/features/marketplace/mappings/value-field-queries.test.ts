import { describe, expect, it } from 'vitest'
import { mappedLocalIds, valueMappingProgressQuery, valueMappingsQuery } from './value-field-queries'

// 7 Nov 2026: the category window read one value list per mapped field to count its progress (31 requests, 350 KB for
// Elbise); it now reads the whole category's progress once and counts a field from its own list once that is newer.
describe('value progress of a field', () => {
  const progress = { data: { '179': ['a', 'b'] }, dataUpdatedAt: 2_000 }

  it('counts from the category read while the field has no list of its own', () => {
    expect(mappedLocalIds(undefined, progress, '179')).toEqual(['a', 'b'])
    expect(mappedLocalIds({ data: undefined, dataUpdatedAt: 0 }, progress, '338')).toEqual([])
  })

  it('counts from the field’s own list once it is at least as new (a save in the window)', () => {
    expect(mappedLocalIds({ data: [{ localId: 'a' }, { localId: 'b' }, { localId: 'c' }], dataUpdatedAt: 3_000 }, progress, '179')).toEqual(['a', 'b', 'c'])
    expect(mappedLocalIds({ data: [{ localId: 'a' }], dataUpdatedAt: 1_000 }, progress, '179')).toEqual(['a', 'b'])
  })

  it('stays unknown while neither has arrived', () => {
    expect(mappedLocalIds(undefined, { data: undefined, dataUpdatedAt: 0 }, '179')).toBeNull()
  })

  it('sits under the value lists’ key prefix, so invalidating them all reads it again, but not under a single field’s', () => {
    const all = valueMappingProgressQuery('c1', '1182').queryKey
    expect(all.slice(0, 2)).toEqual(['attribute-value-mappings', 'c1'])
    expect(all[2]).not.toBe(valueMappingsQuery('c1', '1182/179').queryKey[2])
  })
})
