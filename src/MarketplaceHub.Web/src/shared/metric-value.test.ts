import { describe, expect, it } from 'vitest'
import { cardCountText, isAboveZero, noValue, toneAbove } from './metric-value'

describe('the one empty-value rule', () => {
  it('reads nothing to show as a dash, for a zero count as for an unknown one', () => {
    expect(cardCountText(0)).toBe(noValue)
    expect(cardCountText(undefined)).toBe(noValue)
    expect(cardCountText(null)).toBe(noValue)
    expect(cardCountText(1234)).toBe('1.234')
    expect(cardCountText(5, { error: true })).toBe(noValue)
    expect(cardCountText(5, { loading: true })).toBe('…')
  })
  it('lets a semantic colour mark only a figure above nought', () => {
    expect(isAboveZero(0)).toBe(false)
    expect(isAboveZero(undefined)).toBe(false)
    expect(toneAbove(0, 'warn')).toBeUndefined()
    expect(toneAbove(3, 'warn')).toBe('warn')
  })
})
