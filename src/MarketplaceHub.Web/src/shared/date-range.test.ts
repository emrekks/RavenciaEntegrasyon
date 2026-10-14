import { describe, expect, it } from 'vitest'
import { formatDateRange, selectDateRangeDay } from './date-range'

describe('date range selection', () => {
  it('selects a start and end date from one calendar flow', () => {
    expect(selectDateRangeDay({ from: '', to: '' }, '2026-10-03')).toEqual({ from: '2026-10-03', to: '' })
    expect(selectDateRangeDay({ from: '2026-10-03', to: '' }, '2026-10-08')).toEqual({ from: '2026-10-03', to: '2026-10-08' })
  })

  it('starts a new range when the next day is before the start or after a complete range', () => {
    expect(selectDateRangeDay({ from: '2026-10-03', to: '' }, '2026-10-01')).toEqual({ from: '2026-10-01', to: '' })
    expect(selectDateRangeDay({ from: '2026-10-03', to: '2026-10-08' }, '2026-10-10')).toEqual({ from: '2026-10-10', to: '' })
  })

  it('formats the selected dates for a single control', () => {
    expect(formatDateRange({ from: '2026-10-03', to: '2026-10-08' })).toBe('03.10.2026 – 08.10.2026')
  })
})
