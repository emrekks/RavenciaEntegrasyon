import { describe, expect, it } from 'vitest'
import { dateRangePresets, formatDateRange, selectDateRangeDay } from './date-range'

describe('date range selection', () => {
  it('selects a start and end date from one calendar flow', () => {
    expect(selectDateRangeDay({ from: '', to: '' }, '2026-10-03')).toEqual({ from: '2026-10-03', to: '' })
    expect(selectDateRangeDay({ from: '2026-10-03', to: '' }, '2026-10-08')).toEqual({ from: '2026-10-03', to: '2026-10-08' })
  })

  it('starts a new range when the next day is before the start or after a complete range', () => {
    expect(selectDateRangeDay({ from: '2026-10-03', to: '' }, '2026-10-01')).toEqual({ from: '2026-10-01', to: '' })
    expect(selectDateRangeDay({ from: '2026-10-03', to: '2026-10-08' }, '2026-10-10')).toEqual({ from: '2026-10-10', to: '' })
  })

  it('computes the ready ranges from an Istanbul calendar day', () => {
    // Wednesday 4 Nov 2026.
    expect(Object.fromEntries(dateRangePresets('2026-11-04').map(preset => [preset.label, preset.range]))).toEqual({
      'Bugün': { from: '2026-11-04', to: '2026-11-04' },
      'Dün': { from: '2026-11-03', to: '2026-11-03' },
      'Geçen Hafta': { from: '2026-10-26', to: '2026-11-01' },
      'Geçen Ay': { from: '2026-10-01', to: '2026-10-31' },
      'Bu Ay': { from: '2026-11-01', to: '2026-11-04' },
      'Bu Sene': { from: '2026-01-01', to: '2026-11-04' },
    })
    // A Monday on 1 March: last week is the whole previous Monday-to-Sunday, last month is February.
    const march = Object.fromEntries(dateRangePresets('2027-03-01').map(preset => [preset.label, preset.range]))
    expect(march['Geçen Hafta']).toEqual({ from: '2027-02-22', to: '2027-02-28' })
    expect(march['Geçen Ay']).toEqual({ from: '2027-02-01', to: '2027-02-28' })
    expect(march['Dün']).toEqual({ from: '2027-02-28', to: '2027-02-28' })
    // A Sunday on 1 January: last week ends the day before, last month is December of the year before.
    const newYear = Object.fromEntries(dateRangePresets('2028-01-02').map(preset => [preset.label, preset.range]))
    expect(newYear['Geçen Hafta']).toEqual({ from: '2027-12-20', to: '2027-12-26' })
    expect(newYear['Geçen Ay']).toEqual({ from: '2027-12-01', to: '2027-12-31' })
  })

  it('formats the selected dates for a single control', () => {
    expect(formatDateRange({ from: '2026-10-03', to: '2026-10-08' })).toBe('03.10.2026 – 08.10.2026')
  })
})
