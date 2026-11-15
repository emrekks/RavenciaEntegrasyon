// Every query key of the panel is built here (WEB-ALTYAPI-10). A key is its root followed by the parts that tell its
// reads apart: queryKeys.orders('page', filters) is ['orders', 'page', filters], and queryKeys.orders() is the root
// ['orders'] that refreshes (invalidateQueries) every read under it. Building keys in one place keeps a refresh from
// naming a root no query uses, and query-keys.test.ts fails on a key written as a literal array anywhere else.
//
// Each root also says which realtime announcements refresh it (the resources the server names: AppDbContext.OutboxChange
// and the job broadcaster). app/realtime-query-keys.ts derives its refresh map from these, so a new root has to decide
// whether a realtime change refreshes it ([] when none does) and cannot be forgotten there.
//
// The arrays are exactly the ones the screens wrote by hand before; only where they are written changed. A typed client
// generated from the API's OpenAPI document can later give each root typed parts and a queryOptions with its queryFn
// without changing the keys (connectionQueries below is the first such read).
import { queryOptions } from '@tanstack/react-query'
import type { ApiAnswer } from './api-answer'
// Through the index, the module the screens import, so a test that mocks '…/shared/api' mocks these reads too.
import { loadAllPages } from './index'

/** The resources a realtime change announcement names (useOperationsRealtime). */
export type RealtimeResource = 'orders' | 'returns' | 'products' | 'inventory' | 'invoices' | 'connections' | 'questions' | 'dashboard' | 'jobs'

export type QueryRoot<R extends string = string> = {
  <const P extends readonly unknown[]>(...parts: P): readonly [R, ...P]
  /** The root string, the first item of every key it builds. */
  readonly root: R
  /** The realtime announcements that refresh every query under this root. */
  readonly refreshedBy: readonly RealtimeResource[]
}

function queryRoot<const R extends string>(root: R, refreshedBy: readonly RealtimeResource[]): QueryRoot<R> {
  const key = <const P extends readonly unknown[]>(...parts: P): readonly [R, ...P] => [root, ...parts]
  return Object.assign(key, { root, refreshedBy })
}

