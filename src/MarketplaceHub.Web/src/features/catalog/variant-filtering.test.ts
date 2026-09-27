import { describe, expect, it } from 'vitest'
import { filterVariantsByOptions, selectVariantDraftsByKeys, type VariantOptionFilterGroup } from './variant-filtering'

const groups: VariantOptionFilterGroup[] = [
  { id: 'color', values: [{ id: 'bordo', value: 'Bordo' }, { id: 'gray', value: 'Gri' }, { id: 'navy', value: 'Lacivert' }] },
  { id: 'size', values: [{ id: 'xl', value: 'XL' }, { id: '2xl', value: '2XL' }, { id: '3xl', value: '3XL' }] },
]
const rows = [
  { key: 'bordo-2xl', color: 'Bordo', size: '2XL' },
  { key: 'bordo-xl', color: 'Bordo', size: 'XL' },
  { key: 'gray-3xl', color: 'Gri', size: '3XL' },
  { key: 'navy-2xl', color: 'Lacivert', size: '2XL' },
]
const matches = (row: typeof rows[number], group: VariantOptionFilterGroup, value: { id: string; value: string }) =>
  row[group.id === 'color' ? 'color' : 'size'].toLocaleLowerCase('tr-TR') === value.value.toLocaleLowerCase('tr-TR')

describe('variant color and size filters', () => {
  it('matches any selected value within a dimension and intersects color with size', () => {
    const filtered = filterVariantsByOptions(rows, groups, { color: ['bordo', 'gray'], size: ['2xl', '3xl'] }, matches)

    expect(filtered.map(row => row.key)).toEqual(['bordo-2xl', 'gray-3xl'])
  })

  it('returns all rows when no option filters are selected', () => {
    expect(filterVariantsByOptions(rows, groups, {}, matches)).toEqual(rows)
  })

  it('returns no rows for a selected combination that does not exist', () => {
    expect(filterVariantsByOptions(rows, groups, { color: ['gray'], size: ['xl'] }, matches)).toEqual([])
  })

  it('keeps a filtered platform-price save scoped to edited variant drafts', () => {
    const drafts = {
      'bordo-2xl:trendyol': { listPrice: '599', salePrice: '549' },
      'gray-3xl:trendyol': { listPrice: '699', salePrice: '649' },
      'navy-2xl:trendyol': { listPrice: '799', salePrice: '749' },
    }

    expect(selectVariantDraftsByKeys(drafts, ['bordo-2xl:trendyol', 'gray-3xl:trendyol']))
      .toEqual({
        'bordo-2xl:trendyol': drafts['bordo-2xl:trendyol'],
        'gray-3xl:trendyol': drafts['gray-3xl:trendyol'],
      })
  })
})
