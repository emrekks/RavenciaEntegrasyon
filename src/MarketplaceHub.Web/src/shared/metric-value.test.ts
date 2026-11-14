import { describe, expect, it } from 'vitest'
import { cardCountText, isAboveZero, noValue, toneAbove, valueWithUnit } from './metric-value'

describe('the one empty-value rule', () => {
  it('reads a real zero as 0 and no data source, a failure or an unknown as a dash', () => {
    expect(cardCountText(0)).toBe('0')
    expect(cardCountText(1234)).toBe('1.234')
    expect(cardCountText(0, { hasSource: false })).toBe(noValue)
    expect(cardCountText(undefined)).toBe(noValue)
    expect(cardCountText(null)).toBe(noValue)
    expect(cardCountText(5, { error: true })).toBe(noValue)
    expect(cardCountText(5, { loading: true })).toBe('…')
  })
  it('lets a semantic colour mark only a figure above nought', () => {
    expect(isAboveZero(0)).toBe(false)
    expect(toneAbove(0, 'warn')).toBeUndefined()
    expect(toneAbove(3, 'warn')).toBe('warn')
  })
  it('gives a real zero its unit word and a dash none', () => {
    expect(valueWithUnit(3, 'Paket')).toBe('3 Paket')
    expect(valueWithUnit(0, 'Paket')).toBe('0 Paket')
    expect(valueWithUnit(undefined, 'adet')).toBe('—')
  })
})
