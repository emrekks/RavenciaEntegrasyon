import { describe, expect, it } from 'vitest'
import { filterAttributeOptionValues } from './attribute-value-search'

const values = [
  { id: 'indigo', value: 'İndigo' },
  { id: 'brown', value: 'Kahverengi' },
  { id: 'navy', value: 'Lacivert' },
]

describe('attribute option value search', () => {
  it('filters values case-insensitively with Turkish casing rules', () => {
    expect(filterAttributeOptionValues(values, '  İNDİGO ')).toEqual([values[0]])
    expect(filterAttributeOptionValues(values, 'KAHVERENGİ')).toEqual([values[1]])
  })

  it('returns all values for an empty query and no values when nothing matches', () => {
    expect(filterAttributeOptionValues(values, '   ')).toBe(values)
    expect(filterAttributeOptionValues(values, 'mor')).toEqual([])
  })
})
