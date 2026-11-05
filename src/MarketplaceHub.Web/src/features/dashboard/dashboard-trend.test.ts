import { describe, expect, it } from 'vitest'
import { periodTrend } from './dashboard-performance'

describe('periodTrend', () => {
  it('tells no change when the previous period had nothing, instead of a made-up %100', () => {
    expect(periodTrend(1500, 0)).toBeNull()
    expect(periodTrend(0, 0)).toBeNull()
  })
  it('gives the change in per cent with its direction', () => {
    expect(periodTrend(150, 100)).toEqual(['%50', true])
    expect(periodTrend(75, 100)).toEqual(['-%25', false])
  })
})
