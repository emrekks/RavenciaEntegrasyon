import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError } from './api'
import { centerInput, centerNotificationCard, centerQueryString, emptyCenterFilters, internalNotificationHref, notificationCategory, notificationCenterHref, startNotificationCenterSink } from './notification-center'
import { appendNotification, clearNotificationHistory, markAllNotificationsRead, markNotificationRead, readNotificationHistory, syncAttentionNotices, type AttentionNotice } from './notifications'
import { jobsMainTab } from '../features/operations/jobs-url-state'

type Sent = { path: string; body: { items?: Array<Record<string, unknown>>; clientKeys?: string[] } }

const failing: AttentionNotice = {
  key: 'cursor:c1:RETURNS', revision: '100', code: 'SYNC_CURSOR_FAILING', title: 'İade Okuma Başarısız', message: 'İade okuma art arda 3 kez başarısız oldu.',
  connectionId: 'c1', platformCode: 'TRENDYOL', storeName: 'Ravencia', failureCount: 3, items: []
}

describe('the notification center sink', () => {
  let sent: Sent[]
  let stop: (() => void) | null
  const send = vi.fn((path: string, body: unknown) => { sent.push({ path, body: body as Sent['body'] }); return Promise.resolve({}) })

  beforeEach(() => {
    localStorage.clear()
    sent = []
    stop = null
    send.mockClear()
    vi.useFakeTimers()
  })
  afterEach(() => {
    stop?.()
    vi.useRealTimers()
  })

  it('sends what the bell saved, read and cleared, in that order, and the center keeps cleared ones as dismissed', async () => {
    stop = startNotificationCenterSink(send)
    const saved = appendNotification({ message: 'Sipariş paketlendi.', kind: 'success', title: 'Paketlendi', record: true, subject: { kind: 'order', id: 'o1', label: '123', href: '/orders/o1' } })!
    markNotificationRead(saved.id)
    clearNotificationHistory()
    await vi.advanceTimersByTimeAsync(1000)

    expect(sent.map(item => item.path)).toEqual(['/notifications', '/notifications/read', '/notifications/dismiss'])
    expect(sent[0].body.items?.[0]).toMatchObject({ clientKey: saved.id, severity: 'SUCCESS', category: 'ORDER', title: 'Paketlendi', href: '/orders/o1', shared: false })
    expect(sent[1].body.clientKeys).toEqual([saved.id])
    expect(sent[2].body.clientKeys).toEqual([saved.id])
    expect(readNotificationHistory()).toEqual([])
  })

  it('a read made in the center only follows in this bell and is not sent back', async () => {
    stop = startNotificationCenterSink(send)
    const saved = appendNotification({ message: 'Ürün gönderilemedi.', kind: 'error', title: 'Gönderilemedi', record: true })!
    await vi.advanceTimersByTimeAsync(1000)
    sent = []
    markNotificationRead(saved.id, { local: true })
    markAllNotificationsRead({ local: true })
    await vi.advanceTimersByTimeAsync(1000)
    expect(sent).toEqual([])
    expect(readNotificationHistory()[0].read).toBe(true)
  })

  it('leaves background notices to the server: the bell shows them, the browser never writes an attention key', async () => {
    stop = startNotificationCenterSink(send)
    syncAttentionNotices([failing])
    await vi.advanceTimersByTimeAsync(1000)
    expect(readNotificationHistory()[0].id).toBe('attention:cursor:c1:RETURNS:100')
    expect(sent.flatMap(request => request.body.items ?? []).filter(item => String(item.clientKey).startsWith('attention:'))).toEqual([])
  })

  it('moves the bell history once per person and browser', async () => {
    appendNotification({ message: 'Fatura kesildi.', kind: 'success', title: 'Fatura Kesildi', record: true })
    stop = startNotificationCenterSink(send)
    await vi.advanceTimersByTimeAsync(1000)
    expect(sent).toHaveLength(1)
    stop()
    sent = []
    stop = startNotificationCenterSink(send)
    await vi.advanceTimersByTimeAsync(1000)
    expect(sent).toEqual([])
  })

  it('stops for the session on a refusal a repeat cannot fix, and retries any other failure', async () => {
    const refused = vi.fn(() => Promise.reject(new ApiRequestError('Yetki yok', 403)))
    stop = startNotificationCenterSink(refused)
    appendNotification({ message: 'Bir.', kind: 'error', title: 'Bir', record: true })
    await vi.advanceTimersByTimeAsync(1000)
    appendNotification({ message: 'İki.', kind: 'error', title: 'İki', record: true })
    await vi.advanceTimersByTimeAsync(60_000)
    expect(refused).toHaveBeenCalledTimes(1)
    stop()

    let calls = 0
    const flaky = vi.fn((path: string, body: unknown) => { calls += 1; return calls === 1 ? Promise.reject(new Error('ağ')) : send(path, body) })
    localStorage.clear()
    stop = startNotificationCenterSink(flaky)
    appendNotification({ message: 'Üç.', kind: 'error', title: 'Üç', record: true })
    await vi.advanceTimersByTimeAsync(10_000)
    expect(flaky).toHaveBeenCalledTimes(2)
    expect(sent[0].body.items?.[0]).toMatchObject({ title: 'Üç' })
  })
})

