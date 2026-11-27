// What the overview (Genel Bakış), the notification bell and İşlem Takibi read besides the job list: the day's revenue
// per store and the cancellations, background notices, notification and product-activity logs, the API health and the
// price/stock monitor. Synthetic data; stores are the shell's connections.
import { channelDay, channelProfiles, connectionIds, ist, orders } from './sales.mjs'

const stores = Object.keys(channelProfiles).map(code => ({ platformCode: code, connectionId: connectionIds[code] }))
const names = { TRENDYOL: 'Ravencia Moda Trendyol', HEPSIBURADA: 'Ravencia Moda Hepsiburada', SHOPIFY: 'Ravencia Shopify Mağazası', N11: 'Ravencia Moda n11', PAZARAMA: 'Ravencia Moda Pazarama' }
const channels = stores.map(store => ({ connectionId: store.connectionId, platformCode: store.platformCode, displayName: names[store.platformCode], status: 'ACTIVE' }))

const frozenDay = '2026-10-20'
const addDays = (day, count) => new Date(Date.parse(`${day}T00:00:00Z`) + count * 86_400_000).toISOString().slice(0, 10)
// The shared generator gives a part of the day on the frozen day and nothing after it; a clock past it (a preview in a
// live browser) still shows whole days.
const dayActivity = (code, day) => day > frozenDay ? channelDay(code, addDays(frozenDay, -1)) : channelDay(code, day)
const rows = (from, to) => {
  const result = []
  for (let day = from, guard = 0; day <= to && guard < 400; day = addDays(day, 1), guard += 1) {
    for (const { platformCode, connectionId } of stores) {
      const part = dayActivity(platformCode, day)
      result.push({ day, connectionId, currency: 'TRY', amount: part.amount, orderCount: part.orderCount, productQuantity: part.productQuantity, packageCount: part.shipmentCount, returnedAmount: Math.round(part.amount * 0.04), returnedOrderCount: Math.round(part.orderCount * 0.04) })
    }
  }
  return result
}
const dayCount = (from, to) => Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000) + 1
const hours = (day, from) => stores.flatMap(({ platformCode, connectionId }) => {
  const part = dayActivity(platformCode, day)
  return Array.from({ length: 24 }, (_, hour) => {
    const weight = Math.max(0.2, Math.sin(((hour - 6) / 17) * Math.PI)) / 9
    return { hour: `${from}T${String(hour).padStart(2, '0')}:00`, connectionId, currency: 'TRY', amount: Math.round(part.amount * weight), orderCount: Math.round(part.orderCount * weight), productQuantity: Math.round(part.productQuantity * weight), packageCount: Math.round(part.shipmentCount * weight), returnedAmount: 0, returnedOrderCount: 0 }
  })
})

function performance({ url }) {
  const from = url.searchParams.get('dateFrom') ?? url.searchParams.get('from') ?? frozenDay
  const to = url.searchParams.get('dateTo') ?? url.searchParams.get('to') ?? from
  const length = dayCount(from, to)
  const previousTo = addDays(from, -1), previousFrom = addDays(previousTo, -(length - 1))
  const single = from === to
  return {
    from, to, previousFrom, previousTo, currentStart: ist(`${from} 00:00`), currentEnd: ist(`${to} 23:59`), previousStart: ist(`${previousFrom} 00:00`), previousEnd: ist(`${previousTo} 23:59`),
    partialDay: false, channels, current: rows(from, to), previous: rows(previousFrom, previousTo),
    currentHours: single ? hours(from, from) : null, previousHours: single ? hours(previousTo, previousTo) : null,
    unreadableLineOrders: 0, firstOrderDays: Object.fromEntries(stores.map(store => [store.connectionId, '2026-01-12'])),
  }
}

const cancellations = ({ url }) => {
  const day = url.searchParams.get('day') ?? frozenDay
  return {
    day, start: ist(`${day} 00:00`), end: ist(`${day} 23:59`),
    stores: [
      { connectionId: 'c-ty', cancelledOrders: 2, partialOrders: 1, cancelledUnits: 3, unknownTimeOrders: 0, unknownTimeUnits: 0, wholeUnits: 2, partialUnits: 1 },
      { connectionId: 'c-hb', cancelledOrders: 1, partialOrders: 0, cancelledUnits: 1, unknownTimeOrders: 0, unknownTimeUnits: 0, wholeUnits: 1, partialUnits: 0 },
    ],
  }
}

