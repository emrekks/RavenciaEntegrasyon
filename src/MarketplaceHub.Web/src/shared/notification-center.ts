import { backgroundPollInit, hubApi, isPermanentRequestError } from './api'
import { istanbulDayBoundary } from './format-date'
import { readStoredItem, writeStoredItem } from './local-storage'
import { readNotificationHistory, registerNotificationSink, type AppNotification, type NotificationSink, type NotificationSubjectKind } from './notifications'
import { scopedStorageKey } from './storage-scope'

// The notification center: the server's copy of every notification the bell shows, kept a week (İşlem Takibi ›
// Bildirimler, ops.panel_notifications). Producers never call this module: they raise notifications with
// appendNotification (shared/notifications.ts) as before, and the sink below sends each one here. Removing a
// notification from the bell (Temizle) only dismisses it for the bell; the center keeps listing it until its week is over.

export type NotificationCategory = 'ORDER' | 'PACKAGE' | 'INVOICE' | 'RETURN' | 'PRODUCT' | 'CONNECTION' | 'JOB' | 'MAPPING' | 'QUESTION' | 'IMPORT' | 'ACCOUNT' | 'ATTENTION' | 'OTHER'
export type NotificationSeverity = 'SUCCESS' | 'ERROR' | 'INFO'

export const notificationCategoryLabels: Record<NotificationCategory, string> = {
  ORDER: 'Sipariş', PACKAGE: 'Gönderi', INVOICE: 'Fatura', RETURN: 'İade', PRODUCT: 'Ürün', CONNECTION: 'Entegrasyon', JOB: 'İşlem',
  MAPPING: 'Eşleştirme', QUESTION: 'Müşteri Sorusu', IMPORT: 'Aktarım', ACCOUNT: 'Hesap', ATTENTION: 'Arka Plan Uyarısı', OTHER: 'Diğer'
}
export const notificationSeverityLabels: Record<NotificationSeverity, string> = { ERROR: 'Hata', SUCCESS: 'Başarılı', INFO: 'Bilgi' }

export type CenterNotification = {
  id: string
  clientKey: string
  severity: NotificationSeverity
  category: NotificationCategory
  title: string
  message: string
  href?: string | null
  actionLabel?: string | null
  platformCode?: string | null
  storeName?: string | null
  count: number
  payload?: string | null
  shared: boolean
  createdAt: string
  updatedAt: string
  readAt?: string | null
  dismissedAt?: string | null
}
export type CenterNotificationPage = { items: CenterNotification[]; nextCursor?: string | null; hasMore: boolean }
export type CenterNotificationSummary = { total: number; unread: number; errors: number }

const subjectCategories: Record<NotificationSubjectKind, NotificationCategory> = {
  product: 'PRODUCT', order: 'ORDER', package: 'PACKAGE', invoice: 'INVOICE', return: 'RETURN', connection: 'CONNECTION',
  job: 'JOB', mapping: 'MAPPING', question: 'QUESTION', import: 'IMPORT', account: 'ACCOUNT'
}
const pathCategories: Array<[RegExp, NotificationCategory]> = [
  [/^\/orders/u, 'ORDER'], [/^\/shipments/u, 'PACKAGE'], [/^\/invoices/u, 'INVOICE'], [/^\/returns/u, 'RETURN'], [/^\/products/u, 'PRODUCT'],
  [/^\/integrations/u, 'CONNECTION'], [/^\/jobs/u, 'JOB'], [/^\/mapping/u, 'MAPPING'], [/^\/questions/u, 'QUESTION'], [/^\/imports/u, 'IMPORT'], [/^\/settings/u, 'ACCOUNT']
]

/** The center's Tür of a bell notification: a background notice, else its record's kind, else its destination page. */
export function notificationCategory(notification: Pick<AppNotification, 'groupKey' | 'subject' | 'items' | 'href'>): NotificationCategory {
  if (notification.groupKey?.startsWith('attention:')) return 'ATTENTION'
  const kind = notification.subject?.kind ?? notification.items?.[0]?.kind
  if (kind) return subjectCategories[kind]
  const href = notification.href ?? ''
  return pathCategories.find(([pattern]) => pattern.test(href))?.[1] ?? 'OTHER'
}

const severities: Record<AppNotification['kind'], NotificationSeverity> = { success: 'SUCCESS', error: 'ERROR', info: 'INFO' }
const maximumPayload = 15_000

/** What the server keeps of a bell notification: the fields it filters by, and the whole notification for the card. */
export function centerInput(notification: AppNotification, shared = false) {
  let payload = JSON.stringify(notification)
  if (payload.length > maximumPayload) payload = JSON.stringify({ ...notification, items: notification.items?.slice(0, 10), details: notification.details?.slice(0, 10) })
  return {
    clientKey: notification.id,
    severity: severities[notification.kind],
    category: notificationCategory(notification),
    title: notification.title ?? null,
    message: notification.message,
    href: notification.href ?? null,
    actionLabel: notification.actionLabel ?? null,
    platformCode: notification.platformCode ?? null,
    storeName: notification.storeName ?? null,
    count: notification.count ?? 1,
    payload: payload.length > maximumPayload ? null : JSON.parse(payload) as unknown,
    createdAt: notification.firstAt ?? notification.createdAt,
    shared,
    read: notification.read
  }
}

/** The bell notification the center row was made from (its card), or null when it was not kept. */
export function centerNotificationCard(row: CenterNotification): AppNotification | null {
  if (!row.payload) return null
  try {
    const value = JSON.parse(row.payload) as AppNotification
    return value && typeof value === 'object' && typeof value.message === 'string' ? value : null
  } catch {
    return null
  }
}

