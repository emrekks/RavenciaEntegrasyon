import { describe, expect, it } from 'vitest'
import { smoothPath } from './smooth-path'

describe('smoothPath', () => {
  it('draws straight segments for one or two points', () => {
    expect(smoothPath([])).toBe('')
    expect(smoothPath([[0, 10]])).toBe('M0.0 10.0')
    expect(smoothPath([[0, 10], [10, 20]])).toBe('M0.0 10.0 L10.0 20.0')
  })

  it('passes through every point with one curve per gap', () => {
    const path = smoothPath([[0, 50], [10, 10], [20, 40], [30, 40]])
    expect(path.startsWith('M0.0 50.0')).toBe(true)
    expect(path.match(/C/g)).toHaveLength(3)
    expect(path).toContain('10.0 10.0 C')
    expect(path.endsWith('30.0 40.0')).toBe(true)
  })

  it('never overshoots a peak or a flat run', () => {
    const path = smoothPath([[0, 50], [10, 10], [20, 50], [30, 50], [40, 50]])
    const ys = [...path.matchAll(/-?\d+\.\d (-?\d+\.\d)/g)].map(match => Number(match[1]))
    expect(Math.min(...ys)).toBeGreaterThanOrEqual(10)
    expect(Math.max(...ys)).toBeLessThanOrEqual(50)
  })

  it('flows through a small dip instead of flattening at it, and stays on the baseline around a lone spike', () => {
    // y grows downwards: 100 is the baseline (zero), 20 the highest value.
    const dip = smoothPath([[0, 60], [10, 40], [20, 50], [30, 30]])
    const second = dip.split(' C')[1].split(' ')
    expect(Number(second[1])).not.toBe(40)
    const spike = smoothPath([[0, 100], [10, 100], [20, 20], [30, 100], [40, 100]])
    const ys = [...spike.matchAll(/-?\d+\.\d (-?\d+\.\d)/g)].map(match => Number(match[1]))
    expect(Math.max(...ys)).toBeLessThanOrEqual(100)
    expect(Math.min(...ys)).toBeGreaterThanOrEqual(20)
  })
})
