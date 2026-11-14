import { describe, expect, it } from 'vitest'
import { dataStartsOn, periodTrend, type Performance } from './dashboard-performance'
import { previousHasData } from '../../shared/components/ChangePill'

describe('periodTrend', () => {
  it('reads a rise from nothing as Yeni and nothing against nothing as no change', () => {
    expect(periodTrend(1500, 0)).toEqual(['Yeni', true])
    expect(periodTrend(0, 1500)).toEqual(['-%100', false])
    expect(periodTrend(0, 0)).toBeNull()
  })
  it('gives the change in per cent with its direction', () => {
    expect(periodTrend(150, 100)).toEqual(['%50', true])
    expect(periodTrend(75, 100)).toEqual(['-%25', false])
  })
  it('never shows a minus on a change that rounds to nothing', () => {
    // 99.97 against 100 is −0.03 %: shown as %0, not as a falling "-%0".
    expect(periodTrend(99.97, 100)).toEqual(['%0', true])
    expect(periodTrend(99.9, 100)).toEqual(['-%0,1', false])
  })
})

describe('dataStartsOn', () => {
  const performance = (firstOrderDays?: Record<string, string> | null) => ({ firstOrderDays }) as unknown as Performance
  it('takes the chosen store\'s first order day, the earliest for every store, and says nothing for an older server', () => {
    const data = performance({ a: '2026-03-05', b: '2025-11-20' })
    expect(dataStartsOn(data, 'ALL')).toBe('2025-11-20')
    expect(dataStartsOn(data, 'a')).toBe('2026-03-05')
    expect(dataStartsOn(data, 'c')).toBeNull()
    expect(dataStartsOn(performance(undefined), 'ALL')).toBeUndefined()
  })
  it('does not compare "Bu Yıl" with a previous year the stores had no data for', () => {
    const data = performance({ a: '2026-03-05' })
    expect(previousHasData('2025-01-01', dataStartsOn(data, 'ALL'))).toBe(false)
    expect(previousHasData('2026-10-01', dataStartsOn(data, 'ALL'))).toBe(true)
    expect(previousHasData('2026-10-01', dataStartsOn(data, 'c'))).toBe(false)
    expect(previousHasData('2025-01-01', undefined)).toBe(true)
  })
})
