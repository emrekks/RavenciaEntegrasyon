import { afterEach, describe, expect, it, vi } from 'vitest'
import { jobDuration } from './job-duration'

describe('jobDuration', () => {
  afterEach(() => vi.useRealTimers())

  it('measures only the active attempt for a leased job', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-10T12:00:00.000Z'))

    expect(jobDuration(
      '2026-10-10T00:00:00.000Z',
      null,
      '2026-10-10T11:30:00.000Z',
      'LEASED'
    )).toBe('30 dk 0 sn')
  })

  it('keeps the full elapsed time for a completed job', () => {
    expect(jobDuration(
      '2026-10-10T11:00:00.000Z',
      '2026-10-10T11:05:30.000Z',
      null,
      'SUCCEEDED'
    )).toBe('5 dk 30 sn')
  })
})
