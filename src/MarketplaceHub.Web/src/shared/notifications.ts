import { scopedStorageKey } from './storage-scope'

export type NotificationTarget = { href: string; label: string }
export type NotificationDetail = { label: string; value: string; tone?: 'success' | 'error' }

// What a notification is about: a product, an order, an invoice… with the page that shows it when there is one.
export type NotificationSubjectKind = 'product' | 'order' | 'package' | 'invoice' | 'return' | 'connection' | 'job' | 'mapping' | 'question' | 'import' | 'account'
export type NotificationSubject = { kind: NotificationSubjectKind; id?: string; label: string; href?: string }

export type AppNotification = {
  id: string
  message: string
  kind: 'success' | 'error' | 'info'
  createdAt: string
  read: boolean
  // Where the notification leads: the page that explains or fixes what it reports.
  href?: string
  actionLabel?: string
  // The destination was derived from the message (inferNotificationTarget), so it is derived again on read.
  targetInferred?: boolean
  // Rows of the Ayrıntı section beyond the standard ones (for example one line per return with its outcome).
  details?: NotificationDetail[]
  // The operation and its outcome in a few words ("Yayın Durumu Sorgulanamadı"); derived from the message when absent.
  title?: string
  // The operation alone ("Yayın Durumu Sorgulama"), for the Ayrıntı rows.
  operation?: string
  // Where it happened: the marketplace and the store (connection) it was done in.
  platformCode?: string
  connectionId?: string
  storeName?: string
  subject?: NotificationSubject
  // The marketplace's or the server's own code of a failure, kept for support.
  code?: string
  jobId?: string
  // Identical notifications in a burst collapse into one: the key, how many there were, since when, and what they touched.
  groupKey?: string
  groupMessage?: string
  count?: number
  firstAt?: string
  items?: NotificationSubject[]
  // A producer kept it on purpose, outside the wording policy (shouldRecordNotification).
  recorded?: boolean
}

// Context a producer knows about its notification. Everything is optional: a bare sentence still makes a notification.
export type NotificationContext = {
  title?: string
  operation?: string
  platformCode?: string | null
  connectionId?: string | null
  storeName?: string | null
  subject?: NotificationSubject | null
  code?: string | null
  jobId?: string | null
  details?: NotificationDetail[]
  /** Notifications with the same key within a few minutes collapse into one with a count. */
  groupKey?: string
  /** The summary once several collapsed; "{count}" is replaced by their number. */
  groupMessage?: string
  /** Kept in the history even when the wording policy would leave it a toast (a connection test, for example). */
  record?: boolean
  /** The button of the notification; by default the subject's page, else a page derived from the message. */
  target?: NotificationTarget | null
}

export type NotificationInput = NotificationContext & { message: string; kind?: AppNotification['kind'] }

const subjectActions: Record<NotificationSubjectKind, string> = {
  product: 'Ürüne Git', order: 'Siparişe Git', package: 'Gönderiye Git', invoice: 'Faturaya Git', return: 'İadeye Git',
  connection: 'Entegrasyona Git', job: 'İşleme Git', mapping: 'Eşleştirmeye Git', question: 'Sorulara Git', import: 'İçe Aktarıma Git', account: 'Ayarlara Git'
}

export const notificationSubjectLabels: Record<NotificationSubjectKind, string> = {
  product: 'Ürün', order: 'Sipariş', package: 'Paket', invoice: 'Fatura', return: 'İade', connection: 'Bağlantı', job: 'İş', mapping: 'Eşleştirme', question: 'Soru', import: 'İçe Aktarım', account: 'Hesap'
}

const platformNames: Record<string, string> = { TRENDYOL: 'Trendyol', TRENDYOL_EFATURAM: 'Trendyol E-Faturam', HEPSIBURADA: 'Hepsiburada', N11: 'n11', SHOPIFY: 'Shopify', PAZARAMA: 'Pazarama', PTTAVM: 'PTT AVM' }

export function notificationPlatformName(platformCode: string | null | undefined) {
  const code = platformCode?.trim().toUpperCase() ?? ''
  return code ? platformNames[code] ?? code : null
}

