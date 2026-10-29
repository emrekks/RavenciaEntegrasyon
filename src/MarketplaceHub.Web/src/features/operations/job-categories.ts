// The tabs of İşlem Takibi and the job types each one asks the server for. The types come from this fixed catalogue
// (the server's MarketplaceJobTypes, InvoicingJobTypes and the import jobs; job-categories.test.ts checks it against
// the server source), not from the live counts: a count refresh that brings a new type no longer changes the list
// query and sends the list back to its first page (G22).

export type JobCategory = 'ALL' | 'ORDERS' | 'PRICE_INVENTORY' | 'CATALOG' | 'INVOICES' | 'RETURNS' | 'SYSTEM'

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
  'MARKETPLACE_QUESTION_SYNC', 'TRENDYOL_FINANCE_SYNC', 'PAZARAMA_FINANCE_SYNC',
  'CATALOG_STRUCTURE_IMPORT',
  // InvoicingJobTypes
  'EFATURAM_CONNECTION_TEST', 'INVOICE_SUBMIT', 'INVOICE_RECONCILE', 'INVOICE_DOCUMENT_FETCH', 'INVOICE_MARKETPLACE_DELIVERY',
  'MANUAL_ORDER_INVOICE_DELIVERY', 'INVOICE_CANCELLATION', 'INVOICE_DUE_SCAN', 'EFATURAM_STAGE_CAPABILITY_PROBE',
  // ImportService
  'IMPORT_PREVIEW', 'IMPORT_APPLY'
]

export const jobCategoryTabs: ReadonlyArray<{ key: JobCategory; label: string; match: (type: string) => boolean }> = [
  { key: 'ALL', label: 'Tüm İşlemler', match: () => true },
  { key: 'ORDERS', label: 'Sipariş/Kargo', match: t => /ORDER|SHIPMENT|PACKAGE|COURIER|LABEL/i.test(t) },
  { key: 'PRICE_INVENTORY', label: 'Fiyat/Stok', match: t => /PRICE|INVENTORY|STOCK|OFFER/i.test(t) },
  { key: 'CATALOG', label: 'Ürün/Yayın', match: t => /PRODUCT|CATALOG|IMPORT|ATTRIBUTE|CATEGORY|BRAND|PUBLICATION|LISTING/i.test(t) },
  { key: 'INVOICES', label: 'Faturalar', match: t => /INVOICE|EFATURAM|BILLING/i.test(t) },
  { key: 'RETURNS', label: 'İadeler', match: t => /RETURN|CLAIM/i.test(t) },
  { key: 'SYSTEM', label: 'Sistem & Test', match: t => /TEST|PROBE|PING|MIGRATION|SCAN|SCHEDULER/i.test(t) }
]

export function jobCategoryMatcher(category: JobCategory) {
  return jobCategoryTabs.find(tab => tab.key === category)?.match ?? (() => true)
}

const categoryJobTypes = new Map<JobCategory, readonly string[] | null>(jobCategoryTabs.map(tab => [tab.key, tab.key === 'ALL' ? null : knownJobTypes.filter(tab.match).sort()]))

/** The job types a tab asks the server for; null for every job (no filter). Fixed for the life of the page. */
export function jobTypesForCategoryTab(category: JobCategory): readonly string[] | null {
  return categoryJobTypes.get(category) ?? null
}
