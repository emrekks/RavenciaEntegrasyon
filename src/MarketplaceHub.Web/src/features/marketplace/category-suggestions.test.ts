import { describe, expect, it } from 'vitest'
import { suggestCategoryMappings } from './category-suggestions'

const remotes = [
  { externalId: '1', name: 'Tişört', path: 'Giyim / Erkek / Üst Giyim / Tişört' },
  { externalId: '2', name: 'Tişört', path: 'Giyim / Kadın / Üst Giyim / Tişört' },
  { externalId: '3', name: 'Bluz', path: 'Giyim / Kadın / Üst Giyim / Bluz' },
  { externalId: '4', name: 'T-Shirt', path: 'Çocuk / Kız Çocuk / T-Shirt' },
  { externalId: '5', name: 'Telefon Kılıfı', path: 'Elektronik / Aksesuar / Telefon Kılıfı' }
]

describe('suggestCategoryMappings', () => {
  it('ranks the same leaf under the matching parent first', () => {
    const result = suggestCategoryMappings({ name: 'Tişört', path: 'Kadın / Tişört' }, remotes)
    expect(result.map(item => item.externalId)).toEqual(['2', '1', '4'])
  })

  it('treats spelling variants and plurals as the same leaf', () => {
    const result = suggestCategoryMappings({ name: 'T-Shirtler', path: 'Çocuk / T-Shirtler' }, remotes)
    expect(result[0]?.externalId).toBe('4')
    expect(suggestCategoryMappings({ name: 'Bluzlar', path: 'Bluzlar' }, remotes)[0]?.externalId).toBe('3')
  })

  it('ignores qualifiers in parentheses and reads combined leaves', () => {
    const result = suggestCategoryMappings({ name: 'Bluz (Hepsiburada)', path: 'Bluz (Hepsiburada)' }, [...remotes, { externalId: '6', name: 'Bluz & Tunik', path: 'Giyim / Bluz & Tunik' }])
    expect(result.slice(0, 2).map(item => item.score)).toEqual([70, 70])
    expect(suggestCategoryMappings({ name: 'Kot Pantolon', path: 'Kot Pantolon' }, [{ externalId: '7', name: 'Jeans', path: 'Giyim / Alt Giyim / Jeans' }, { externalId: '8', name: 'Pantolon', path: 'Giyim / Alt Giyim / Pantolon' }])[0]?.externalId).toBe('7')
  })

  it('returns nothing for unrelated categories and short prefixes', () => {
    expect(suggestCategoryMappings({ name: 'Ayakkabı', path: 'Ayakkabı' }, remotes)).toEqual([])
    expect(suggestCategoryMappings({ name: 'Tulum', path: 'Tulum' }, [{ externalId: '9', name: 'Tül', path: 'Hobi / Tül' }])).toEqual([])
  })

  it('does not offer a generic category when a category named like the panel one exists', () => {
    const modest = [
      { externalId: '10', name: 'Tesettür Gömlek', path: 'Kadın Giyim / Tesettür Giyim / Tesettür Gömlek' },
      { externalId: '11', name: 'Gömlek', path: 'Erkek Giyim / Gömlek' },
      { externalId: '12', name: 'Gömlek', path: 'Bebek Giyim / Erkek Bebek / Gömlek' }
    ]
    expect(suggestCategoryMappings({ name: 'Tesettür Gömlek', path: 'Tesettür Gömlek' }, modest).map(item => item.externalId)).toEqual(['10'])
    // without a close match the generic category is still better than nothing
    expect(suggestCategoryMappings({ name: 'Tesettür Gömlek', path: 'Tesettür Gömlek' }, modest.slice(1)).map(item => item.externalId)).toEqual(['11', '12'])
  })

  it('keeps at most the requested number of candidates', () => {
    expect(suggestCategoryMappings({ name: 'Tişört', path: 'Tişört' }, remotes, 2)).toHaveLength(2)
  })
})