const countBy = (items, key) => items.reduce((counts, item) => ({ ...counts, [item[key]]: (counts[item[key]] ?? 0) + 1 }), {})
const orderFacets = { platforms: countBy(orders, 'platformCode'), cargos: {} }

const minutesBefore = minutes => new Date(Date.parse(ist('2026-10-20 09:20')) - minutes * 60_000).toISOString()
const notification = (id, severity, category, title, message, minutes, extra = {}) => ({
  id, clientKey: `k-${id}`, severity, category, title, message, href: null, actionLabel: null, platformCode: null, storeName: null, count: 1, payload: null, shared: true,
  createdAt: minutesBefore(minutes), updatedAt: minutesBefore(minutes), readAt: null, dismissedAt: null, ...extra,
})
const notifications = [
  notification('n1', 'ERROR', 'CONNECTION', 'Pazarama Yetkisi Doldu', 'Pazarama API anahtarı reddedildi; yeni anahtarı Entegrasyonlar bölümünden girin.', 12, { platformCode: 'PAZARAMA', storeName: 'Ravencia Moda Pazarama', href: '/integrations' }),
  notification('n2', 'WARNING', 'JOB', 'Stok Gönderimi Gecikiyor', 'Hepsiburada stok gönderimi hız sınırına takıldı; otomatik yeniden denenecek.', 55, { platformCode: 'HEPSIBURADA', storeName: 'Ravencia Moda Hepsiburada' }),
  notification('n3', 'SUCCESS', 'INVOICE', 'Faturalar Gönderildi', '6 faturanın bağlantısı pazaryerine iletildi.', 180, { readAt: ist('2026-10-20 07:00') }),
  notification('n4', 'INFO', 'ORDER', 'Yeni Siparişler', 'Son bir saatte 4 yeni sipariş geldi.', 240, { readAt: ist('2026-10-20 06:00'), count: 4, href: '/orders' }),
]

// The bell's "Yeni Sipariş" notices (GET /notifications/new-orders, NewOrderNoticePolicy): invented stores and numbers.
const newOrderSubject = number => ({ kind: 'order', id: `o-${number}`, label: number, href: `/orders?search=${number}` })
const newOrderNotice = (id, connection, platformCode, storeName, message, minutes, href, actionLabel, card) => ({
  ...notification(id, 'INFO', 'ORDER', 'Yeni Sipariş', message, minutes, { clientKey: `new-orders:${connection}:${id}`, platformCode, storeName, href, actionLabel }),
  payload: JSON.stringify({ connectionId: connection, ...card }),
})
const newOrderNotices = [
  newOrderNotice('no1', 'c-ty', 'TRENDYOL', 'Ravencia Moda Trendyol', 'Trendyol: 3 yeni paket', 4, '/orders?status=NEW', 'Siparişlere Git', { items: ['10400000011', '10400000012', '10400000013'].map(newOrderSubject) }),
  newOrderNotice('no2', 'c-pz', 'PAZARAMA', 'Ravencia Moda Pazarama', 'Pazarama: 1 yeni paket, sipariş PZR-200001', 26, '/orders?search=PZR-200001', 'Siparişe Git', { subject: newOrderSubject('PZR-200001') }),
]

const activity = (id, minutes, kind, type, extra) => ({
  id, occurredAt: minutesBefore(minutes), kind, type, productId: 'p1', productTitle: 'Kadın Kemerli Trençkot', imageUrl: null, variantId: 'v1', sku: 'RVK-TRC-0377-BEJ-38',
  barcode: '8684471203771', modelCode: 'RVK-TRC-0377', optionText: 'Renk: Bej | Beden: 38', source: 'ORDER', note: null, connectionId: 'c-ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Moda Trendyol', ...extra,
})
const activityRows = [
  activity('a1', 9, 'STOCK', 'ORDER_SOLD', { quantityDelta: -1, stockBefore: 6, stockAfter: 5, orderId: 'o-1001', orderNumber: '10400000001' }),
  activity('a2', 47, 'PRICE', 'PRICE_CHANGED', { source: 'PRICE_EDIT', previousSalePrice: 1599.9, newSalePrice: 1499.9, currency: 'TRY', actorName: 'Ayşe Yılmaz' }),
  activity('a3', 130, 'STOCK', 'RETURN_RECEIVED', { source: 'RETURN', quantityDelta: 1, stockBefore: 4, stockAfter: 5, returnId: 'r-1', returnNumber: 'RV-IAD-0007' }),
  activity('a4', 300, 'STATUS', 'STATUS_CHANGED', { source: 'EDITOR', previousStatus: 'DRAFT', newStatus: 'ACTIVE', actorName: 'Ayşe Yılmaz' }),
]
const activityCounts = Object.entries(countBy(activityRows, 'kind')).map(([kind, count]) => ({ kind, count }))

