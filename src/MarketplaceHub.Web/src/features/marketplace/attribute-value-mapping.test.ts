import { describe, expect, it } from 'vitest'
import { attributeValueMappingNeedsSave, hasDirectReferenceValue, planDirectReferenceValues, planReferencePanelMappings, updatePanelValueReferenceSelection, valueMappingRowClassName } from './attribute-value-mapping'

describe('attribute value reference mappings', () => {
  it('matches labels despite whitespace around season separators', () => {
    expect(hasDirectReferenceValue('Sonbahar / Kış', ['İlkbahar / Sonbahar', 'Sonbahar/Kış'])).toBe(true)
    expect(hasDirectReferenceValue('Sonbahar / Kış', ['İlkbahar / Sonbahar', 'Kış'])).toBe(false)
  })

  it('re-saves an unchanged mapping when the reference snapshot changes', () => {
    expect(attributeValueMappingNeedsSave({ localId: 'panel-winter', externalId: 'winter', snapshotId: 'old' }, 'panel-winter', 'winter', 'new')).toBe(true)
    expect(attributeValueMappingNeedsSave({ localId: 'panel-winter', externalId: 'winter', snapshotId: 'new' }, 'panel-winter', 'winter', 'new')).toBe(false)
    expect(attributeValueMappingNeedsSave(undefined, 'panel-winter', 'winter', 'new')).toBe(true)
    expect(attributeValueMappingNeedsSave({ localId: 'panel-winter', externalId: 'winter', snapshotId: 'new' }, 'panel-winter', '', 'new')).toBe(false)
  })

  it('maps exact-name panel values to unique Trendyol values while skipping ambiguous reference labels', () => {
    expect(planDirectReferenceValues(
      [
        { id: 'spring', value: 'İlkbahar / Sonbahar' },
        { id: 'winter', value: 'Sonbahar / Kış' }
      ],
      [
        { externalId: 'spring', name: 'İlkbahar/Sonbahar' },
        { externalId: 'winter', name: 'Sonbahar / Kış' },
        { externalId: 'winter-2', name: 'Sonbahar/Kış' }
      ]
    )).toEqual({
      mappings: [{ localId: 'spring', externalId: 'spring' }],
      ambiguousCount: 2
    })
  })

  it('does not create panel values when no exact local value exists', () => {
    expect(planDirectReferenceValues(
      [{ id: 'winter', value: 'Kış' }],
      [{ externalId: 'winter', name: 'Kış' }, { externalId: 'summer', name: 'Yaz' }]
    )).toEqual({
      mappings: [{ localId: 'winter', externalId: 'winter' }],
      ambiguousCount: 0
    })
  })

  it('skips a reference label when multiple local values normalize to the same name', () => {
    expect(planDirectReferenceValues(
      [{ id: 'one', value: 'İ' }, { id: 'two', value: 'i' }],
      [{ externalId: 'letter', name: 'i' }]
    )).toEqual({ mappings: [], ambiguousCount: 1 })
  })

  it('marks empty required and optional rows with separate validation styles', () => {
    expect(valueMappingRowClassName(false, true)).toBe('value-mapping-row is-empty is-required')
    expect(valueMappingRowClassName(false, false)).toBe('value-mapping-row is-empty is-optional')
    expect(valueMappingRowClassName(true, true)).toBe('value-mapping-row is-required')
  })

  it('stores marketplace values as keys for their selected panel values', () => {
    expect(planReferencePanelMappings({ dress: 'panel-dress', shirt: 'panel-shirt' })).toEqual([
      { localId: 'panel-dress', externalId: 'dress' },
      { localId: 'panel-shirt', externalId: 'shirt' }
    ])
  })

  it('allows Hepsiburada selections to reuse the same panel value and supports clearing a selection', () => {
    expect(updatePanelValueReferenceSelection(
      { dress: 'panel-dress' },
      'shirt',
      'panel-dress',
      true
    )).toEqual({ dress: 'panel-dress', shirt: 'panel-dress' })
    expect(updatePanelValueReferenceSelection(
      { dress: 'panel-dress', shirt: 'panel-dress' },
      'shirt',
      ''
    )).toEqual({ dress: 'panel-dress' })
  })

  it('keeps selections exclusive on platforms that do not allow reuse', () => {
    expect(updatePanelValueReferenceSelection(
      { dress: 'panel-dress' },
      'shirt',
      'panel-dress'
    )).toEqual({ shirt: 'panel-dress' })
  })
})
