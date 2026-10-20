import { describe, expect, it } from 'vitest'
import { formatDate, formatDateTime, formatIstanbul, formatTime, fromDateTimeLocal, istanbulDateKey, istanbulDayBoundary, istanbulOffsetMinutes, toDateTimeLocal } from './format-date'
import { formatMoney } from './format-money'

describe('Istanbul dates', () => {
  it('shows an instant in Istanbul time, whatever the browser clock', () => {
    expect(formatDateTime('2026-10-19T06:04:09Z')).toBe('19.10.2026 09:04:09')
    expect(formatDate('2026-10-18T22:30:00Z')).toBe('19.10.2026')
    expect(formatTime('2026-10-18T22:30:00Z')).toBe('01:30:00')
    expect(formatIstanbul('2026-10-19T06:04:09Z', { day: 'numeric', month: 'long' })).toBe('19 Ekim')
  })

  it('shows a dash (or the given text) for an empty or unreadable value', () => {
    expect(formatDateTime(null)).toBe('—')
    expect(formatDate('')).toBe('—')
    expect(formatDate('yarın', 'Bilinmiyor')).toBe('Bilinmiyor')
  })

  it('knows Istanbul is three hours ahead of UTC', () => {
    expect(istanbulOffsetMinutes(new Date('2026-01-15T12:00:00Z'))).toBe(180)
    expect(istanbulOffsetMinutes(new Date('2026-07-15T12:00:00Z'))).toBe(180)
  })

  it('names the Istanbul calendar day', () => {
    expect(istanbulDateKey('2026-10-18T21:30:00Z')).toBe('2026-10-19')
    expect(istanbulDateKey('2026-10-18T20:59:59Z')).toBe('2026-10-18')
  })
})

describe('date-time input round trip', () => {
  it('fills the input with Istanbul wall-clock time, not UTC', () => {
    expect(toDateTimeLocal('2026-10-19T06:04:00Z')).toBe('2026-10-19T09:04')
    expect(toDateTimeLocal(null)).toBe('')
  })

  it('saves what the person typed as the same instant', () => {
    expect(fromDateTimeLocal('2026-10-19T09:04')).toBe('2026-10-19T06:04:00.000Z')
    expect(fromDateTimeLocal('2026-10-19')).toBe('2026-10-18T21:00:00.000Z')
    expect(fromDateTimeLocal('19.10.2026 09:04')).toBe('')
  })

  it('does not move the time when an invoice date is opened and saved again', () => {
    let stored = '2026-10-19T06:04:00.000Z'
    for (let round = 0; round < 5; round += 1) stored = fromDateTimeLocal(toDateTimeLocal(stored))
    expect(stored).toBe('2026-10-19T06:04:00.000Z')
  })
})

describe('day boundaries for date filters', () => {
  it('starts and ends the day in Istanbul', () => {
    expect(istanbulDayBoundary('2026-10-19')).toBe('2026-10-19T00:00:00.000+03:00')
    expect(istanbulDayBoundary('2026-10-19', true)).toBe('2026-10-19T23:59:59.999+03:00')
    expect(new Date(istanbulDayBoundary('2026-10-19')).toISOString()).toBe('2026-10-18T21:00:00.000Z')
    expect(istanbulDayBoundary('19.10.2026')).toBe('')
  })
})

describe('money', () => {
  it('formats Turkish lira and other currencies the way the browser does', () => {
    expect(formatMoney(1234.5)).toBe((1234.5).toLocaleString('tr-TR', { style: 'currency', currency: 'TRY' }))
    expect(formatMoney(10, 'eur')).toBe((10).toLocaleString('tr-TR', { style: 'currency', currency: 'EUR' }))
    expect(formatMoney(1234.5, 'TRY', { maximumFractionDigits: 0 })).toBe((1234.5).toLocaleString('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 }))
  })

  it('does not throw for a currency code the browser does not know', () => {
    expect(() => (1).toLocaleString('tr-TR', { style: 'currency', currency: 'XX' })).toThrow(RangeError)
    expect(formatMoney(1234.5, 'XX')).toBe('1.234,50 XX')
    expect(formatMoney(5, '')).toBe(formatMoney(5, 'TRY'))
    expect(formatMoney(null)).toBe('—')
  })
})
