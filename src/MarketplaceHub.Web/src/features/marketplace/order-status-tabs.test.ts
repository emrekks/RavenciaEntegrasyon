import { describe, expect, it } from 'vitest'
import { orderStatusMoreTabs, orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'

describe('order status tabs', () => {
  it('gives payment pending, unverified and the pending queue a tab of their own in the more menu', () => {
    const values: string[] = orderStatusTabs.map(([value]) => value)
    for (const status of ['PAYMENT_PENDING', 'UNVERIFIED', 'PENDING']) {
      expect(values).toContain(status)
      expect(orderStatusMoreTabs).toContain(status)
    }
    expect(new Set(values).size).toBe(values.length)
  })

  it('opens the tab a link names, so the dashboard pending link lists what its counter counts', () => {
    expect(resolveOrderStatusTab('PENDING')).toBe('PENDING')
    expect(resolveOrderStatusTab('pending')).toBe('PENDING')
    expect(resolveOrderStatusTab('PAYMENT_PENDING')).toBe('PAYMENT_PENDING')
    expect(resolveOrderStatusTab('UNVERIFIED')).toBe('UNVERIFIED')
    expect(resolveOrderStatusTab('READY_TO_SHIP')).toBe('PROCESSING')
    expect(resolveOrderStatusTab(null)).toBe('ALL')
    expect(resolveOrderStatusTab('CANCELLED')).toBe('CANCELLED')
  })
})

describe('order status tabs for every status', () => {
  it('offers the returned, in-transit return, undelivered and manual review statuses in the more menu', () => {
    const values: string[] = orderStatusTabs.map(([value]) => value)
    for (const status of ['UNDELIVERED', 'RETURN_IN_TRANSIT', 'RETURNED', 'MANUAL_REVIEW']) {
      expect(values).toContain(status)
      expect(orderStatusMoreTabs).toContain(status)
    }
  })
})
