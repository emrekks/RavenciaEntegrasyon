import { describe, expect, it } from 'vitest'
import { syncPolicyHealthPresentation } from './sync-policy-health'

describe('syncPolicyHealthPresentation', () => {
  it('surfaces cursor stagnation even when the schedule health is healthy', () => {
    expect(syncPolicyHealthPresentation({ enabled: true, healthStatus: 'HEALTHY', cursorProgressStatus: 'STALLED' })).toEqual({
      label: 'İmleç durgun',
      tone: 'stalled',
      stalled: true,
    })
  })

  it('keeps an offline flow higher priority while preserving its stalled warning', () => {
    expect(syncPolicyHealthPresentation({ enabled: true, healthStatus: 'OFFLINE', cursorProgressStatus: 'STALLED' })).toEqual({
      label: 'Çevrim dışı',
      tone: 'offline',
      stalled: true,
    })
  })

  it('does not alert for disabled or no-data flows', () => {
    expect(syncPolicyHealthPresentation({ enabled: false, healthStatus: 'HEALTHY', cursorProgressStatus: 'STALLED' })).toEqual({
      label: 'Kapalı',
      tone: 'disabled',
      stalled: false,
    })
    expect(syncPolicyHealthPresentation({ enabled: true, healthStatus: 'HEALTHY', cursorProgressStatus: 'NO_DATA' })).toEqual({
      label: 'Güncel',
      tone: 'healthy',
      stalled: false,
    })
  })
})
