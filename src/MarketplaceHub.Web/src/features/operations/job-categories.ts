// The tabs of İşlem Takibi and the job types each one asks the server for. The types come from this fixed catalogue
// (the server's MarketplaceJobTypes, InvoicingJobTypes, PanelJobTypes and the import jobs; job-categories.test.ts checks it against
// the server source), not from the live counts: a count refresh that brings a new type no longer changes the list
// query and sends the list back to its first page (G22).

export type JobCategory = 'ALL' | 'ORDERS' | 'PRICE_INVENTORY' | 'CATALOG' | 'LISTS' | 'QUESTIONS' | 'INVOICES' | 'FINANCE' | 'RETURNS' | 'SYSTEM'

export const knownJobTypes: readonly string[] = [
  // MarketplaceJobTypes
  'TRENDYOL_CONNECTION_TEST', 'TRENDYOL_REFERENCE_SYNC', 'HEPSIBURADA_REFERENCE_SYNC', 'TRENDYOL_ORDER_SYNC', 'TRENDYOL_ORDER_RECOVERY_SYNC',
  'ORDER_STATUS_SYNC', 'TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION', 'ORDER_RECONCILIATION', 'ORDER_INVOICE_RECONCILIATION', 'PRODUCT_SYNC',
  'SHIPMENT_ACTION', 'RETURN_SYNC', 'RETURN_STATUS_SYNC', 'RETURN_RECONCILIATION', 'RETURN_ACTION', 'EXTERNAL_EFFECT_RECONCILE',
  'TRENDYOL_WEBHOOK_INGEST', 'PRODUCT_CREATE', 'PRODUCT_APPROVAL_RECONCILE', 'PRODUCT_UPDATE', 'PRODUCT_ARCHIVE', 'LISTING_VERIFY', 'LISTING_LOOKUP', 'PRICE_INVENTORY_SYNC',
  'STOCK_PROJECTION_DISPATCH', 'STOCK_RECONCILIATION', 'TRENDYOL_COMMON_LABEL', 'TRENDYOL_CAPABILITY_PROBE', 'TRENDYOL_STAGE_TEST_ORDER',
  'SHOPIFY_CONNECTION_TEST', 'SHOPIFY_PRODUCT_SYNC', 'SHOPIFY_ORDER_SYNC', 'SHOPIFY_ORDER_RECOVERY_SYNC', 'SHOPIFY_ORDER_STATUS_SYNC',
  'SHOPIFY_ORDER_RECONCILIATION', 'SHOPIFY_ORDER_INVOICE_RECONCILIATION', 'SHOPIFY_WEBHOOK_INGEST', 'HEPSIBURADA_CONNECTION_TEST',
  'HEPSIBURADA_PRODUCT_SYNC', 'HEPSIBURADA_ORDER_SYNC', 'HEPSIBURADA_ORDER_RECOVERY_SYNC', 'HEPSIBURADA_ORDER_STATUS_SYNC',
  'HEPSIBURADA_WEBHOOK_INGEST', 'HEPSIBURADA_RETURN_SYNC', 'HEPSIBURADA_ORDER_INVOICE_RECONCILIATION', 'N11_CONNECTION_TEST',
  'N11_REFERENCE_SYNC', 'N11_ORDER_SYNC', 'N11_ORDER_RECOVERY_SYNC', 'PAZARAMA_CONNECTION_TEST', 'PAZARAMA_REFERENCE_SYNC',
  'PAZARAMA_ORDER_SYNC', 'PAZARAMA_ORDER_RECOVERY_SYNC', 'PAZARAMA_ORDER_STATUS_SYNC', 'PTTAVM_CONNECTION_TEST', 'PTTAVM_REFERENCE_SYNC', 'PTTAVM_ORDER_SYNC', 'PTTAVM_ORDER_RECOVERY_SYNC', 'PTTAVM_ORDER_STATUS_SYNC',
  'MARKETPLACE_QUESTION_SYNC', 'TRENDYOL_FINANCE_SYNC', 'PAZARAMA_FINANCE_SYNC', 'N11_SETTLEMENT_SYNC', 'HEPSIBURADA_FINANCE_SYNC',
  'CATALOG_STRUCTURE_IMPORT', 'CONNECTION_FULL_RESET',
  // InvoicingJobTypes
  'EFATURAM_CONNECTION_TEST', 'INVOICE_SUBMIT', 'INVOICE_RECONCILE', 'INVOICE_DOCUMENT_FETCH', 'INVOICE_MARKETPLACE_DELIVERY',
  'MANUAL_ORDER_INVOICE_DELIVERY', 'INVOICE_CANCELLATION', 'INVOICE_DUE_SCAN', 'EFATURAM_STAGE_CAPABILITY_PROBE',
  // ImportService
  'IMPORT_PREVIEW', 'IMPORT_APPLY',
  // PanelJobTypes (Ayarlar › Veri)
  'PANEL_FACTORY_RESET'
]