/** The code of a marketplace named in words ("Pazarama", "PTT AVM"); the panel's own name and unknown words give none. */
export function platformCodeFromName(name: string | null | undefined) {
  const normalized = name?.trim().toLocaleLowerCase('tr-TR').replace(/[\s-]+/gu, '') ?? ''
  if (!normalized) return undefined
  const match = Object.entries(platformNames).find(([code, label]) => label.toLocaleLowerCase('tr-TR').replace(/[\s-]+/gu, '') === normalized || code.toLocaleLowerCase('tr-TR') === normalized)
  if (match) return match[0]
  return normalized === 'efaturam' ? 'TRENDYOL_EFATURAM' : undefined
}

// A link to one record where the panel has a route for it; lists open filtered on it otherwise.
export const notificationLinks = {
  product: (id: string) => `/products/${id}`,
  order: (orderNumber: string) => `/orders?search=${encodeURIComponent(orderNumber)}`,
  package: (id: string) => `/shipments/${id}`,
  invoice: (id: string) => `/invoices/${id}`,
  return: (id: string) => `/returns/${id}`,
  connection: (id: string) => `/integrations/${id}`,
  job: (search: string) => `/jobs?ara=${encodeURIComponent(search)}`
}

export function productNotificationSubject(product: { id?: string | null; name?: string | null; modelCode?: string | null }): NotificationSubject | null {
  const name = product.name?.trim()
  const model = product.modelCode?.trim()
  const label = name && model && !name.includes(model) ? `${name} (${model})` : name || model
  if (!label) return null
  return { kind: 'product', id: product.id ?? undefined, label, href: product.id ? notificationLinks.product(product.id) : undefined }
}

// Most notifications are plain sentences, so the destination is derived from what they talk about.
export function inferNotificationTarget(message: string): NotificationTarget | null {
  const text = message.toLocaleLowerCase('tr-TR')
  // A connection's own events (for example "… bağlantısında seçilen veriler sıfırlandı: iade verileri…") lead to
  // the connection even when they name the data they touched.
  if (/(dış yazma|yazma anahtar|bağlantı doğrulan|bağlantı test|api anahtar|kimlik doğrulama|bağlantısında)/u.test(text)) return { href: '/integrations', label: 'Entegrasyonlara Git' }
  if (/iade/u.test(text)) {
    if (/(onaylanamadı|reddedilemedi|başarısız|hata|alınamadı)/u.test(text)) return { href: '/returns?status=ACTION_REQUIRED', label: 'Bekleyen İadelere Git' }
    if (/(reddi|reddedildi|ret kararı)/u.test(text)) return { href: '/returns?status=REJECTED', label: 'Reddedilen İadelere Git' }
    if (/(kabul|onaylandı)/u.test(text)) return { href: '/returns?status=APPROVED', label: 'Onaylanan İadelere Git' }
    return { href: '/returns', label: 'İadelere Git' }
  }
  if (/sipariş/u.test(text)) return { href: '/orders', label: 'Siparişlere Git' }
  if (/fatura/u.test(text)) return { href: '/invoices', label: 'Faturalara Git' }
  if (/(ürün|katalog|stok|varyant|yayın)/u.test(text)) return { href: '/products', label: 'Ürünlere Git' }
  if (/(eşleş|kategori|marka|özellik)/u.test(text)) return { href: '/mappings/categories', label: 'Eşleştirmelere Git' }
  if (/(kuyruğa|senkron|eşitle)/u.test(text)) return { href: '/jobs', label: 'İşlem Takibine Git' }
  return null
}

// Kept per tenant and user (storage-scope.ts): the history of one person is never shown to the next one on this browser.
const notificationStorageKey = 'ravencia.notification-history'
const notificationChangeEvent = 'ravencia:notification-change'
const historyLimit = 20
// Identical notifications closer than this collapse into one (17 failed refreshes of one click burst are one notice).
const burstWindowMs = 5 * 60_000

