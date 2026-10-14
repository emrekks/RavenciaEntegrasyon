import { describe, expect, it } from 'vitest'
import { failureRate, formatMetricSeconds } from './JobQueueMetrics'

describe('job queue metrics formatting', () => {
  it('formats seconds into short readable units', () => {
    expect(formatMetricSeconds(null)).toBe('—')
    expect(formatMetricSeconds(0.4)).toBe('<1 sn')
    expect(formatMetricSeconds(42)).toBe('42 sn')
    expect(formatMetricSeconds(157)).toBe('2,6 dk')
    expect(formatMetricSeconds(7200)).toBe('2 sa')
  })

  it('computes the failure rate only when there were attempts', () => {
    const base = { jobType: 'X', averageWaitSeconds: null, p95WaitSeconds: null, averageDurationSeconds: null, p95DurationSeconds: null, queued: 0, running: 0 }
    expect(failureRate({ ...base, attempts: 0, failed: 0 })).toBeNull()
    expect(failureRate({ ...base, attempts: 10, failed: 1 })).toBe(0.1)
  })
})
