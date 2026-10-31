import { instantNotificationStateLabels, webhookTone, type InstantNotificationState, type InstantNotificationStatus } from '../marketplace/instant-notification-status'

export type WebhookCell = { tone: 'ok' | 'warn' | 'bad' | 'off' | 'na'; text: string; title: string }

// The matrix cell's short words; the integration page's full label and its explanation go to the hover text.
const SHORT: Record<InstantNotificationState, string> = { UNSUPPORTED: 'Desteklenmiyor', OFF: 'Kapalı', ACTIVE: 'Açık', PASSIVE: 'Durduruldu', MISSING: 'Kayıt Yok', UNKNOWN: 'Okunamadı', WAITING: 'Bekleniyor', QUIET: 'Bildirim Yok', CONNECTION_INACTIVE: 'Bağlantı Pasif' }

/** The overview's webhook cell for a connection, from the integration page's status answer. */
export function webhookCell(status: InstantNotificationStatus | undefined, failed: boolean, since: (timestamp: string | null) => string | null): WebhookCell {
  if (!status) return failed ? { tone: 'off', text: 'Okunamadı', title: 'Webhook · Durum okunamadı' } : { tone: 'off', text: '…', title: 'Webhook · Kontrol ediliyor' }
  const tone = webhookTone(status)
  const text = status.state === 'ACTIVE' && !status.lastRejectedAt ? since(status.lastReceivedAt) ?? SHORT.ACTIVE
    : status.state === 'ACTIVE' ? 'Reddedilen Var'
    : SHORT[status.state]
  const detail = [status.lastRejectedReason, status.message].filter(Boolean).join(' ')
  return { tone, text, title: `Webhook · ${instantNotificationStateLabels[status.state]}${detail ? ` · ${detail}` : ''}` }
}
