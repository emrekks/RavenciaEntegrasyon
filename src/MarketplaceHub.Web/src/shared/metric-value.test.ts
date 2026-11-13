import { describe, expect, it } from 'vitest'
import { countText, isAboveZero, noValue, readCountText, toneAbove } from './metric-value'

describe('the one empty-value rule', () => {
  it('reads a known zero as 0 and an unknown value as a dash', () => {
    expect(countText(0)).toBe('0')
    expect(countText(1234)).toBe('1.234')
    expect(countText(undefined)).toBe(noValue)
    expect(countText(null)).toBe(noValue)
    expect(countText(5, { error: true })).toBe(noValue)
    expect(countText(5, { loading: true })).toBe('…')
  })
  it('counts a read list that leaves a counter out as zero, but not a failed or loading read', () => {
    expect(readCountText(undefined)).toBe('0')
    expect(readCountText(undefined, { error: true })).toBe(noValue)
    expect(readCountText(undefined, { loading: true })).toBe('…')
  })
  it('lets a semantic colour mark only a figure above nought', () => {
    expect(isAboveZero(0)).toBe(false)
    expect(isAboveZero(undefined)).toBe(false)
    expect(toneAbove(0, 'warn')).toBeUndefined()
    expect(toneAbove(3, 'warn')).toBe('warn')
  })
})
