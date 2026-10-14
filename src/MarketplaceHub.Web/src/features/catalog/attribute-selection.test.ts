import { describe, expect, it } from 'vitest'
import { toggleProductAttributeValue } from './attribute-selection'

describe('product attribute selections', () => {
  it('allows optional values to be selected across more than three attributes', () => {
    const selections = ['material', 'season', 'product-type', 'pattern', 'fit']
      .reduce((current, attributeId) => toggleProductAttributeValue(current, attributeId, `${attributeId}-value`, true), {})

    expect(selections).toEqual({
      material: ['material-value'],
      season: ['season-value'],
      'product-type': ['product-type-value'],
      pattern: ['pattern-value'],
      fit: ['fit-value'],
    })
  })

  it('replaces the value for single-select attributes and toggles it off', () => {
    const initial = { season: ['summer'] }
    const replaced = toggleProductAttributeValue(initial, 'season', 'winter', true)

    expect(replaced.season).toEqual(['winter'])
    expect(initial.season).toEqual(['summer'])
    expect(toggleProductAttributeValue(replaced, 'season', 'winter', true).season).toEqual([])
  })

  it('adds and removes values for multi-select attributes', () => {
    const initial = { material: ['cotton'] }
    const added = toggleProductAttributeValue(initial, 'material', 'linen', false)

    expect(added.material).toEqual(['cotton', 'linen'])
    expect(toggleProductAttributeValue(added, 'material', 'cotton', false).material).toEqual(['linen'])
  })
})
