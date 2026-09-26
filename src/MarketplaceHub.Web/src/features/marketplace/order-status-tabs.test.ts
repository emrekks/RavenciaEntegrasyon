import { describe, expect, it } from 'vitest'
import { orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'

describe('order status tabs', () => {
  it('removes the duplicate pending tab and keeps New as the canonical tab', () => {
    const values: string[] = orderStatusTabs.map(([value]) => value)
    expect(values).not.toContain('PENDING')
    expect(values).toContain('NEW')
  })

  it('redirects legacy pending links to New', () => {
    expect(resolveOrderStatusTab('PENDING')).toBe('NEW')
    expect(resolveOrderStatusTab('pending')).toBe('NEW')
    expect(resolveOrderStatusTab(null)).toBe('ALL')
    expect(resolveOrderStatusTab('CANCELLED')).toBe('CANCELLED')
  })
})
