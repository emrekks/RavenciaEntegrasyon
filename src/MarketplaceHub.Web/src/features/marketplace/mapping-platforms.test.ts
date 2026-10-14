import { describe, expect, it } from 'vitest'
import { activeMappingConnectionsForPlatform, mappingPlatformLabel } from './mapping-platforms'

describe('marketplace mapping platform selection', () => {
  it('returns only active connections for the selected platform, ignoring case', () => {
    const connections = [
      { id: 'hb-active', platformCode: 'HEPSIBURADA', status: 'ACTIVE' },
      { id: 'hb-verified', platformCode: 'hepsiburada', status: 'verified' },
      { id: 'hb-inactive', platformCode: 'HEPSIBURADA', status: 'INACTIVE' },
      { id: 'ty-active', platformCode: 'TRENDYOL', status: 'ACTIVE' }
    ]

    expect(activeMappingConnectionsForPlatform(connections, 'Hepsiburada').map(item => item.id))
      .toEqual(['hb-active', 'hb-verified'])
  })

  it('uses the marketplace display label and preserves unknown platform codes', () => {
    expect(mappingPlatformLabel('HEPSIBURADA')).toBe('Hepsiburada')
    expect(mappingPlatformLabel('custom-market')).toBe('custom-market')
  })
})
