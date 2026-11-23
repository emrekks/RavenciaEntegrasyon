import { describe, expect, it } from 'vitest'
import { parseTrCount, parseTrEntry, parseTrMeasure, parseTrNumber, trNumberInput } from './tr-number'

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

describe('parseTrEntry (the product screens\' one rule)', () => {
  it('reads the four Turkish forms', () => {
    expect(parseTrEntry('1.250')).toBe(1250)
    expect(parseTrEntry('1.250,50')).toBe(1250.5)
    expect(parseTrEntry('1250,5')).toBe(1250.5)
    expect(parseTrEntry('12,5')).toBe(12.5)
    expect(parseTrEntry('1.000.000')).toBe(1000000)
  })

  it('refuses a point used as a decimal point and English grouping', () => {
    for (const text of ['1.25', '12.5', '1250.50', '0.5', '1,250.50', '1.250.5', '0.125', '1e3', '12,5,0', '', '1,250', '1,000', '0,125', '1.250,505', '0x10'])
      expect(parseTrEntry(text), text).toBeNull()
  })

  it('reads measurements with any number of decimals, never a point used as a decimal point', () => {
    expect(parseTrMeasure('0,125')).toBe(0.125)
    expect(parseTrMeasure('1.250')).toBe(1250)
    expect(parseTrMeasure('1.25')).toBeNull()
  })

  // Every price with two decimals up to 2 million, sampled: what a box shows (formatted, or as the field starts it)
  // reads back as the same number, and the formatted text of that number is the same text again.
  it('round-trips display -> parse -> display for many prices', () => {
    const format = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    let seed = 7
    const next = () => { seed = (seed * 48271) % 2147483647; return seed }
    const samples = [0, 0.01, 0.1, 1, 1.25, 9.99, 12.5, 999.99, 1000, 1250, 1250.5, 99999.99, 1000000, 1999999.99]
    for (let index = 0; index < 3000; index++) samples.push((next() % 200000000) / 100)
    for (const value of samples) {
      const shown = format(value)
      expect(parseTrEntry(shown), shown).toBe(value)
      expect(format(parseTrEntry(shown)!), shown).toBe(shown)
      expect(parseTrEntry(trNumberInput(value)), String(value)).toBe(value)
      // The integer part with thousands points and no decimals ("1.250") reads as the whole number.
      const whole = Math.trunc(value)
      expect(parseTrEntry(whole.toLocaleString('tr-TR')), String(whole)).toBe(whole)
    }
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
    expect(parseTrCount('2.5')).toEqual({ ok: false, reason: 'invalid' })
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
