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
  it('never shows a minus on a change that rounds to nothing', () => {
    // 99.97 against 100 is −0.03 %: shown as %0, not as a falling "-%0".
    expect(periodTrend(99.97, 100)).toEqual(['%0', true])
    expect(periodTrend(99.9, 100)).toEqual(['-%0,1', false])
  })
})
