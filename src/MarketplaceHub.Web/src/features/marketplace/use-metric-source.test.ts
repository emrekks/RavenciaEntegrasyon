import { describe, expect, it } from 'vitest'
import { hasMetricSource } from './use-metric-source'

const trendyol = { id: 'a', platformCode: 'TRENDYOL', status: 'ACTIVE' }
const hepsiburada = { id: 'b', platformCode: 'HEPSIBURADA', status: 'PASSIVE' }

describe('hasMetricSource', () => {
  it('needs an active marketplace connection', () => {
    expect(hasMetricSource([trendyol])).toBe(true)
    expect(hasMetricSource([hepsiburada])).toBe(false)
    expect(hasMetricSource([])).toBe(false)
  })
  it('respects a platform filter and a single connection', () => {
    expect(hasMetricSource([trendyol, hepsiburada], { platforms: ['HEPSIBURADA'] })).toBe(false)
    expect(hasMetricSource([trendyol, hepsiburada], { platforms: ['trendyol'] })).toBe(true)
    expect(hasMetricSource([trendyol], { connectionId: 'a' })).toBe(true)
    expect(hasMetricSource([trendyol], { connectionId: 'zz' })).toBe(false)
    expect(hasMetricSource([trendyol], { connectionId: 'ALL' })).toBe(true)
  })
  it('reads returns from the connections that can sync them', () => {
    expect(hasMetricSource([trendyol], { kind: 'returns' })).toBe(true)
    expect(hasMetricSource([hepsiburada], { kind: 'returns' })).toBe(false)
  })
})
