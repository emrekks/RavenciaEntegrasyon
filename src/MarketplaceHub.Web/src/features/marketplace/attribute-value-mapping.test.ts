import { describe, expect, it } from 'vitest'
import { autoMatchScore, valueMatchScore } from '../../shared/value-matching'
import { attributeValueMappingNeedsSave, hasDirectReferenceValue, isTemplateFilledRow, suggestValueMappings, visiblePanelValues, planDirectReferenceValues, planMissingReferencePanelValues, planReferencePanelMappings, togglePanelValueReferenceSelection, updatePanelValueReferenceSelection, valueMappingRowClassName } from './attribute-value-mapping'

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

  it('plans same-name panel values only for empty mappings and skips existing or duplicate labels', () => {
    expect(planMissingReferencePanelValues(
      [{ id: 'winter', value: 'Kış' }],
      [
        { externalId: 'winter', name: 'Kış' },
        { externalId: 'summer', name: 'Yaz' },
        { externalId: 'summer-duplicate', name: ' Yaz ' },
        { externalId: 'autumn', name: 'Sonbahar' }
      ],
      { winter: ['winter'] }
    )).toEqual(['Yaz', 'Sonbahar'])
  })

  it('does not plan blank remote names as panel values', () => {
    expect(planMissingReferencePanelValues(
      [],
      [{ externalId: 'blank', name: '   ' }],
      {}
    )).toEqual([])
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
    expect(planReferencePanelMappings({ dress: ['panel-dress'], shirt: ['panel-shirt', 'panel-top'] })).toEqual([
      { localId: 'panel-dress', externalId: 'dress' },
      { localId: 'panel-shirt', externalId: 'shirt' },
      { localId: 'panel-top', externalId: 'shirt' }
    ])
  })

  it('lets one marketplace value take several panel values and toggles them one by one', () => {
    const first = togglePanelValueReferenceSelection({}, 'women', 'woman')
    const second = togglePanelValueReferenceSelection(first, 'women', 'girl')
    expect(second).toEqual({ women: ['woman', 'girl'] })
    expect(togglePanelValueReferenceSelection(second, 'women', 'woman')).toEqual({ women: ['girl'] })
    expect(togglePanelValueReferenceSelection({ women: ['girl'] }, 'women', 'girl')).toEqual({})
  })

  it('allows Hepsiburada selections to reuse the same panel value and supports clearing a selection', () => {
    expect(updatePanelValueReferenceSelection({ dress: ['panel-dress'] }, 'shirt', ['panel-dress'], true)).toEqual({ dress: ['panel-dress'], shirt: ['panel-dress'] })
    expect(updatePanelValueReferenceSelection({ dress: ['panel-dress'], shirt: ['panel-dress'] }, 'shirt', [])).toEqual({ dress: ['panel-dress'] })
  })

  it('keeps a panel value on one marketplace value on platforms that do not allow reuse', () => {
    expect(updatePanelValueReferenceSelection({ dress: ['panel-dress', 'panel-gown'] }, 'shirt', ['panel-dress'])).toEqual({ dress: ['panel-gown'], shirt: ['panel-dress'] })
  })
})

describe('template filled value rows', () => {
  it('shows the badge only while the saved template mappings are untouched', () => {
    const saved = [{ localId: 'siyah', source: 'TEMPLATE' }]
    expect(isTemplateFilledRow(saved, ['siyah'])).toBe(true)
    expect(isTemplateFilledRow(saved, ['siyah', 'antrasit'])).toBe(false)
    expect(isTemplateFilledRow(saved, [])).toBe(false)
    expect(isTemplateFilledRow([{ localId: 'siyah', source: 'MANUAL' }], ['siyah'])).toBe(false)
    expect(isTemplateFilledRow([], [])).toBe(false)
  })
})

describe('used panel values filter', () => {
  const values = [{ id: 'siyah' }, { id: 'beyaz' }, { id: 'mor' }, { id: 'turkuaz' }]

  it('lists the values products use and keeps selected ones visible', () => {
    const result = visiblePanelValues(values, ['siyah', 'beyaz'], ['turkuaz'], false)
    expect(result.visible.map(value => value.id)).toEqual(['siyah', 'beyaz', 'turkuaz'])
    expect(result.hiddenCount).toBe(1)
  })

  it('hides nothing when asked to or when no product uses the attribute', () => {
    expect(visiblePanelValues(values, ['siyah'], [], true)).toEqual({ visible: values, hiddenCount: 0 })
    expect(visiblePanelValues(values, [], [], false)).toEqual({ visible: values, hiddenCount: 0 })
    expect(visiblePanelValues(values, undefined, [], false)).toEqual({ visible: values, hiddenCount: 0 })
  })
})

describe('value mapping suggestions with confidence', () => {
  it('selects confident matches at once and leaves weaker ones for approval', () => {
    const unmatched = [{ id: 'black', value: 'Black' }, { id: 'laci', value: 'Laci' }, { id: 'mor', value: 'Mor' }]
    const remote = [{ externalId: '1', name: 'Siyah' }, { externalId: '2', name: 'Lacivert' }, { externalId: '3', name: 'Beyaz' }]

    const result = suggestValueMappings(unmatched, remote, valueMatchScore, autoMatchScore)

    expect(result.automatic.map(item => [item.localId, item.externalId, item.score])).toEqual([['black', '1', 90]])
    expect(result.review.map(item => [item.localId, item.externalId, item.score])).toEqual([['laci', '2', 70]])
  })
})

describe('platform bridge suggestions', () => {
  it('uses the name a panel value has on another marketplace', () => {
    const unmatched = [{ id: 'gece', value: 'Gece Mavisi' }]
    const remote = [{ externalId: 'hb-1', name: 'Lacivert' }, { externalId: 'hb-2', name: 'Mavi' }]

    const withoutBridge = suggestValueMappings(unmatched, remote, valueMatchScore, autoMatchScore)
    const withBridge = suggestValueMappings(unmatched, remote, valueMatchScore, autoMatchScore, { gece: ['Lacivert'] })

    expect(withoutBridge.automatic).toEqual([])
    expect(withBridge.automatic.map(item => [item.externalId, item.score])).toEqual([['hb-1', 92]])
  })
})