const apiHealth = {
  generatedAt: ist('2026-10-20 09:29'),
  connections: Object.keys(names).map((code, index) => ({
    connectionId: connectionIds[code], platformCode: code, displayName: names[code],
    auth: code === 'PAZARAMA' ? { state: 'REJECTED', lastRejectedAt: ist('2026-10-20 09:10'), lastRejectedCode: 'AUTH_EXPIRED', lastOkAt: ist('2026-10-19 22:40') } : { state: 'OK', lastRejectedAt: null, lastRejectedCode: null, lastOkAt: ist('2026-10-20 09:28') },
    lag: { stream: 'ORDERS', lagSeconds: 40 + index * 30, expectedSeconds: 300, level: 'OK' },
    errorRate: { attempts: 288 - index * 40, failed: index === 4 ? 21 : index, remoteSide: index === 4 ? 21 : index, ourSide: 0, rateLimited: index === 1 ? 4 : 0, workflowWait: 0 },
    rateLimit: { refused24h: index === 1 ? 12 : 0, waitingJobs: index === 1 ? 2 : 0, pausedUntil: null, lastRunRefusals: 0 },
    queue: { ageSeconds: 20 + index * 15, jobType: 'ORDER_SYNC', pending: index === 1 ? 2 : 0 },
    webhook: { used: code === 'TRENDYOL' || code === 'SHOPIFY', lastReceivedAt: code === 'TRENDYOL' ? ist('2026-10-20 09:27') : null, healthy: true, lastOrderReadAt: ist('2026-10-20 09:28'), fallbackReadSeconds: 900 },
  })),
}

const monitorConnection = (code, extra = {}) => ({
  connectionId: connectionIds[code], platformCode: code, displayName: names[code], status: 'ACTIVE', masterSwitchOn: false, priceWriteEnabled: false, stockWriteEnabled: false, automaticWritesOpen: false,
  lastSuccessfulPushAt: ist('2026-10-20 08:40'), queuedJobs: 0, waitingForOrderRead: 0, failedPushes7d: 0, manualReviewPushes: 0, invalidOffers: 0, notFoundLinks: 0, rejectedLinks: 0, latestFailure: null,
  invalidOfferSamples: [], orderSource: 'POLLING', lastNotificationAt: null, orderReadIntervalSeconds: 300, lostListings: [], ...extra,
})
const priceStockMonitor = {
  generatedAt: ist('2026-10-20 09:29'),
  connections: [
    monitorConnection('TRENDYOL', { orderSource: 'WEBHOOK', lastNotificationAt: ist('2026-10-20 09:27') }),
    monitorConnection('HEPSIBURADA', { failedPushes7d: 3, latestFailure: { jobId: 'job-03', errorCode: 'RATE_LIMITED', errorSummary: 'Hız sınırına takıldı', status: 'RETRY_SCHEDULED', at: ist('2026-10-20 09:22') } }),
    monitorConnection('N11'),
    monitorConnection('PAZARAMA'),
  ],
}

export const monitoring = [
  ['GET /dashboard/performance', performance],
  ['GET /dashboard/cancellations', cancellations],
  ['GET /jobs/attention', []],
  ['GET /auth/invitations', []],
  ['GET /settings/resend-reminder', { count: 0, version: 1 }],
  ['GET /manual-resends', { items: [] }],
  ['GET /orders/facets', orderFacets],
  ['GET /notifications/new-orders', newOrderNotices],
  ['GET /notifications/summary', { total: notifications.length, unread: notifications.filter(item => !item.readAt).length, errors: 1 }],
  ['GET /notifications', { items: notifications, nextCursor: null, hasMore: false, totalCount: notifications.length, supportedFilters: null }],
  ['GET /product-activity/counts', activityCounts],
  ['GET /product-activity', { items: activityRows, nextCursor: null, hasMore: false, totalCount: activityRows.length, supportedFilters: null }],
  ['GET /api-health-signals', apiHealth],
  ['GET /price-stock-monitor', priceStockMonitor],
  ['GET /price-stock-send-log/counts', []],
  ['GET /settings/platform-update', { settings: null, version: 0 }],
]