export function normalizeNotificationKind(message: string, kind: AppNotification['kind']): AppNotification['kind'] {
  // A count of none ("3 başarılı, 0 başarısız") reports no failure.
  const normalized = message.toLocaleLowerCase('tr-TR').replace(/(?<![\d.,])0\s+(?:başarısız|hatalı|hata)/gu, '')
  if (/(hata|başarısız|kaydedilemedi|güncellenemedi|silinemedi|oluşturulamadı|alınamadı|bulunamadı|geçersiz|uygulanamadı|okunamadı|yenilenemedi|eşlenemedi|sorgulanamadı|reddi|reddedildi|yayın uyarısı|eşlemesi (?:yok|gerek)|zorunlu özellik)/u.test(normalized)) return 'error'
  if (/(kuyruğa\s+alındı|kuyrukta|işleniyor|sonucu\s+(?:henüz\s+)?bekleniyor|incelemesi\s+bekleniyor|onayı\s+bekleniyor|yeniden\s+kuyruğa|waiting(?:fraudcheck|[_\s-]*approval)|pending|processing|salt-okunur)/u.test(normalized)) return 'info'
  return kind
}

function readNotificationDetails(value: unknown): NotificationDetail[] | undefined {
  if (!Array.isArray(value)) return undefined
  const details = value.flatMap(item => {
    if (!item || typeof item !== 'object') return []
    const entry = item as Record<string, unknown>
    if (typeof entry.label !== 'string' || typeof entry.value !== 'string') return []
    return [{ label: entry.label.slice(0, 60), value: entry.value.slice(0, 240), tone: entry.tone === 'success' || entry.tone === 'error' ? entry.tone : undefined } satisfies NotificationDetail]
  }).slice(0, 40)
  return details.length > 0 ? details : undefined
}

const subjectKinds = new Set<string>(Object.keys(subjectActions))

function readSubject(value: unknown): NotificationSubject | undefined {
  if (!value || typeof value !== 'object') return undefined
  const entry = value as Record<string, unknown>
  if (typeof entry.kind !== 'string' || !subjectKinds.has(entry.kind) || typeof entry.label !== 'string' || !entry.label.trim()) return undefined
  return {
    kind: entry.kind as NotificationSubjectKind,
    label: entry.label.slice(0, 160),
    id: typeof entry.id === 'string' ? entry.id.slice(0, 80) : undefined,
    href: typeof entry.href === 'string' && entry.href.startsWith('/') ? entry.href.slice(0, 300) : undefined
  }
}

function readSubjects(value: unknown): NotificationSubject[] | undefined {
  if (!Array.isArray(value)) return undefined
  const items = value.map(readSubject).filter((item): item is NotificationSubject => !!item).slice(0, 50)
  return items.length > 0 ? items : undefined
}

const text = (value: unknown, max: number) => typeof value === 'string' && value.trim() ? value.trim().slice(0, max) : undefined

// What deserves a place in the history, decided once for every producer.
//   Recorded:  failures of real operations, and successes that change something outside the screen
//              (decisions sent to a marketplace, invoices, labels, stock, publishing, destructive cleanups,
//              security changes) so they can be looked up after the toast is gone.
//   Toast only: form validation, local preview/selection/copy feedback, "queued" and progress messages
//              (their outcome is what matters) and routine saves.
const validationOrLocal = /(boş olamaz|en az bir|seçin\b|yazın|doldurun|girin\b|geçersiz veya|önizleme|kopyalandı|seçildi|seçimi|ayarları hesabınıza kaydedildi|eşleştirmesi kaydedildi|değer güncellendi|değer kaldırıldı)/u
const progressOnly = /(kuyruğa alındı|kuyruğa alınıyor|kuyrukta|eşitleniyor|güncelleniyor|yükleniyor|işleniyor|hazırlanıyor|sorgulanıyor|sonucu\s+(?:henüz\s+)?(?:bekleniyor|kesinleşmedi)|read-back ile doğrulanacak)/u
const consequential = /(a gönderildi|pazar yerine gönderildi|iade (?:kabul|ret|red)|fatura|etiket|kargo|paket|stok|stoğa|yayın|sonlandırıldı|oturum|temizlik tamamlandı|ürün oluşturuldu|silindi|dış yazma|yazma anahtar|eşitleme(?:si)? tamamlandı|aktarım tamamlandı)/u

