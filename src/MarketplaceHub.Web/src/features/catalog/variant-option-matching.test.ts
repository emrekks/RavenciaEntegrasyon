import { describe, expect, it } from 'vitest'
import { mergeVariantOptionEntries, normalizeVariantOptionValue } from './variant-option-matching'

describe('variant option matching', () => {
  it('treats Turkish dotted and ASCII-uppercase I spellings as the same option value', () => {
    expect(normalizeVariantOptionValue('HAKI')).toBe(normalizeVariantOptionValue('Haki'))
    expect(normalizeVariantOptionValue('  Haki   ')).toBe(normalizeVariantOptionValue('Haki'))
  })

  it('keeps options missing from a partial variant signature', () => {
    expect(mergeVariantOptionEntries(
      [{ name: 'Renk', value: 'HAKI' }, { name: 'Beden', value: 'XL' }],
      [{ name: 'Beden', value: 'XL' }]
    )).toEqual([{ name: 'Renk', value: 'HAKI' }, { name: 'Beden', value: 'XL' }])
  })

  it('lets signature values override the same option in the fallback options map', () => {
    expect(mergeVariantOptionEntries(
      [{ name: 'Renk', value: 'Yeşil' }],
      [{ name: 'Renk', value: 'Haki' }]
    )).toEqual([{ name: 'Renk', value: 'Haki' }])
  })
})
