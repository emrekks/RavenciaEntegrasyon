import { describe, expect, it } from 'vitest'
import { parseTrCount, parseTrNumber, trNumberInput } from './tr-number'

describe('parseTrNumber', () => {
  it('reads Turkish thousands points and decimal commas', () => {
    expect(parseTrNumber('1.250')).toBe(1250)
    expect(parseTrNumber('1.250.000')).toBe(1250000)
    expect(parseTrNumber('1.250,50')).toBe(1250.5)
    expect(parseTrNumber('1250,5')).toBe(1250.5)
    expect(parseTrNumber('0,125')).toBe(0.125)
    expect(parseTrNumber('1250')).toBe(1250)
    expect(parseTrNumber(' 1.249,90 ₺')).toBe(1249.9)
    expect(parseTrNumber('%12,5')).toBe(12.5)
  })

  it('takes a single point with one or two digits after it as a decimal point', () => {
    expect(parseTrNumber('1250.50')).toBe(1250.5)
    expect(parseTrNumber('12.5')).toBe(12.5)
    expect(parseTrNumber('1.25')).toBe(1.25)
  })

  it('keeps a leading minus', () => {
    expect(parseTrNumber('-12,5')).toBe(-12.5)
    expect(parseTrNumber('−1.250')).toBe(-1250)
  })

  it('refuses what could mean two things or is not a number', () => {
    for (const text of ['', ' ', '-', '0.125', '1,250.50', '1.25.0', '1,2,3', '12a', '1.2500', '12.345.6', ',5', '1250,', '1.250,', '1..250', 'abc', '1e3', 'Infinity'])
      expect(parseTrNumber(text), text).toBeNull()
  })
})

describe('parseTrCount', () => {
  it('reads whole numbers with thousands points', () => {
    expect(parseTrCount('1.250')).toEqual({ ok: true, value: 1250 })
    expect(parseTrCount('0')).toEqual({ ok: true, value: 0 })
  })

  it('says why a value is not a count', () => {
    expect(parseTrCount('')).toEqual({ ok: false, reason: 'empty' })
    expect(parseTrCount('2,5')).toEqual({ ok: false, reason: 'fraction' })
    expect(parseTrCount('2.5')).toEqual({ ok: false, reason: 'fraction' })
    expect(parseTrCount('-3')).toEqual({ ok: false, reason: 'negative' })
    expect(parseTrCount('üç')).toEqual({ ok: false, reason: 'invalid' })
  })
})

describe('trNumberInput', () => {
  it('writes a number so that it reads back the same', () => {
    expect(trNumberInput(1250.5)).toBe('1250,5')
    expect(trNumberInput(1.125)).toBe('1,125')
    expect(parseTrNumber(trNumberInput(1.125))).toBe(1.125)
    expect(trNumberInput(null)).toBe('')
  })
})