const centerChangeEvent = 'ravencia:notification-center-change'
/** Calls listener whenever this browser sent the center something (the Bildirimler tab refreshes then). */
export function subscribeNotificationCenter(listener: () => void) {
  window.addEventListener(centerChangeEvent, listener)
  return () => window.removeEventListener(centerChangeEvent, listener)
}

type Pending = { saves: Map<string, ReturnType<typeof centerInput>>; read: Set<string>; dismissed: Set<string> }
const flushDelayMs = 600
const migratedKey = 'ravencia.notification-history.center'

function post(path: string, body: unknown) {
  // Sent with the notification, not by the person: it does not keep an idle session alive.
  return hubApi(path, backgroundPollInit({ method: 'POST', body: JSON.stringify(body) }))
}

/**
 * The bell's sink into the center (registered once by the signed-in shell): saves, reads and dismissals are batched
 * for a moment and sent in that order. A refusal that a repeat cannot fix (a read-only role) turns the sink off for the
 * session; any other failure keeps the batch for the next send. The first time for this person on this browser the
 * bell's current history is sent too (older than a week is not kept by the server).
 */
export function startNotificationCenterSink(send: (path: string, body: unknown) => Promise<unknown> = post) {
  const pending: Pending = { saves: new Map(), read: new Set(), dismissed: new Set() }
  let timer: number | null = null
  let sending = false
  let stopped = false
  let failures = 0
  const schedule = () => {
    if (stopped || timer !== null) return
    timer = window.setTimeout(() => { timer = null; void flush() }, flushDelayMs * Math.min(1 + failures * 4, 50))
  }
  const flush = async () => {
    if (stopped || sending) return
    sending = true
    const saves = [...pending.saves.values()]
    const read = [...pending.read]
    const dismissed = [...pending.dismissed]
    pending.saves.clear(); pending.read.clear(); pending.dismissed.clear()
    try {
      for (let index = 0; index < saves.length; index += 50) await send('/notifications', { items: saves.slice(index, index + 50) })
      if (read.length > 0) await send('/notifications/read', { clientKeys: read })
      if (dismissed.length > 0) await send('/notifications/dismiss', { clientKeys: dismissed })
      failures = 0
      if (saves.length + read.length + dismissed.length > 0) window.dispatchEvent(new CustomEvent(centerChangeEvent))
    } catch (error) {
      if (isPermanentRequestError(error)) { stopped = true; return }
      failures += 1
      // Newer saves of the same notification win over the ones that failed.
      for (const save of saves) if (!pending.saves.has(save.clientKey)) pending.saves.set(save.clientKey, save)
      read.forEach(key => pending.read.add(key))
      dismissed.forEach(key => pending.dismissed.add(key))
    } finally {
      sending = false
      if (!stopped && pending.saves.size + pending.read.size + pending.dismissed.size > 0) schedule()
    }
  }
  const sink: NotificationSink = {
    saved: (notification, options) => { pending.saves.set(notification.id, centerInput(notification, options?.shared ?? notification.groupKey?.startsWith('attention:') ?? false)); schedule() },
    read: ids => { ids.forEach(id => pending.read.add(id)); schedule() },
    dismissed: ids => { ids.forEach(id => pending.dismissed.add(id)); schedule() }
  }
  const unregister = registerNotificationSink(sink)
  try {
    if (readStoredItem(scopedStorageKey(migratedKey)) !== '1') {
      readNotificationHistory().forEach(notification => sink.saved(notification))
      writeStoredItem(scopedStorageKey(migratedKey), '1')
    }
  } catch { /* the move is best effort; new notifications reach the center either way */ }
  return () => {
    unregister()
    if (timer !== null) window.clearTimeout(timer)
    void flush()
    stopped = true
  }
}

// The center's list filters (İşlem Takibi › Bildirimler).
export type CenterFilters = { categories: string[]; severities: string[]; state: '' | 'UNREAD' | 'READ'; from: string; to: string; search: string }
export const emptyCenterFilters: CenterFilters = { categories: [], severities: [], state: '', from: '', to: '', search: '' }

export function centerFilterCount(filters: CenterFilters) {
  return (filters.categories.length > 0 ? 1 : 0) + (filters.severities.length > 0 ? 1 : 0) + (filters.state ? 1 : 0) + (filters.from || filters.to ? 1 : 0) + (filters.search.trim() ? 1 : 0)
}

/** The list's query string; from/to are Istanbul days (yyyy-MM-dd), sent as the day's start and end in Istanbul time. */
export function centerQueryString(filters: CenterFilters, after?: string | null) {
  const params = new URLSearchParams()
  params.set('limit', '50')
  if (after) params.set('after', after)
  if (filters.categories.length > 0) params.set('categories', filters.categories.join(','))
  if (filters.severities.length > 0) params.set('severities', filters.severities.join(','))
  if (filters.state) params.set('state', filters.state)
  const from = filters.from ? istanbulDayBoundary(filters.from) : ''
  const to = filters.to ? istanbulDayBoundary(filters.to, true) : ''
  if (from) params.set('from', from)
  if (to) params.set('to', to)
  if (filters.search.trim()) params.set('search', filters.search.trim().slice(0, 100))
  return `?${params.toString()}`
}

export const markCenterNotificationsRead = (selection: { ids?: string[]; all?: boolean }) => hubApi<{ updated: number }>('/notifications/read', { method: 'POST', body: JSON.stringify(selection) })

/** İşlem Takibi › Bildirimler, where the bell's "Tüm Bildirimler" leads (jobs-url-state: ?sekme=bildirimler). */
export const notificationCenterHref = '/jobs?sekme=bildirimler'
