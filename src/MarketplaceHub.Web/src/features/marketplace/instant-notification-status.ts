import type { WebhookSetup } from './webhook-setup'

// GET /connections/{id}/instant-notifications (InstantNotificationService.StatusAsync): the connection's webhook as the
// integration page shows it. The overview reads the same answer under the same query key.
export type InstantNotificationState = 'UNSUPPORTED' | 'OFF' | 'ACTIVE' | 'PASSIVE' | 'MISSING' | 'UNKNOWN' | 'WAITING' | 'QUIET' | 'CONNECTION_INACTIVE'
export type InstantNotificationStatus = { state: InstantNotificationState; endpoint: string | null; registeredAt: string | null; lastReceivedAt: string | null; receivedLast24Hours: number; message: string | null; setup?: WebhookSetup | null; lastRejectedAt?: string | null; lastRejectedReason?: string | null; previousPreparedAt?: string | null; adoptableFrom?: { connectionName: string; environment: string; preparedAt: string } | null }

export const instantNotificationStateLabels: Record<InstantNotificationState, string> = { UNSUPPORTED: 'Desteklenmiyor', OFF: 'Kapalı', ACTIVE: 'Açık', PASSIVE: 'Trendyol durdurdu', MISSING: 'Trendyol’da kayıt yok', UNKNOWN: 'Durum okunamadı', WAITING: 'İlk bildirim bekleniyor', QUIET: 'Son saatlerde bildirim yok', CONNECTION_INACTIVE: 'Bağlantı pasif' }

export const instantNotificationsQueryKey = (connectionId: string) => ['instant-notifications', connectionId] as const

/**
 * How the overview marks a webhook: "bad" when notifications cannot arrive until the user acts (the registration is
 * gone, the connection refuses them), "warn" when they stopped or a delivery was turned away, "off" when the webhook is
 * off, not yet confirmed or could not be read (unknown stays unknown), "na" when the marketplace has none.
 */
export function webhookTone(status: InstantNotificationStatus): 'ok' | 'warn' | 'bad' | 'off' | 'na' {
  switch (status.state) {
    case 'UNSUPPORTED': return 'na'
    case 'MISSING':
    case 'CONNECTION_INACTIVE': return 'bad'
    case 'PASSIVE':
    case 'QUIET': return 'warn'
    case 'ACTIVE': return status.lastRejectedAt ? 'warn' : 'ok'
    default: return 'off'
  }
}
