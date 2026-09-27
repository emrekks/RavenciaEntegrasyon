import { describe, expect, it } from 'vitest'
import { formatColorOptionValue, matchingVariantOptionValues, mergeVariantOptionEntries, mergeVariantOptionValues, normalizeVariantOptionValue } from './variant-option-matching'

describe('variant option matching', () => {
  it('treats Turkish dotted and ASCII-uppercase I spellings as the same option value', () => {
    expect(normalizeVariantOptionValue('HAKI')).toBe(normalizeVariantOptionValue('Haki'))
    expect(normalizeVariantOptionValue('  Haki   ')).toBe(normalizeVariantOptionValue('Haki'))
  })

  it('formats color labels in Turkish title case', () => {
    expect(formatColorOptionValue('BORDO')).toBe('Bordo')
    expect(formatColorOptionValue('  AÇIK   MAVİ ')).toBe('Açık Mavi')
    expect(formatColorOptionValue('KIRMIZI-BEYAZ')).toBe('Kırmızı-Beyaz')
  })

  it('matches imported uppercase Turkish variant values to catalog values when inferring selections', () => {
    const catalogColors = [
      { id: 'bordo', value: 'Bordo' },
      { id: 'gri', value: 'Gri' },
      { id: 'haki', value: 'Haki' },
      { id: 'kahverengi', value: 'Kahverengi' },
      { id: 'lacivert', value: 'Lacivert' },
      { id: 'mor', value: 'Mor' },
      { id: 'siyah', value: 'Siyah' },
    ]

    expect(matchingVariantOptionValues(catalogColors, ['BORDO', 'GRI', 'HAKI', 'KAHVERENGI', 'LACIVERT', 'MOR', 'SIYAH']))
      .toEqual(catalogColors)
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

  it('merges category colors with variant-row colors while keeping one entry for normalized duplicates', () => {
    expect(mergeVariantOptionValues(
      [{ id: 'bordo-id', value: 'Bordo' }, { id: 'mor-id', value: 'Mor' }],
      [
        { id: 'gri', value: 'Gri' },
        { id: 'haki', value: 'HAKI' },
        { id: 'kahverengi', value: 'Kahverengi' },
        { id: 'lacivert', value: 'Lacivert' },
        { id: 'siyah', value: 'Siyah' },
        { id: 'gri-duplicate', value: 'GRI' },
      ]
    )).toEqual([
      { id: 'bordo-id', value: 'Bordo' },
      { id: 'mor-id', value: 'Mor' },
      { id: 'gri', value: 'Gri' },
      { id: 'haki', value: 'HAKI' },
      { id: 'kahverengi', value: 'Kahverengi' },
      { id: 'lacivert', value: 'Lacivert' },
      { id: 'siyah', value: 'Siyah' },
    ])
  })
})
