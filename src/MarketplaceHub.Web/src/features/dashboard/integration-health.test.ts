import { describe, expect, it } from 'vitest'
import type { InstantNotificationStatus } from '../marketplace/instant-notification-status'
import { webhookCell } from './integration-health'

const status = (state: InstantNotificationStatus['state'], extra: Partial<InstantNotificationStatus> = {}): InstantNotificationStatus =>
  ({ state, endpoint: null, registeredAt: null, lastReceivedAt: null, receivedLast24Hours: 0, message: null, ...extra })
const since = (value: string | null) => value ? '3 dk önce' : null

describe('webhookCell', () => {
  it('says a marketplace without webhooks has none, without calling it off', () => {
    expect(webhookCell(status('UNSUPPORTED'), false, since)).toMatchObject({ tone: 'na', text: 'Desteklenmiyor' })
  })

  it('shows a working webhook by its last notification', () => {
    expect(webhookCell(status('ACTIVE', { lastReceivedAt: '2026-11-01T10:00:00Z' }), false, since)).toMatchObject({ tone: 'ok', text: '3 dk önce' })
    expect(webhookCell(status('ACTIVE'), false, since)).toMatchObject({ tone: 'ok', text: 'Açık' })
  })

  it('marks stopped and missing registrations and turned-away deliveries for attention', () => {
    expect(webhookCell(status('MISSING'), false, since).tone).toBe('bad')
    expect(webhookCell(status('CONNECTION_INACTIVE'), false, since).tone).toBe('bad')
    expect(webhookCell(status('PASSIVE'), false, since).tone).toBe('warn')
    expect(webhookCell(status('QUIET'), false, since).tone).toBe('warn')
    const rejected = webhookCell(status('ACTIVE', { lastRejectedAt: '2026-11-01T09:00:00Z', lastRejectedReason: 'Kimlik doğrulanamadı.' }), false, since)
    expect(rejected).toMatchObject({ tone: 'warn', text: 'Reddedilen Var' })
    expect(rejected.title).toContain('Kimlik doğrulanamadı.')
  })

  it('keeps an off, unconfirmed or unread webhook neutral', () => {
    expect(webhookCell(status('OFF'), false, since)).toMatchObject({ tone: 'off', text: 'Kapalı' })
    expect(webhookCell(status('WAITING'), false, since)).toMatchObject({ tone: 'off', text: 'Bekleniyor' })
    expect(webhookCell(status('UNKNOWN', { message: 'Trendyol’daki durum okunamadı' }), false, since)).toMatchObject({ tone: 'off', text: 'Okunamadı' })
    expect(webhookCell(undefined, true, since)).toMatchObject({ tone: 'off', text: 'Okunamadı' })
    expect(webhookCell(undefined, false, since)).toMatchObject({ tone: 'off', text: '…' })
  })

  it('puts the integration page label and explanation in the hover text', () => {
    expect(webhookCell(status('QUIET', { message: 'Son bildirimden sonra gelen sipariş için bildirim gelmedi.' }), false, since).title)
      .toBe('Webhook · Son saatlerde bildirim yok · Son bildirimden sonra gelen sipariş için bildirim gelmedi.')
  })
})