export const queryKeys = {
  // Refreshed by realtime announcements (app/realtime-query-keys.ts).
  orders: queryRoot('orders', ['orders', 'invoices']),
  shipment: queryRoot('shipment', ['orders']),
  invoiceWorkspacePage: queryRoot('invoice-workspace-page', ['orders', 'invoices', 'connections']),
  invoiceWorkspaceSummary: queryRoot('invoice-workspace-summary', ['orders', 'invoices', 'connections']),
  // The operational cards and menu badges: a cheap snapshot read every data change refreshes; the worker announces
  // "dashboard" after it rebuilt the snapshot.
  dashboardBootstrap: queryRoot('dashboard-bootstrap', ['orders', 'returns', 'products', 'inventory', 'invoices', 'connections', 'dashboard']),
  returns: queryRoot('returns', ['returns']),
  return: queryRoot('return', ['returns']),
  // Stock lives on the product screens; there is no separate inventory query.
  products: queryRoot('products', ['products', 'inventory']),
  product: queryRoot('product', ['products', 'inventory']),
  publicationStatus: queryRoot('publication-status', ['products']),
  invoice: queryRoot('invoice', ['invoices']),
  invoiceViewer: queryRoot('invoice-viewer', ['invoices']),
  connections: queryRoot('connections', ['connections']),
  connection: queryRoot('connection', ['connections']),
  // The revenue panel reads a whole period and refreshes itself every minute while on screen; one order event does not
  // refetch it, a connection change does.
  dashboard: queryRoot('dashboard', ['connections']),
  questions: queryRoot('questions', ['questions']),
  jobs: queryRoot('jobs', ['jobs']),
  job: queryRoot('job', ['jobs']),

  // Not refreshed by realtime announcements.
  aiSettings: queryRoot('ai-settings', []),
  aiUsage: queryRoot('ai-usage', []),
  alertEmailSettings: queryRoot('alert-email-settings', []),
  apiHealthSignals: queryRoot('api-health-signals', []),
  appearanceSettings: queryRoot('appearance-settings', []),
  attributeDefaults: queryRoot('attribute-defaults', []),
  attributeMappings: queryRoot('attribute-mappings', []),
  attributeNameHints: queryRoot('attribute-name-hints', []),
  attributeUsedValues: queryRoot('attribute-used-values', []),
  attributeValueMappings: queryRoot('attribute-value-mappings', []),
  attributes: queryRoot('attributes', []),
  brandMapping: queryRoot('brand-mapping', []),
  brandMappings: queryRoot('brand-mappings', []),
  brandUnmapped: queryRoot('brand-unmapped', []),
  brands: queryRoot('brands', []),
  candidates: queryRoot('candidates', []),
  capabilities: queryRoot('capabilities', []),
  categories: queryRoot('categories', []),
  categoryMapping: queryRoot('category-mapping', []),
  categoryMappings: queryRoot('category-mappings', []),
  categoryRequirements: queryRoot('category-requirements', []),
  commissionTariffFacets: queryRoot('commission-tariff-facets', []),
  commissionTariffRows: queryRoot('commission-tariff-rows', []),
  commissionTariffSelection: queryRoot('commission-tariff-selection', []),
  commissionTariffs: queryRoot('commission-tariffs', []),
  connectionFullReset: queryRoot('connection-full-reset', []),
  import: queryRoot('import', []),
  imports: queryRoot('imports', []),
  instantNotifications: queryRoot('instant-notifications', []),
  managedUsers: queryRoot('managed-users', []),
  mappingConflicts: queryRoot('mapping-conflicts', []),
  marketplaceNameHints: queryRoot('marketplace-name-hints', []),
  me: queryRoot('me', []),
  modelCodeHolder: queryRoot('model-code-holder', []),
  n11ShipmentTemplates: queryRoot('n11-shipment-templates', []),
  notificationCenter: queryRoot('notification-center', []),
  offsiteBackup: queryRoot('offsite-backup', []),
  orderLabelDetail: queryRoot('order-label-detail', []),
  orderLabelDetails: queryRoot('order-label-details', []),
  panelFactoryReset: queryRoot('panel-factory-reset', []),
  pazaramaFinance: queryRoot('pazarama-finance', []),
  priceStockMonitor: queryRoot('price-stock-monitor', []),
  priceStockSendLog: queryRoot('price-stock-send-log', []),
  productActivity: queryRoot('product-activity', []),
  productDeletePreview: queryRoot('product-delete-preview', []),
  productMarketplaceCodes: queryRoot('product-marketplace-codes', []),
  promotionOffer: queryRoot('promotion-offer', []),
  promotionOffers: queryRoot('promotion-offers', []),
  publicationAttributeReadiness: queryRoot('publication-attribute-readiness', []),
  publicationJob: queryRoot('publication-job', []),
  publicationJobDetail: queryRoot('publication-job-detail', []),
  questionTemplates: queryRoot('question-templates', []),
  queueQuota: queryRoot('queue-quota', []),
  referenceAttributeValues: queryRoot('reference-attribute-values', []),
  referenceAttributes: queryRoot('reference-attributes', []),
  referenceBrands: queryRoot('reference-brands', []),
  referenceCategories: queryRoot('reference-categories', []),
  referenceValueSyncJob: queryRoot('reference-value-sync-job', []),
  reportOrderProfit: queryRoot('report-order-profit', []),
  reports: queryRoot('reports', []),
  reportsPayoutLines: queryRoot('reports-payout-lines', []),
  reportsPayouts: queryRoot('reports-payouts', []),
  reportsProfitRate: queryRoot('reports-profit-rate', []),
  reportsSales: queryRoot('reports-sales', []),
  reportsStock: queryRoot('reports-stock', []),
  returnAudits: queryRoot('return-audits', []),
  returnReasons: queryRoot('return-reasons', []),
  returnShipmentCompanies: queryRoot('return-shipment-companies', []),
  security: queryRoot('security', []),
  sessions: queryRoot('sessions', []),
  settings: queryRoot('settings', []),
  shipmentCarriers: queryRoot('shipment-carriers', []),
  shippingLabelSettings: queryRoot('shipping-label-settings', []),
  sizeChart: queryRoot('size-chart', []),
  syncPolicies: queryRoot('sync-policies', []),
  variantCodesTaken: queryRoot('variant-codes-taken', []),
} as const satisfies Record<string, QueryRoot>

export type QueryKeyName = keyof typeof queryKeys

/**
 * Every cached copy of one invoice (or, without an id, of all invoices): the invoice page's detail and the order screen's
 * invoice viewer (W1). An invoice action refreshes both, so the viewer never shows the invoice as it was before.
 */
export const invoiceCopies = (id?: string) => id === undefined
  ? [queryKeys.invoice(), queryKeys.invoiceViewer()] as const
  : [queryKeys.invoice(id), queryKeys.invoiceViewer(id)] as const

/** Every root, in declaration order. */
export const queryRootList: readonly QueryRoot[] = Object.values(queryKeys)

// Reads shared by several screens: the key and the request together, so two screens asking the same request share one
// cache entry. Options that differ per screen (staleTime, enabled, select) are added where the query is used.
export const connectionQueries = {
  /**
   * Every connection (GET /connections, all pages). Orders, returns, invoices, mappings, promotions, the product editor
   * and the dashboard all read this one entry, so switching between them does not read the list again; every write
   * that refreshes ['connections'] refreshes it; a screen narrows the list with `select`. `T` is only the shape the
   * screen reads of each item (the contract's connection by default).
   */
  list: <T = ApiAnswer<'getConnections'>['items'][number]>() => queryOptions({ queryKey: queryKeys.connections(), queryFn: ({ signal }) => loadAllPages<T>('/connections', { signal }) }),
}
