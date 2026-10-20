import { describe, expect, it } from 'vitest'
import { orderLinkOnlyTabs, orderStatusMoreTabs, orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'

describe('order status tabs', () => {
  it('keeps the undelivered and manual review tabs and drops the return, payment and unverified ones', () => {
    const values: string[] = orderStatusTabs.map(([value]) => value)
    for (const status of ['UNDELIVERED', 'MANUAL_REVIEW', 'LATE']) expect(values).toContain(status)
    for (const status of ['RETURN_IN_TRANSIT', 'RETURNED', 'PAYMENT_PENDING', 'UNVERIFIED']) expect(values).not.toContain(status)
    expect(new Set(values).size).toBe(values.length)
  })

  it('lets the tab row decide what fits and shows the pending queue only when a link opened it', () => {
    expect(orderStatusMoreTabs).toEqual([])
    expect(orderLinkOnlyTabs).toEqual(['PENDING'])
  })

  it('opens the tab a link names, so the dashboard pending link lists what its counter counts', () => {
    expect(resolveOrderStatusTab('PENDING')).toBe('PENDING')
    expect(resolveOrderStatusTab('pending')).toBe('PENDING')
    expect(resolveOrderStatusTab('LATE')).toBe('LATE')
    expect(resolveOrderStatusTab('READY_TO_SHIP')).toBe('PROCESSING')
    expect(resolveOrderStatusTab(null)).toBe('ALL')
    expect(resolveOrderStatusTab('CANCELLED')).toBe('CANCELLED')
  })
})
