import { describe, expect, it } from 'vitest'
import { orderStatusMoreTabs, orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'

describe('order status tabs', () => {
  it('removes the duplicate pending and unverified tabs while keeping New canonical', () => {
    const values: string[] = orderStatusTabs.map(([value]) => value)
    expect(values).not.toContain('PENDING')
    expect(values).not.toContain('UNVERIFIED')
    expect(values).toContain('NEW')
    expect(values).not.toContain('PAYMENT_PENDING')
  })

  it('redirects legacy pending links to New', () => {
    expect(resolveOrderStatusTab('PENDING')).toBe('NEW')
    expect(resolveOrderStatusTab('pending')).toBe('NEW')
    expect(resolveOrderStatusTab('PAYMENT_PENDING')).toBe('NEW')
    expect(resolveOrderStatusTab('UNVERIFIED')).toBe('ALL')
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
