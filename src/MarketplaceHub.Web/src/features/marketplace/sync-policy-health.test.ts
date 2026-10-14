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

  it('explains stale enabled flows without implying the marketplace connection is offline', () => {
    expect(syncPolicyHealthPresentation({ enabled: true, lastSuccessAt: '2026-10-08T20:52:50Z', healthStatus: 'OFFLINE', cursorProgressStatus: 'STALLED' })).toEqual({
      label: 'Güncelleme gecikti',
      tone: 'offline',
      stalled: true,
    })
    expect(syncPolicyHealthPresentation({ enabled: true, lastSuccessAt: null, healthStatus: 'OFFLINE' })).toEqual({
      label: 'İlk çalışma bekliyor',
      tone: 'offline',
      stalled: false,
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
