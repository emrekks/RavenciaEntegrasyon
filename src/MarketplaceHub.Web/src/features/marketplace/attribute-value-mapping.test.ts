import { describe, expect, it } from 'vitest'
import { attributeValueMappingNeedsSave, hasDirectReferenceValue, planDirectReferenceValues } from './attribute-value-mapping'

describe('attribute value reference mappings', () => {
  it('matches labels despite whitespace around season separators', () => {
    expect(hasDirectReferenceValue('Sonbahar / Kış', ['İlkbahar / Sonbahar', 'Sonbahar/Kış'])).toBe(true)
    expect(hasDirectReferenceValue('Sonbahar / Kış', ['İlkbahar / Sonbahar', 'Kış'])).toBe(false)
  })

  it('re-saves an unchanged mapping when the reference snapshot changes', () => {
    expect(attributeValueMappingNeedsSave({ externalId: 'winter', snapshotId: 'old' }, 'winter', 'new')).toBe(true)
    expect(attributeValueMappingNeedsSave({ externalId: 'winter', snapshotId: 'new' }, 'winter', 'new')).toBe(false)
    expect(attributeValueMappingNeedsSave(undefined, 'winter', 'new')).toBe(true)
    expect(attributeValueMappingNeedsSave({ externalId: 'winter', snapshotId: 'new' }, '', 'new')).toBe(false)
  })

  it('plans exact-name additions and mappings while skipping ambiguous reference labels', () => {
    expect(planDirectReferenceValues(
      [{ id: 'spring', value: 'İlkbahar / Sonbahar' }],
      [
        { externalId: 'spring', name: 'İlkbahar/Sonbahar' },
        { externalId: 'winter', name: 'Sonbahar / Kış' },
        { externalId: 'winter-2', name: 'Sonbahar/Kış' }
      ]
    )).toEqual({
      missingValues: [],
      mappings: [{ localId: 'spring', externalId: 'spring' }],
      ambiguousCount: 2
    })
  })

  it('plans absent unique Trendyol labels for one-click local creation', () => {
    expect(planDirectReferenceValues(
      [{ id: 'winter', value: 'Kış' }],
      [{ externalId: 'winter', name: 'Kış' }, { externalId: 'summer', name: 'Yaz' }]
    )).toEqual({
      missingValues: ['Yaz'],
      mappings: [{ localId: 'winter', externalId: 'winter' }],
      ambiguousCount: 0
    })
  })

  it('skips a reference label when multiple local values normalize to the same name', () => {
    expect(planDirectReferenceValues(
      [{ id: 'one', value: 'İ' }, { id: 'two', value: 'i' }],
      [{ externalId: 'letter', name: 'i' }]
    )).toEqual({ missingValues: [], mappings: [], ambiguousCount: 1 })
  })
})
