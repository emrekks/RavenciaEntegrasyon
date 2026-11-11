import { describe, expect, it } from 'vitest'
import { editedSharedFields, familyDifferenceText, sharedFieldSnapshot, type SharedFieldValues } from './family-shared-fields'

const loaded: SharedFieldValues = {
  title: 'Kışlık Triko Bluz', description: '<p>Açıklama</p>', brandId: 'b1', categoryId: 'c1', vatRate: '10', vatIncluded: 'INCLUDED',
  warrantyMonths: '0', deliveryDays: '1', attributeSelections: { a1: ['v2', 'v1'] }, attributeTextValues: { a2: 'Pamuk' }
}

describe('the model-row fields a save names', () => {
  it('names only the fields the owner changed', () => {
    const initial = sharedFieldSnapshot(loaded)
    expect(editedSharedFields(initial, sharedFieldSnapshot(loaded))).toEqual([])
    // The same values in another order or with surrounding spaces are no change.
    expect(editedSharedFields(initial, sharedFieldSnapshot({ ...loaded, title: ' Kışlık Triko Bluz ', vatRate: '10.0', attributeSelections: { a1: ['v1', 'v2'] } }))).toEqual([])
    expect(editedSharedFields(initial, sharedFieldSnapshot({ ...loaded, title: 'Yeni Ad', vatIncluded: 'EXCLUDED', attributeTextValues: { a2: 'Yün' } }))).toEqual(['title', 'vat', 'attributes'])
  })

  it('says which colours differ on which field', () => {
    expect(familyDifferenceText([{ field: 'description', colors: ['Siyah', 'Kahverengi'] }, { field: 'vat', colors: ['Mürdüm'] }])).toBe('Renklerde farklı: Açıklama (Siyah, Kahverengi) · KDV (Mürdüm)')
    expect(familyDifferenceText([])).toBe('')
    expect(familyDifferenceText(null)).toBe('')
  })
})
