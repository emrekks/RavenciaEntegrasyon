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

  it('keeps persisted color values over stale signature values and Web Color', () => {
    expect(mergeVariantOptionEntries(
      [{ name: 'Renk', value: 'Indigo' }, { name: 'Beden', value: 'L' }],
      [{ name: 'Renk', value: 'Lacivert' }, { name: 'Web Color', value: 'Lacivert' }]
    )).toEqual([{ name: 'Renk', value: 'Indigo' }, { name: 'Beden', value: 'L' }])
  })

  it('keeps Web Color only when no real color option is available', () => {
    expect(mergeVariantOptionEntries(
      [],
      [{ name: 'Web Color', value: 'Indigo' }, { name: 'Beden', value: 'L' }]
    )).toEqual([{ name: 'Web Color', value: 'Indigo' }, { name: 'Beden', value: 'L' }])
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
