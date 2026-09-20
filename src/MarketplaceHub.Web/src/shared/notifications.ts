export type AppNotification = {
  id: string
  message: string
  kind: 'success' | 'error' | 'info'
  createdAt: string
  read: boolean
}

const notificationStorageKey = 'ravencia.notification-history'
const notificationChangeEvent = 'ravencia:notification-change'

export function normalizeNotificationKind(message: string, kind: AppNotification['kind']): AppNotification['kind'] {
  const normalized = message.toLocaleLowerCase('tr-TR')
  if (/(hata|başarısız|kaydedilemedi|güncellenemedi|silinemedi|oluşturulamadı|alınamadı|bulunamadı|geçersiz|uygulanamadı|okunamadı|yenilenemedi|eşlenemedi|reddi|reddedildi)/u.test(normalized)) return 'error'
  if (/(kuyruğa\s+alındı|kuyrukta|işleniyor|sonucu\s+(?:henüz\s+)?bekleniyor|incelemesi\s+bekleniyor|onayı\s+bekleniyor|yeniden\s+kuyruğa|waiting(?:fraudcheck|[_\s-]*approval)|pending|processing|salt-okunur)/u.test(normalized)) return 'info'
  return kind
}

export function readNotificationHistory(): AppNotification[] {
  try {
    const raw = localStorage.getItem(notificationStorageKey)
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return []
    return parsed.flatMap(item => {
      if (!item || typeof item !== 'object') return []
      const value = item as Record<string, unknown>
      if (typeof value.id !== 'string' || typeof value.message !== 'string' || typeof value.createdAt !== 'string'
        || (value.kind !== 'success' && value.kind !== 'error' && value.kind !== 'info')) return []
      return [{ id: value.id, message: value.message, kind: normalizeNotificationKind(value.message, value.kind as AppNotification['kind']), createdAt: value.createdAt, read: value.read === true }]
    }).slice(0, 20)
  } catch {
    return []
  }
}

export function appendNotification(message: string, kind: AppNotification['kind'] = 'info') {
  const notification: AppNotification = { id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, message, kind: normalizeNotificationKind(message, kind), createdAt: new Date().toISOString(), read: false }
  const history = [notification, ...readNotificationHistory()].slice(0, 20)
  try {
    localStorage.setItem(notificationStorageKey, JSON.stringify(history))
  } catch {
    // A full or restricted browser store must not break the operation that
    // produced the notification.
  }
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
  return notification
}

export function markNotificationRead(notificationId: string) {
  const history = readNotificationHistory()
  const next = history.map(notification => notification.id === notificationId ? { ...notification, read: true } : notification)
  if (next.every((notification, index) => notification.read === history[index]?.read)) return
  try {
    localStorage.setItem(notificationStorageKey, JSON.stringify(next))
  } catch {
    return
  }
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
}

export function markAllNotificationsRead() {
  const history = readNotificationHistory()
  if (!history.some(notification => !notification.read)) return
  const next = history.map(notification => notification.read ? notification : { ...notification, read: true })
  try {
    localStorage.setItem(notificationStorageKey, JSON.stringify(next))
  } catch {
    return
  }
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
}

export function clearNotificationHistory() {
  try {
    localStorage.removeItem(notificationStorageKey)
  } catch {
    return
  }
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
}

export function subscribeNotificationHistory(listener: () => void) {
  const refresh = () => listener()
  window.addEventListener(notificationChangeEvent, refresh)
  window.addEventListener('storage', refresh)
  return () => {
    window.removeEventListener(notificationChangeEvent, refresh)
    window.removeEventListener('storage', refresh)
  }
}
