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
})
