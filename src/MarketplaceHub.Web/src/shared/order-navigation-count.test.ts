import { describe, expect, it } from 'vitest'
import { orderNavigationCount } from './order-navigation-count'

describe('order navigation count', () => {
  it('adds the new and processing package counts', () => {
    expect(orderNavigationCount({ new: 4, processing: 0 })).toBe(4)
    expect(orderNavigationCount({ new: 4, processing: 3 })).toBe(7)
  })

  it('preserves a zero count and omits the badge while the summary is unavailable', () => {
    expect(orderNavigationCount({ new: 0, processing: 0 })).toBe(0)
    expect(orderNavigationCount(undefined)).toBeUndefined()
  })
})
