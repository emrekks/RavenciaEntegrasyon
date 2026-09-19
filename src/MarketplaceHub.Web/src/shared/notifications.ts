export type AppNotification = {
  id: string
  message: string
  kind: 'success' | 'error' | 'info'
  createdAt: string
}

const notificationStorageKey = 'ravencia.notification-history'
const notificationChangeEvent = 'ravencia:notification-change'

export function readNotificationHistory(): AppNotification[] {
  try {
    const raw = localStorage.getItem(notificationStorageKey)
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return []
    return parsed.filter((item): item is AppNotification => {
      if (!item || typeof item !== 'object') return false
      const value = item as Record<string, unknown>
      return typeof value.id === 'string' && typeof value.message === 'string' && typeof value.createdAt === 'string'
        && (value.kind === 'success' || value.kind === 'error' || value.kind === 'info')
    }).slice(0, 20)
  } catch {
    return []
  }
}

export function appendNotification(message: string, kind: AppNotification['kind'] = 'info') {
  const notification: AppNotification = { id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, message, kind, createdAt: new Date().toISOString() }
  const history = [notification, ...readNotificationHistory()].slice(0, 20)
  localStorage.setItem(notificationStorageKey, JSON.stringify(history))
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
  return notification
}

export function clearNotificationHistory() {
  localStorage.removeItem(notificationStorageKey)
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
