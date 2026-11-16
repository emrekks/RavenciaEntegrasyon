import { describe, expect, it } from 'vitest'
import { roundPercent } from './percent-rounding'

describe('roundPercent', () => {
  it('rounds half away from zero on both sides', () => {
    expect(roundPercent(12.5)).toBe(13)
    expect(roundPercent(-12.5)).toBe(-13)
    expect(roundPercent(-22.5, 0)).toBe(-23)
    expect(roundPercent(-0.25, 1)).toBe(-0.3)
    expect(roundPercent(0.25, 1)).toBe(0.3)
    expect(roundPercent(12.4)).toBe(12)
  })
  it('keeps a change that rounds to nothing without a minus', () => {
    expect(Object.is(roundPercent(-0.03, 1), -0)).toBe(true)
    expect(roundPercent(-0.03, 1) < 0).toBe(false)
  })
})