export function shouldRecordNotification(message: string, kind: AppNotification['kind']): boolean {
  const text = message.toLocaleLowerCase('tr-TR')
  if (validationOrLocal.test(text)) return false
  if (kind === 'error') return true
  if (progressOnly.test(text)) return false
  return kind === 'success' && consequential.test(text)
}

// Failures caused by a closed external-write switch are fixed on the integration, not on the page that failed.
export function isExternalWriteMessage(message: string): boolean {
  return /(dış yazma|yazma anahtar|yazma kapı|yetenek kanıtı|external_write)/iu.test(message)
}

export function integrationNotificationTarget(connectionId: string | null | undefined, platformName: string): NotificationTarget {
  return connectionId ? { href: `/integrations/${connectionId}`, label: `${platformName} Entegrasyonuna Git` } : { href: '/integrations', label: 'Entegrasyonlara Git' }
}

// The shell knows the connections it has loaded; a producer that names only a connection gets its store name from it.
type ConnectionIdentity = { platformCode: string; displayName?: string }
let connectionResolver: ((connectionId: string) => ConnectionIdentity | null) | null = null
export function registerNotificationConnectionResolver(resolver: ((connectionId: string) => ConnectionIdentity | null) | null) {
  connectionResolver = resolver
}

/** The code and message of a failed request, for a notification's Kod row. */
export function notificationError(reason: unknown, fallback: string): { message: string; code?: string } {
  if (!(reason instanceof Error)) return { message: fallback }
  const code = (reason as { code?: unknown }).code
  return { message: reason.message || fallback, code: typeof code === 'string' && code ? code : undefined }
}

const titleCaseLowerWords = new Set(['ve', 'veya', 'ile', 'için', 'ya', 'da', 'de'])

/** A short failure sentence ("Bağlantı pasife alınamadı.") as a Title Case title; a long or compound one gives none. */
export function sentenceTitle(sentence: string): string | undefined {
  const clean = sentence.trim().replace(/[.…!]+$/u, '')
  const words = clean.split(/\s+/u).filter(Boolean)
  if (!clean || words.length > 7 || /[:;—,]/u.test(clean)) return undefined
  return words.map((word, index) => index > 0 && titleCaseLowerWords.has(word.toLocaleLowerCase('tr-TR'))
    ? word.toLocaleLowerCase('tr-TR')
    : word.charAt(0).toLocaleUpperCase('tr-TR') + word.slice(1)).join(' ')
}

/**
 * A failed request as a notice: the fallback sentence names the operation ("Bağlantı pasife alınamadı."), so it becomes
 * the title while the server's own message is the body and its code goes to the Ayrıntı rows.
 */
export function failureNotice(reason: unknown, fallback: string) {
  const failure = notificationError(reason, fallback)
  return { kind: 'error' as const, title: sentenceTitle(fallback), message: failure.message, context: { code: failure.code } }
}

export function notificationGroupKey(notification: Pick<AppNotification, 'groupKey' | 'kind' | 'title' | 'platformCode' | 'connectionId' | 'message'>) {
  return notification.groupKey ?? [notification.kind, notification.title ?? '', notification.platformCode ?? '', notification.connectionId ?? '', notification.message].join('|')
}

function sameSubject(left: NotificationSubject, right: NotificationSubject) {
  return left.kind === right.kind && (left.id && right.id ? left.id === right.id : left.label === right.label)
}

function subjectsOf(notification: AppNotification): NotificationSubject[] {
  return notification.items ?? (notification.subject ? [notification.subject] : [])
}

function mergeSubjects(newer: NotificationSubject[], older: NotificationSubject[]) {
  const merged = [...newer]
  for (const item of older) if (!merged.some(existing => sameSubject(existing, item))) merged.push(item)
  return merged.slice(0, 50)
}

