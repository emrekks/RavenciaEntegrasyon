import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearNotificationHistory, markNotificationRead, readNotificationHistory, syncNewOrderNotices, type NewOrderNoticeRow } from './notifications'
import { startNotificationCenterSink } from './notification-center'

// Invented stores and order numbers only.
const many: NewOrderNoticeRow = {
  clientKey: 'new-orders:c1:r1', title: 'Yeni Sipariş', message: 'Trendyol: 3 yeni paket', href: '/orders?status=NEW', actionLabel: 'Siparişlere Git',
  platformCode: 'TRENDYOL', storeName: 'Deneme Mağaza', createdAt: '2026-11-27T09:00:00Z', updatedAt: '2026-11-27T09:00:00Z', readAt: null,
  payload: JSON.stringify({ connectionId: 'c1', items: [1, 2, 3].map(n => ({ kind: 'order', id: `o${n}`, label: `TST-000${n}`, href: `/orders?search=TST-000${n}` })) })
}
const single: NewOrderNoticeRow = {
  clientKey: 'new-orders:c2:r2', title: 'Yeni Sipariş', message: 'Pazarama: 1 yeni paket, sipariş TST-0100', href: '/orders?search=TST-0100', actionLabel: 'Siparişe Git',
  platformCode: 'PAZARAMA', storeName: 'İkinci Mağaza', createdAt: '2026-11-27T10:00:00Z', updatedAt: '2026-11-27T10:00:00Z', readAt: null,
  payload: JSON.stringify({ connectionId: 'c2', subject: { kind: 'order', id: 'o100', label: 'TST-0100', href: '/orders?search=TST-0100' } })
}
const orders = () => readNotificationHistory().filter(item => item.id.startsWith('new-orders:'))

describe('new orders in the bell', () => {
  afterEach(() => localStorage.clear())

  it('adds one unread entry per server notification, newest first, leading to the orders', () => {
    expect(syncNewOrderNotices([many, single])).toBe(true)
    const [latest, earlier] = orders()
    expect(latest).toMatchObject({ id: single.clientKey, title: 'Yeni Sipariş', message: 'Pazarama: 1 yeni paket, sipariş TST-0100', kind: 'info', read: false, href: '/orders?search=TST-0100', actionLabel: 'Siparişe Git', platformCode: 'PAZARAMA', storeName: 'İkinci Mağaza', source: 'Otomatik' })
    expect(latest.subject).toMatchObject({ kind: 'order', label: 'TST-0100' })
    expect(earlier).toMatchObject({ id: many.clientKey, href: '/orders?status=NEW', connectionId: 'c1' })
    expect(earlier.items?.map(item => item.label)).toEqual(['TST-0001', 'TST-0002', 'TST-0003'])
    // No burst count: three orders are "3 paket", not "3×".
    expect(earlier.count).toBeUndefined()
  })

  it('never adds the same notification twice, follows a read from another device and does not bring back a cleared one', () => {
    syncNewOrderNotices([many])
    expect(syncNewOrderNotices([many])).toBe(false)
    expect(orders()).toHaveLength(1)
    expect(syncNewOrderNotices([{ ...many, readAt: '2026-11-27T09:05:00Z' }])).toBe(true)
    expect(orders()[0].read).toBe(true)
    clearNotificationHistory()
    expect(syncNewOrderNotices([many])).toBe(false)
    expect(orders()).toHaveLength(0)
    // A later notification still comes.
    expect(syncNewOrderNotices([many, single])).toBe(true)
    expect(orders().map(item => item.id)).toEqual([single.clientKey])
  })

  it('does not bring back a notification cleared in any bell, also after a sign-out or on a new device', () => {
    const cleared = { ...many, dismissedAt: '2026-11-27T09:10:00Z', readAt: '2026-11-27T09:10:00Z' }
    // A fresh browser (the seen list is empty, as after clearPersonalStorage at sign-out): the cleared one stays away.
    expect(syncNewOrderNotices([cleared, single])).toBe(true)
    expect(orders().map(item => item.id)).toEqual([single.clientKey])
    // Read on the server: shown read.
    localStorage.clear()
    syncNewOrderNotices([{ ...single, readAt: '2026-11-27T10:01:00Z' }])
    expect(orders()[0].read).toBe(true)
  })

  it('keeps one entry per notification when two tabs wrote it at the same moment', () => {
    syncNewOrderNotices([many])
    const key = Object.keys(localStorage).find(name => name.startsWith('ravencia.notification-history') && !name.includes('.center'))!
    const stored = JSON.parse(localStorage.getItem(key)!) as unknown[]
    localStorage.setItem(key, JSON.stringify([...stored, ...stored]))
    expect(orders()).toHaveLength(1)
    // Not folded into a burst of two ("2×") either: it is the same notification.
    expect(orders()[0].count).toBeUndefined()
    expect(syncNewOrderNotices([many])).toBe(false)
    expect(readNotificationHistory().filter(item => item.id === many.clientKey)).toHaveLength(1)
  })

  it('keeps a notification read here read, and ignores rows that are not new-order notices', () => {
    syncNewOrderNotices([many, { ...single, clientKey: 'attention:x:1' }])
    expect(orders()).toHaveLength(1)
    markNotificationRead(many.clientKey)
    expect(syncNewOrderNotices([many])).toBe(false)
    expect(orders()[0].read).toBe(true)
  })

  it('is not sent to the center as the person’s own notification; reading it is', async () => {
    vi.useFakeTimers()
    try {
      const send = vi.fn().mockResolvedValue({})
      localStorage.setItem('ravencia.notification-history.center@', '1')
      const stop = startNotificationCenterSink(send)
      syncNewOrderNotices([many])
      markNotificationRead(many.clientKey)
      await vi.advanceTimersByTimeAsync(1000)
      expect(send.mock.calls.map(call => call[0])).toEqual(['/notifications/read'])
      expect(send.mock.calls[0][1]).toEqual({ clientKeys: [many.clientKey] })
      stop()
    } finally {
      vi.useRealTimers()
    }
  })
})
