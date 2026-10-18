import { describe, expect, it } from 'vitest'
import { failureRate, formatMetricSeconds, quotaFullness } from './JobQueueMetrics'

describe('job queue metrics formatting', () => {
  it('formats seconds into short readable units', () => {
    expect(formatMetricSeconds(null)).toBe('—')
    expect(formatMetricSeconds(0.4)).toBe('<1 sn')
    expect(formatMetricSeconds(42)).toBe('42 sn')
    expect(formatMetricSeconds(157)).toBe('2,6 dk')
    expect(formatMetricSeconds(7200)).toBe('2 sa')
  })

  it('measures quota fullness as the hourly average against the limit', () => {
    const usage = { platformCode: 'TRENDYOL', storeName: 'Mağaza', lane: 'FOREGROUND' as const, limitPerMinute: 12, lastHourRequests: 180, peakMinuteRequests: 9, averagePerMinute: 3 }
    expect(quotaFullness(usage)).toBe(0.25)
    expect(quotaFullness({ ...usage, averagePerMinute: 30 })).toBe(1)
    expect(quotaFullness({ ...usage, limitPerMinute: 0 })).toBeNull()
  })

  it('computes the failure rate only when there were attempts', () => {
    const base = { jobType: 'X', averageWaitSeconds: null, p95WaitSeconds: null, averageDurationSeconds: null, p95DurationSeconds: null, queued: 0, running: 0 }
    expect(failureRate({ ...base, attempts: 0, failed: 0 })).toBeNull()
    expect(failureRate({ ...base, attempts: 10, failed: 1 })).toBe(0.1)
  })
})
