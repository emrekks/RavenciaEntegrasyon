import { describe, expect, it } from 'vitest'
import { filterValueCandidates, rankValueCandidates } from './value-mapping-suggestions'

const colours = [
  { externalId: '1', name: 'Siyah' },
  { externalId: '2', name: 'Siyah Melanj' },
  { externalId: '3', name: 'Lacivert' },
  { externalId: '4', name: 'Açık Siyah' },
  { externalId: '5', name: 'Beyaz' }
]

describe('value mapping suggestions', () => {
  it('puts the same name first, then close names, and leaves unrelated values out', () => {
    expect(rankValueCandidates('SİYAH', colours).map(item => item.externalId)).toEqual(['1', '2', '4'])
    expect(rankValueCandidates('Laci', colours).map(item => item.externalId)).toEqual(['3'])
    expect(rankValueCandidates('Mor', colours)).toEqual([])
  })

  it('searches the whole list by text', () => {
    expect(filterValueCandidates('siy', colours).map(item => item.externalId)).toEqual(['1', '2', '4'])
    expect(filterValueCandidates('  ', colours)).toEqual([])
  })
})
