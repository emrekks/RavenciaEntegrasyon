import { describe, expect, it } from 'vitest'
import { buildVariantAttributeAssignments } from './variant-attribute-assignments'

describe('buildVariantAttributeAssignments', () => {
  const requirements = [
    { attributeId: 'size', name: 'Beden', values: [{ id: 'size-l', value: 'L' }, { id: 'size-xl', value: 'XL' }] },
    { attributeId: 'color', name: 'Renk', values: [{ id: 'color-gray', value: 'Gri' }, { id: 'color-bordo', value: 'Bordo' }] }
  ]

  it('derives required option assignments synchronously when edit-state hydration has not run yet', () => {
    const result = buildVariantAttributeAssignments({
      options: { SIZE: ' L ', COLOR: 'GRİ' },
      selectedValueIds: {},
      existing: [],
      optionRequirements: requirements
    })

    expect(result).toEqual([
      { attributeId: 'size', valueId: 'size-l', textValue: null, numberValue: null, booleanValue: null, sortOrder: 0 },
      { attributeId: 'color', valueId: 'color-gray', textValue: null, numberValue: null, booleanValue: null, sortOrder: 1 }
    ])
  })

  it('uses the row option rather than a stale hydrated value and preserves non-option typed assignments', () => {
    const extra = { attributeId: 'material', valueId: null, textValue: 'Pamuk', numberValue: null, booleanValue: null, sortOrder: 8 }
    const result = buildVariantAttributeAssignments({
      options: { Beden: 'XL', Renk: 'Bordo' },
      selectedValueIds: { size: 'size-l', color: 'color-gray' },
      existing: [
        { attributeId: 'size', valueId: 'size-l', textValue: null, numberValue: null, booleanValue: null, sortOrder: 0 },
        { attributeId: 'color', valueId: 'color-gray', textValue: null, numberValue: null, booleanValue: null, sortOrder: 1 },
        extra
      ],
      optionRequirements: requirements
    })

    expect(result).toEqual([
      { attributeId: 'size', valueId: 'size-xl', textValue: null, numberValue: null, booleanValue: null, sortOrder: 0 },
      { attributeId: 'color', valueId: 'color-bordo', textValue: null, numberValue: null, booleanValue: null, sortOrder: 1 },
      extra
    ])
  })

  it('removes an old option assignment when that option is no longer on the row', () => {
    const result = buildVariantAttributeAssignments({
      options: { Beden: 'L' },
      selectedValueIds: {},
      existing: [{ attributeId: 'color', valueId: 'color-gray', textValue: null, numberValue: null, booleanValue: null, sortOrder: 1 }],
      optionRequirements: requirements
    })

    expect(result.map(item => [item.attributeId, item.valueId])).toEqual([['size', 'size-l']])
  })
})
