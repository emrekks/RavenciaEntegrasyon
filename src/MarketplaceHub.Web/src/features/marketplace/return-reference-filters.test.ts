import { describe, expect, it } from 'vitest'
import { matchesReturnReferenceFilters } from './return-reference-filters'

const claim = { platformCode: 'HEPSIBURADA', cargoProviderName: 'PttAVM', reasonCode: 'SIZE_TOO_LARGE', reasonText: 'Beden büyük geldi' }

describe('matchesReturnReferenceFilters', () => {
  it('combines platform, cargo, and reason filters', () => {
    expect(matchesReturnReferenceFilters(claim, { platforms: ['HEPSIBURADA'], cargo: 'PttAVM', reason: 'SIZE_TOO_LARGE' })).toBe(true)
    expect(matchesReturnReferenceFilters(claim, { platforms: ['TRENDYOL'], cargo: 'PttAVM', reason: 'SIZE_TOO_LARGE' })).toBe(false)
    expect(matchesReturnReferenceFilters(claim, { platforms: ['HEPSIBURADA'], cargo: 'Yurtiçi', reason: 'SIZE_TOO_LARGE' })).toBe(false)
    expect(matchesReturnReferenceFilters(claim, { platforms: ['HEPSIBURADA'], cargo: 'PttAVM', reason: 'OTHER' })).toBe(false)
  })

  it('matches the readable reason when a reason code is absent', () => {
    expect(matchesReturnReferenceFilters({ ...claim, reasonCode: null }, { platforms: [], cargo: 'ALL', reason: 'Beden büyük geldi' })).toBe(true)
  })
})
