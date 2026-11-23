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

  // Y1: the three-digit check ran on the raw text while parseTrNumber dropped the spaces later: "1, 250" read as 1.25.
  it('refuses a space inside the number, and allows it only around it', () => {
    for (const text of ['1, 250', '1,25 0', '1, 250', '1 ,250', '1 250', '1 250', '1.250, 50'])
      expect(parseTrEntry(text), JSON.stringify(text)).toBeNull()
    expect(parseTrEntry(' 1.250 ')).toBe(1250)
    expect(parseTrEntry('1.250 ₺')).toBe(1250)
    expect(parseTrEntry('₺1.250,50')).toBe(1250.5)
    expect(parseTrEntry('1.250 TL')).toBe(1250)
    expect(parseTrEntry(' 1.250,5 ')).toBe(1250.5)
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

// Review 23 Nov (Y1): the one rule of price and stock boxes, as a table. Accepted: optional thousands points grouped
// right (1.250, 12.500, 1.250.000), an optional decimal comma with at most two digits, whitespace and one ₺/TL/% only
// around the number. Everything else is refused (null), never turned into another number.
describe('parseTrEntry table', () => {
  const table: Array<[string, number | null]> = [
    ['0', 0], ['5', 5], ['1250', 1250], ['1.250', 1250], ['12.500', 12500], ['125.000', 125000], ['1.250.000', 1250000],
    ['1.250,50', 1250.5], ['1.250,5', 1250.5], ['1250,50', 1250.5], ['12,5', 12.5], ['0,5', 0.5], ['0,05', 0.05],
    ['631,10', 631.1], [' 1.250 ', 1250], [' 1.250,50 ', 1250.5], ['1.250 ₺', 1250], ['₺1.250,50', 1250.5],
    ['1.250 TL', 1250], ['1.250tl', 1250], ['12,5 %', 12.5],
    // A space, a sign or a letter inside the number.
    ['1, 250', null], ['1 ,250', null], ['1,25 0', null], ['1 250', null], ['1 250', null], ['1.250, 50', null],
    ['1₺250', null], ['1%250', null], ['12a', null], ['abc', null], ['1e3', null], ['0x10', null], ['Infinity', null],
    // A point that is not a rightly grouped thousands point, or English writing.
    ['1.25', null], ['12.5', null], ['1250.50', null], ['0.5', null], ['0.125', null], ['1.2500', null], ['12.50.000', null],
    ['1..250', null], ['1.250.5', null], ['1,250.50', null], ['.250', null], ['1.', null],
    // Commas: more than two decimals, two commas, nothing on one side.
    ['1,250', null], ['1,000', null], ['0,125', null], ['631,104', null], ['1.250,505', null], ['1,2,3', null],
    ['12,5,0', null], [',5', null], ['1250,', null], ['1.250,', null],
    // Signs and emptiness.
    ['-5', null], ['−1.250', null], ['-0,5', null], ['+5', null], ['', null], [' ', null], ['₺', null], ['TL', null]
  ]
  it.each(table)('%j -> %s', (text, expected) => {
    expect(parseTrEntry(text)).toBe(expected)
  })
  it('has at least 40 rows', () => expect(table.length).toBeGreaterThanOrEqual(40))
  // Shown = saved: the number a box shows formatted (tr-TR, two decimals) reads back as the number that is sent.
  it.each(table.filter(([, expected]) => expected !== null))('shows %j as the number it saves', text => {
    const value = parseTrEntry(text)!
    const shown = value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    expect(parseTrEntry(shown)).toBe(value)
  })
  it('refuses the same inside-number spaces and signs in measurements and filters', () => {
    for (const text of ['1, 250', '1 250', '1₺250', '1%250', '1,25 0'])
      for (const parse of [parseTrMeasure, parseTrNumber]) expect(parse(text), `${parse.name} ${text}`).toBeNull()
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
