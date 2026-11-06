import { describe, expect, it } from 'vitest'
import { filterAttributeOptionValues, optionValueToAdd } from './attribute-value-search'

const values = [
  { id: 'indigo', value: 'İndigo' },
  { id: 'brown', value: 'Kahverengi' },
  { id: 'navy', value: 'Lacivert' },
  { id: 'plum', value: 'Mürdüm' },
]

describe('attribute option value search', () => {
  it('filters values case-insensitively with Turkish casing rules', () => {
    expect(filterAttributeOptionValues(values, '  İNDİGO ')).toEqual([values[0]])
    expect(filterAttributeOptionValues(values, 'KAHVERENGİ')).toEqual([values[1]])
  })

  it('finds a value typed without its Turkish letters', () => {
    expect(filterAttributeOptionValues(values, 'murdum')).toEqual([values[3]])
    expect(filterAttributeOptionValues(values, 'INDIGO')).toEqual([values[0]])
  })

  it('returns all values for an empty query and no values when nothing matches', () => {
    expect(filterAttributeOptionValues(values, '   ')).toBe(values)
    expect(filterAttributeOptionValues(values, 'mor')).toEqual([])
  })
})

describe('a new option value from the search box', () => {
  it('offers a colour the group lacks, in Title Case', () => {
    expect(optionValueToAdd('Renk', values, '  zümrüt   yeşili ')).toBe('Zümrüt Yeşili')
    expect(optionValueToAdd('Renk', values, 'ZÜMRÜT')).toBe('Zümrüt')
  })

  it('offers a size in capitals and any other value as typed', () => {
    expect(optionValueToAdd('Beden', [{ id: 's', value: 'S' }], 'xxl')).toBe('XXL')
    expect(optionValueToAdd('Kalıp', [], 'Oversize fit')).toBe('Oversize fit')
  })

  it('offers nothing for an empty box or a value the group holds in any spelling', () => {
    expect(optionValueToAdd('Renk', values, '   ')).toBeNull()
    expect(optionValueToAdd('Renk', values, 'MURDUM')).toBeNull()
    expect(optionValueToAdd('Renk', values, 'indigo')).toBeNull()
    expect(optionValueToAdd('Beden', [{ id: 's', value: 'S' }], ' s ')).toBeNull()
  })
})
