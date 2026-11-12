import { describe, expect, it } from 'vitest'
import { canTakeOrderIntoWork, orderLinkOnlyTabs, orderStatusLabel, orderStatusMoreTabs, orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'

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

  it('opens Tümü for a status no tab lists instead of sending it to the API as typed', () => {
    expect(resolveOrderStatusTab('BOGUS')).toBe('ALL')
    expect(resolveOrderStatusTab('RETURNED')).toBe('ALL')
    expect(resolveOrderStatusTab('<script>')).toBe('ALL')
    expect(resolveOrderStatusTab('  ')).toBe('ALL')
  })

  it('names a held and a reviewed order the same in the tab and everywhere else on the order', () => {
    const tabName = (value: string) => orderStatusTabs.find(([tab]) => tab === value)?.[1]
    expect(orderStatusLabel('ON_HOLD')).toBe('Askıda')
    expect(tabName('ON_HOLD')).toBe(orderStatusLabel('ON_HOLD'))
    expect(orderStatusLabel('manual_review')).toBe('İnceleme Gerekli')
    expect(tabName('MANUAL_REVIEW')).toBe(orderStatusLabel('MANUAL_REVIEW'))
    expect(orderStatusLabel('SHIPPED')).toBe('Kargoda')
  })

  it('offers İşleme Al only where the server takes the order into work', () => {
    for (const platform of ['TRENDYOL', 'N11', 'PAZARAMA', 'HEPSIBURADA', ' n11 ']) expect(canTakeOrderIntoWork(platform)).toBe(true)
    for (const platform of ['PTTAVM', 'SHOPIFY', '']) expect(canTakeOrderIntoWork(platform)).toBe(false)
  })
})
