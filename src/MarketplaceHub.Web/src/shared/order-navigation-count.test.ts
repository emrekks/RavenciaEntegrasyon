import { describe, expect, it } from 'vitest'
import { orderNavigationCount, pendingOrdersQuery } from './order-navigation-count'
import { realtimeQueryKeysFor } from '../app/realtime-query-keys'

describe('order navigation count', () => {
  it('adds the new and processing package counts', () => {
    expect(orderNavigationCount({ new: 4, processing: 0 })).toBe(4)
    expect(orderNavigationCount({ new: 4, processing: 3 })).toBe(7)
  })

  it('shows the Bekleyen tab count when the server sends it', () => {
    expect(orderNavigationCount({ new: 4, processing: 3, pending: 5 })).toBe(5)
    expect(orderNavigationCount({ new: 4, processing: 3, pending: 0 })).toBe(0)
  })

  it('preserves a zero count and omits the badge while the summary is unavailable', () => {
    expect(orderNavigationCount({ new: 0, processing: 0 })).toBe(0)
    expect(orderNavigationCount(undefined)).toBeUndefined()
  })
})

describe('the one Bekleyen query', () => {
  it('is refreshed by every realtime order change, so the badge and the overview card move together', () => {
    const refreshed = realtimeQueryKeysFor(['orders'])
    expect(refreshed.some(root => root.every((part, index) => pendingOrdersQuery.queryKey[index] === part))).toBe(true)
  })
})