// The newer notification of a burst takes the older one's place: its text and time, the sum of both counts and every
// record either touched. The id stays the older one's so an open Ayrıntı stays open.
function mergeBurst(newer: AppNotification, older: AppNotification): AppNotification {
  const items = mergeSubjects(subjectsOf(newer), subjectsOf(older))
  return {
    ...newer,
    id: older.id,
    read: newer.read && older.read,
    count: (newer.count ?? 1) + (older.count ?? 1),
    firstAt: older.firstAt ?? older.createdAt,
    items: items.length > 1 ? items : undefined,
    groupMessage: newer.groupMessage ?? older.groupMessage
  }
}

function withinBurst(newer: AppNotification, older: AppNotification) {
  const newest = Date.parse(newer.firstAt ?? newer.createdAt)
  const previous = Date.parse(older.createdAt)
  return Number.isFinite(newest) && Number.isFinite(previous) && Math.abs(newest - previous) <= burstWindowMs
}

function collapseBursts(history: AppNotification[]) {
  const collapsed: AppNotification[] = []
  for (const notification of history) {
    const previous = collapsed[collapsed.length - 1]
    if (previous && notificationGroupKey(previous) === notificationGroupKey(notification) && withinBurst(previous, notification)) {
      // History is newest first: the entry already kept is the newer one.
      const merged = mergeBurst(previous, notification)
      collapsed[collapsed.length - 1] = { ...merged, id: previous.id, firstAt: notification.firstAt ?? notification.createdAt }
    } else collapsed.push(notification)
  }
  return collapsed
}

export function readNotificationHistory(): AppNotification[] {
  try {
    const raw = localStorage.getItem(scopedStorageKey(notificationStorageKey))
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return []
    const entries = parsed.flatMap(item => {
      if (!item || typeof item !== 'object') return []
      const value = item as Record<string, unknown>
      if (typeof value.id !== 'string' || typeof value.message !== 'string' || typeof value.createdAt !== 'string'
        || (value.kind !== 'success' && value.kind !== 'error' && value.kind !== 'info')) return []
      const title = text(value.title, 80)
      const kind = normalizeNotificationKind(value.message, value.kind as AppNotification['kind'])
      // History saved before the recording policy existed may hold routine messages; drop them on read. A notification
      // a producer recorded on purpose (it carries its own title) stays.
      if (value.recorded !== true && !shouldRecordNotification(title ? `${title}: ${value.message}` : value.message, kind)) return []
      // A destination derived from the message follows the current rules: entries marked as inferred, entries
      // without one, and connection notices saved before they led to Integrations (they linked to Returns).
      const current = inferNotificationTarget(value.message)
      const inferred = typeof value.href !== 'string' || value.targetInferred === true || (current?.href === '/integrations' && /bağlantısında/u.test(value.message.toLocaleLowerCase('tr-TR'))) ? current : null
      const count = typeof value.count === 'number' && Number.isInteger(value.count) && value.count > 1 ? Math.min(value.count, 9999) : undefined
      return [{
        id: value.id,
        message: value.message,
        kind,
        createdAt: value.createdAt,
        read: value.read === true,
        href: inferred ? inferred.href : typeof value.href === 'string' ? value.href : undefined,
        actionLabel: inferred ? inferred.label : typeof value.actionLabel === 'string' ? value.actionLabel : undefined,
        targetInferred: inferred ? true : undefined,
        details: readNotificationDetails(value.details),
        title,
        operation: text(value.operation, 80),
        platformCode: text(value.platformCode, 40),
        connectionId: text(value.connectionId, 80),
        storeName: text(value.storeName, 120),
        subject: readSubject(value.subject),
        code: text(value.code, 120),
        jobId: text(value.jobId, 80),
        groupKey: text(value.groupKey, 200),
        groupMessage: text(value.groupMessage, 240),
        count,
        firstAt: typeof value.firstAt === 'string' ? value.firstAt : undefined,
        items: readSubjects(value.items),
        recorded: value.recorded === true ? true : undefined
      } satisfies AppNotification]
    })
    return collapseBursts(entries).slice(0, historyLimit)
  } catch {
    return []
  }
}

