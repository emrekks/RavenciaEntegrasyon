import { describe, expect, it } from 'vitest'
import { parseFigure } from './CountUp'

describe('count up figures', () => {
  it('reads a figure the Turkish way around its prefix and suffix', () => {
    expect(parseFigure('984')).toEqual({ prefix: '', value: 984, decimals: 0, suffix: '' })
    expect(parseFigure('1.284')).toEqual({ prefix: '', value: 1284, decimals: 0, suffix: '' })
    expect(parseFigure('₺400.479,78')).toEqual({ prefix: '₺', value: 400479.78, decimals: 2, suffix: '' })
    expect(parseFigure('%38,7')).toEqual({ prefix: '%', value: 38.7, decimals: 1, suffix: '' })
  })

  it('leaves text that is not one figure alone', () => {
    expect(parseFigure('…')).toBeNull()
    expect(parseFigure('—')).toBeNull()
    expect(parseFigure('Tutar yok')).toBeNull()
    expect(parseFigure('3 / 12')).toBeNull()
  })
})