// Every known type sits in exactly one tab besides Tüm İşlemler (job-categories.test.ts), so the tab counts add up to the
// total. The first rule that fits wins: tests, probes and resets are Sistem; customer questions, finance reads and the
// invoice work (the order invoice checks included) have their own tabs; category / brand / attribute reads
// (*_REFERENCE_SYNC) are Katalog Listeleri, apart from product imports, publishing and updates (Ürün/Yayın);
// notifications (*_WEBHOOK_INGEST) bring orders.
export function jobCategoryOf(type: string): Exclude<JobCategory, 'ALL'> {
  if (/TEST|PROBE|PING|MIGRATION|SCHEDULER|RESET|EXTERNAL_EFFECT/i.test(type)) return 'SYSTEM'
  if (/QUESTION/i.test(type)) return 'QUESTIONS'
  if (/FINANCE|SETTLEMENT/i.test(type)) return 'FINANCE'
  if (/INVOICE|EFATURAM|BILLING/i.test(type)) return 'INVOICES'
  if (/RETURN|CLAIM/i.test(type)) return 'RETURNS'
  if (/REFERENCE_SYNC|CATEGORY|BRAND|ATTRIBUTE/i.test(type)) return 'LISTS'
  if (/PRICE|INVENTORY|STOCK|OFFER/i.test(type)) return 'PRICE_INVENTORY'
  if (/PRODUCT|CATALOG|IMPORT|PUBLICATION|LISTING/i.test(type)) return 'CATALOG'
  if (/ORDER|SHIPMENT|PACKAGE|COURIER|LABEL|WEBHOOK_INGEST/i.test(type)) return 'ORDERS'
  return 'SYSTEM'
}

const tab = (key: Exclude<JobCategory, 'ALL'>, label: string, title = label) => ({ key, label, title, match: (type: string) => jobCategoryOf(type) === key })
// label is the short name on the segment (it must fit one line at 1280 px); title is the full name for the tooltip and screen readers.
export const jobCategoryTabs: ReadonlyArray<{ key: JobCategory; label: string; title: string; match: (type: string) => boolean }> = [
  { key: 'ALL', label: 'Tümü', title: 'Tüm İşlemler', match: () => true },
  tab('ORDERS', 'Sipariş/Kargo'),
  tab('PRICE_INVENTORY', 'Fiyat/Stok'),
  tab('CATALOG', 'Ürün/Yayın'),
  tab('LISTS', 'Katalog', 'Katalog Listeleri'),
  tab('QUESTIONS', 'Sorular', 'Müşteri Soruları'),
  tab('INVOICES', 'Faturalar'),
  tab('FINANCE', 'Hakediş', 'Finans ve Hakediş'),
  tab('RETURNS', 'İadeler'),
  tab('SYSTEM', 'Sistem')
]

export function jobCategoryMatcher(category: JobCategory) {
  return jobCategoryTabs.find(tab => tab.key === category)?.match ?? (() => true)
}

const categoryJobTypes = new Map<JobCategory, readonly string[] | null>(jobCategoryTabs.map(tab => [tab.key, tab.key === 'ALL' ? null : knownJobTypes.filter(tab.match).sort()]))

/** The job types a tab asks the server for; null for every job (no filter). Fixed for the life of the page. */
export function jobTypesForCategoryTab(category: JobCategory): readonly string[] | null {
  return categoryJobTypes.get(category) ?? null
}