describe('notification center helpers', () => {
  it('derives the Tür from the notice, the record or the page', () => {
    expect(notificationCategory({ groupKey: 'attention:x' })).toBe('ATTENTION')
    expect(notificationCategory({ subject: { kind: 'invoice', label: 'F1' } })).toBe('INVOICE')
    expect(notificationCategory({ href: '/returns/7' })).toBe('RETURN')
    expect(notificationCategory({ href: '/somewhere' })).toBe('OTHER')
  })

  it('keeps the whole bell card and reads it back', () => {
    const input = centerInput({ id: 'n1', message: 'Mesaj', kind: 'info', createdAt: '2026-11-13T08:00:00.000Z', read: false, details: [{ label: 'Kod', value: 'X' }] })
    expect(input).toMatchObject({ clientKey: 'n1', severity: 'INFO', category: 'OTHER', createdAt: '2026-11-13T08:00:00.000Z' })
    const card = centerNotificationCard({ id: 'r', clientKey: 'n1', severity: 'INFO', category: 'OTHER', title: 'Mesaj', message: 'Mesaj', count: 1, shared: false, createdAt: '', updatedAt: '', payload: JSON.stringify(input.payload) })
    expect(card?.details).toEqual([{ label: 'Kod', value: 'X' }])
    expect(centerNotificationCard({ id: 'r', clientKey: 'n1', severity: 'INFO', category: 'OTHER', title: '', message: '', count: 1, shared: false, createdAt: '', updatedAt: '', payload: '{' })).toBeNull()
  })

  it('sends the filters with Istanbul day bounds', () => {
    const query = new URLSearchParams(centerQueryString({ ...emptyCenterFilters, categories: ['ORDER', 'INVOICE'], severities: ['ERROR'], state: 'UNREAD', from: '2026-11-10', to: '2026-11-12', search: ' fatura ' }, 'abc').slice(1))
    expect(Object.fromEntries(query)).toEqual({ limit: '50', after: 'abc', categories: 'ORDER,INVOICE', severities: 'ERROR', state: 'UNREAD', from: '2026-11-10T00:00:00.000+03:00', to: '2026-11-12T23:59:59.999+03:00', search: 'fatura' })
  })

  it('leads the bell to İşlem Takibi › Bildirimler', () => {
    expect(jobsMainTab(new URLSearchParams(notificationCenterHref.split('?')[1]))).toBe('notifications')
  })
})

describe('internalNotificationHref', () => {
  it('follows only a path inside the panel', () => {
    expect(internalNotificationHref('/orders/o1')).toBe('/orders/o1')
    expect(internalNotificationHref('/settings?tab=offsite')).toBe('/settings?tab=offsite')
    for (const href of ['https://evil.example/x', '//evil.example', '/\\evil.example', 'javascript:alert(1)', 'orders/1', '/a\nb', '', null, undefined])
      expect(internalNotificationHref(href)).toBeNull()
  })
})