function writeHistory(history: AppNotification[]) {
  try {
    localStorage.setItem(scopedStorageKey(notificationStorageKey), JSON.stringify(history))
  } catch {
    // A full or restricted browser store must not break the operation that produced the notification.
    return false
  }
  return true
}

function normalizeInput(input: string | NotificationInput, kind: AppNotification['kind'], target?: NotificationTarget | null, details?: NotificationDetail[]): NotificationInput {
  return typeof input === 'string' ? { message: input, kind, target, details } : { ...input, kind: input.kind ?? kind }
}

/**
 * Adds a notification to the history (or to the burst it repeats). A plain sentence still works; a producer that knows
 * more passes the title, the marketplace and store, the record it is about and the failure's code.
 */
export function appendNotification(input: string | NotificationInput, kind: AppNotification['kind'] = 'info', target?: NotificationTarget | null, details?: NotificationDetail[]) {
  const value = normalizeInput(input, kind, target, details)
  const message = value.message.trim()
  if (!message) return null
  const title = value.title?.trim() || undefined
  const normalizedKind = normalizeNotificationKind(`${title ? `${title}: ` : ''}${message}`, value.kind ?? 'info')
  // An explicit kind from a producer that knows the outcome wins over the wording, except an error stays an error.
  const resolvedKind = value.kind === 'error' || normalizedKind === 'error' ? 'error' : title && value.kind ? value.kind : normalizedKind
  if (!value.record && !shouldRecordNotification(title ? `${title}: ${message}` : message, resolvedKind)) return null
  const connection = value.connectionId && (!value.storeName || !value.platformCode) ? connectionResolver?.(value.connectionId) ?? null : null
  const subject = value.subject ?? undefined
  const subjectTarget = subject?.href ? { href: subject.href, label: subjectActions[subject.kind] } : null
  const explicitTarget = value.target === undefined ? subjectTarget : value.target
  const destination = explicitTarget === null ? null : explicitTarget ?? inferNotificationTarget(message)
  const now = new Date().toISOString()
  const notification: AppNotification = {
    id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
    message,
    kind: resolvedKind,
    createdAt: now,
    read: false,
    href: destination?.href,
    actionLabel: destination?.label,
    targetInferred: value.target === undefined && !subjectTarget && destination ? true : undefined,
    details: value.details && value.details.length > 0 ? value.details.slice(0, 40) : undefined,
    title,
    operation: value.operation?.trim() || undefined,
    platformCode: value.platformCode?.trim().toUpperCase() || connection?.platformCode || undefined,
    connectionId: value.connectionId ?? undefined,
    storeName: value.storeName?.trim() || connection?.displayName || undefined,
    subject,
    code: value.code?.trim() || undefined,
    jobId: value.jobId ?? undefined,
    groupKey: value.groupKey,
    groupMessage: value.groupMessage
  }
  if (value.record) notification.recorded = true
  const history = readNotificationHistory()
  const latestSame = history.findIndex(existing => notificationGroupKey(existing) === notificationGroupKey(notification) && withinBurst(notification, existing))
  const next = latestSame >= 0
    ? [mergeBurst(notification, history[latestSame]), ...history.filter((_, index) => index !== latestSame)]
    : [notification, ...history]
  writeHistory(next.slice(0, historyLimit))
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
  return latestSame >= 0 ? next[0] : notification
}

export function markNotificationRead(notificationId: string) {
  const history = readNotificationHistory()
  const next = history.map(notification => notification.id === notificationId ? { ...notification, read: true } : notification)
  if (next.every((notification, index) => notification.read === history[index]?.read)) return
  if (!writeHistory(next)) return
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
}

export function markAllNotificationsRead() {
  const history = readNotificationHistory()
  if (!history.some(notification => !notification.read)) return
  const next = history.map(notification => notification.read ? notification : { ...notification, read: true })
  if (!writeHistory(next)) return
  window.dispatchEvent(new CustomEvent(notificationChangeEvent))
}

export function clearNotificationHistory() {
  try {
    localStorage.removeItem(scopedStorageKey(notificationStorageKey))
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
