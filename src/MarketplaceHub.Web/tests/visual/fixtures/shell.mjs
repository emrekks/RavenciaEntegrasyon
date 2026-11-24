// What the signed-in shell asks on every page: the menu counts (dashboard bootstrap, order and invoice summaries, the
// waiting-question count), the account's appearance settings and the platform connections.
// Fixed data only: every timestamp sits around the frozen clock (2026-10-20 09:30 Istanbul).
import { fixtures } from '../fixtures.mjs'
import { ist, orderSummary } from './sales.mjs'

const connection = (id, platformCode, displayName, externalStoreId, extra = {}) => ({
  id, publicId: `pub-${id}`, platformCode, environment: 'PRODUCTION', displayName, externalStoreId, status: 'ACTIVE', apiVersion: 'v2',
  lastTestedAt: ist('2026-10-18 11:05'), lastSuccessAt: ist('2026-10-20 09:24'), lastErrorCode: null, hasCredential: true,
  externalWritesEnabled: true, invoiceCreationEnabled: true, n11ShipmentTemplate: null, n11PreparingDay: null, version: 3, ...extra
})

export const connections = [
  connection('c-ty', 'TRENDYOL', 'Ravencia Moda Trendyol', '784512'),
  connection('c-hb', 'HEPSIBURADA', 'Ravencia Moda Hepsiburada', 'ravenciamoda', { version: 5 }),
  connection('c-sh', 'SHOPIFY', 'Ravencia Shopify Mağazası', 'ravencia-moda.myshopify.com', { apiVersion: '2025-07' }),
  connection('c-n11', 'N11', 'Ravencia Moda n11', '1209833', { invoiceCreationEnabled: false, n11ShipmentTemplate: 'Standart Kargo', n11PreparingDay: 2 }),
  connection('c-pz', 'PAZARAMA', 'Ravencia Moda Pazarama', '5d1f0c2a', { lastSuccessAt: ist('2026-10-19 22:40'), lastErrorCode: 'AUTH_EXPIRED', externalWritesEnabled: false, invoiceCreationEnabled: false }),
  connection('c-ef', 'TRENDYOL_EFATURAM', 'Trendyol E-Faturam', '784512', { apiVersion: 'v1', invoiceCreationEnabled: false }),
]

// GET /dashboard/bootstrap (DashboardBootstrapView): DashboardPage, OrdersPage and the shell read it under the one
// ['dashboard-bootstrap'] query key. Counts per store are keyed by connection id.
const salesConnections = connections.filter(item => item.platformCode !== 'TRENDYOL_EFATURAM')
export const dashboardBootstrap = {
  metrics: {
    pendingOrders: orderSummary.new + orderSummary.processing, lateOrders: 1, pendingReturns: 3,
    pendingByConnection: { 'c-ty': 2, 'c-hb': 1, 'c-sh': 1 },
    lateByConnection: { 'c-ty': 1 },
    pendingReturnsByConnection: { 'c-ty': 2, 'c-hb': 1 },
    pendingUnitsByConnection: { 'c-ty': 3, 'c-hb': 1, 'c-sh': 1 },
    deadJobCount: 2, manualReviewJobCount: 1, recentJobCount: 1846
  },
  channels: salesConnections.map(item => ({ connectionId: item.id, platformCode: item.platformCode, displayName: item.displayName, status: item.status })),
  generatedAt: ist('2026-10-20 09:29'),
  version: 1
}

const questionWaitingCount = 7

// The waiting-question count shares GET /questions with the questions page (only the query string differs, and the
// mock matches the path alone): the summary query is answered here, every other /questions request goes to the next
// fixture that matches it (the questions page's), as if this entry were not there.
function laterAnswer(self, context, method = 'GET') {
  const index = fixtures.indexOf(self)
  for (const [matcher, answer] of fixtures.slice(index + 1)) {
    const [matchMethod, pattern] = matcher.includes(' ') ? matcher.split(' ') : ['GET', matcher]
    if (matchMethod !== method) continue
    const match = pattern.startsWith('^') ? context.path.match(new RegExp(pattern)) : context.path === pattern ? [context.path] : null
    if (!match) continue
    return typeof answer === 'function' ? answer({ ...context, match }) : answer
  }
  return { items: [], totalCount: 0, hasMore: false, nextCursor: null }
}
const questionSummaryEntry = ['GET /questions', context => {
  const query = context.url.searchParams
  const isWaitingSummary = query.get('status') === 'WAITING' && query.get('limit') === '1' && (query.get('kind') ?? 'ALL') === 'ALL'
  return isWaitingSummary ? { items: [], totalCount: questionWaitingCount, hasMore: true, nextCursor: null } : laterAnswer(questionSummaryEntry, context)
}]

export const shell = [
  ['GET /settings/appearance', { settings: null, version: 0 }],
  ['GET /dashboard/bootstrap', dashboardBootstrap],
  ['GET /invoice-workspace/summary', { dueSoonCount: 2, uninvoicedCount: 8 }],
  ['GET /orders/summary', ({ url }) => orderSummary.forPlatform(url.searchParams.get('platform'))],
  questionSummaryEntry,
  ['GET /connections', { items: connections, nextCursor: null, hasMore: false, totalCount: connections.length }],
  ['^/connections/([^/]+)/full-reset$', { items: [{ scope: 'orders', count: 240 }, { scope: 'products', count: 86 }, { scope: 'offers', count: 312 }], keptProducts: 0, job: null }],
  ['^/connections/([^/]+)$', ({ match }) => connections.find(item => item.id === decodeURIComponent(match[1])) ?? connections[0]],
]
