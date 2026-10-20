import { describe, expect, it } from 'vitest'
import { previousRange, rangeDates } from './DashboardPage'

const day = (value: Date) => `${value.getFullYear()}-${value.getMonth() + 1}-${value.getDate()}`

describe('dashboard periods', () => {
  const now = new Date(2026, 9, 20, 14, 30)

  it('today runs from midnight to now and is compared with the whole of yesterday', () => {
    const today = rangeDates('today', '', '', now)
    expect(today.start).toEqual(new Date(2026, 9, 20))
    expect(today.end).toEqual(now)
    const previous = previousRange(today.start, today.end)
    expect(day(previous.start)).toBe('2026-10-19')
    expect(day(previous.end)).toBe('2026-10-19')
  })

  it('the last 7 days are compared with the 7 days right before them', () => {
    const week = rangeDates('7', '', '', now)
    expect(day(week.start)).toBe('2026-10-14')
    const previous = previousRange(week.start, week.end)
    expect(day(previous.start)).toBe('2026-10-7')
    expect(day(previous.end)).toBe('2026-10-13')
  })

  it('a custom period is compared with as many days before it', () => {
    const custom = rangeDates('custom', '2026-10-01', '2026-10-10', now)
    const previous = previousRange(custom.start, custom.end)
    expect(day(previous.start)).toBe('2026-9-21')
    expect(day(previous.end)).toBe('2026-9-30')
  })
})
