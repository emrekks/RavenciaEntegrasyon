import { describe, expect, it } from 'vitest'
import { orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'

describe('order status tabs', () => {
  it('removes the duplicate pending and unverified tabs while keeping New canonical', () => {
    const values: string[] = orderStatusTabs.map(([value]) => value)
    expect(values).not.toContain('PENDING')
    expect(values).not.toContain('UNVERIFIED')
    expect(values).toContain('NEW')
  })

  it('redirects legacy pending links to New', () => {
    expect(resolveOrderStatusTab('PENDING')).toBe('NEW')
    expect(resolveOrderStatusTab('pending')).toBe('NEW')
    expect(resolveOrderStatusTab('UNVERIFIED')).toBe('ALL')
    expect(resolveOrderStatusTab(null)).toBe('ALL')
    expect(resolveOrderStatusTab('CANCELLED')).toBe('CANCELLED')
  })
})
