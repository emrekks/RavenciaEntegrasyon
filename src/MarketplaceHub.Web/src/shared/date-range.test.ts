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
    const presets = dateRangePresets('2026-11-04')
    expect(presets.map(preset => preset.label)).toEqual(['Bugün', 'Dün', 'Bu Hafta', 'Geçen Hafta', 'Bu Ay', 'Geçen Ay', 'Son 30 Gün', 'Bu Yıl'])
    expect(Object.fromEntries(presets.map(preset => [preset.label, preset.range]))).toEqual({
      'Bugün': { from: '2026-11-04', to: '2026-11-04' },
      'Dün': { from: '2026-11-03', to: '2026-11-03' },
      'Bu Hafta': { from: '2026-11-02', to: '2026-11-04' },
      'Geçen Hafta': { from: '2026-10-26', to: '2026-11-01' },
      'Bu Ay': { from: '2026-11-01', to: '2026-11-04' },
      'Geçen Ay': { from: '2026-10-01', to: '2026-10-31' },
      'Son 30 Gün': { from: '2026-10-06', to: '2026-11-04' },
      'Bu Yıl': { from: '2026-01-01', to: '2026-11-04' },
    })
    // A Monday on 1 March: last week is the whole previous Monday-to-Sunday, last month is February.
    const march = Object.fromEntries(dateRangePresets('2027-03-01').map(preset => [preset.label, preset.range]))
    expect(march['Geçen Hafta']).toEqual({ from: '2027-02-22', to: '2027-02-28' })
    expect(march['Geçen Ay']).toEqual({ from: '2027-02-01', to: '2027-02-28' })
    expect(march['Dün']).toEqual({ from: '2027-02-28', to: '2027-02-28' })
    // On a Monday "Bu Hafta" is that day alone; "Son 30 Gün" counts today as the 30th day.
    expect(march['Bu Hafta']).toEqual({ from: '2027-03-01', to: '2027-03-01' })
    expect(march['Son 30 Gün']).toEqual({ from: '2027-01-31', to: '2027-03-01' })
    // A Sunday on 1 January: last week ends the day before, last month is December of the year before.
    const newYear = Object.fromEntries(dateRangePresets('2028-01-02').map(preset => [preset.label, preset.range]))
    expect(newYear['Geçen Hafta']).toEqual({ from: '2027-12-20', to: '2027-12-26' })
    expect(newYear['Geçen Ay']).toEqual({ from: '2027-12-01', to: '2027-12-31' })
    // On a Sunday "Bu Hafta" runs back to the Monday before, across the year's end.
    expect(newYear['Bu Hafta']).toEqual({ from: '2027-12-27', to: '2028-01-02' })
    expect(newYear['Bu Yıl']).toEqual({ from: '2028-01-01', to: '2028-01-02' })
  })

  it('formats the selected dates for a single control', () => {
    expect(formatDateRange({ from: '2026-10-03', to: '2026-10-08' })).toBe('03.10.2026 – 08.10.2026')
  })
})
