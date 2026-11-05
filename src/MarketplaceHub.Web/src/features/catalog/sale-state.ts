// One rule for every marketplace: a listing that is approved and listed may still be closed to sale there (Trendyol
// onSale=false, Hepsiburada isSalable=false, n11 saleStatus, Pazarama productStatus 10, PTT AVM Aktif=false, Shopify
// DRAFT/ARCHIVED). The sale state sits beside the publication status ("Yayında") and never replaces it: a closed
// listing reads "Satışa Kapalı", one the marketplace did not say anything about stays unknown ("Bilinmiyor").

export type SaleState = 'ON_SALE' | 'OFF_SALE'

const saleStateLabels: Record<string, string> = { ON_SALE: 'Satışta', OFF_SALE: 'Satışa Kapalı' }

export function saleStateLabel(state?: string | null) {
  return saleStateLabels[state?.trim().toUpperCase() ?? ''] ?? 'Bilinmiyor'
}

const saleReasonLabels: Record<string, string> = {
  STOCK_ZERO: 'Stok 0',
  PRICE_ZERO: 'Fiyat 0',
  BY_SELLER: 'Satıcı Kapattı',
  SUSPENDED: 'Askıda',
  LOCKED: 'Kilitli',
  FROZEN: 'Donduruldu',
  BLACKLISTED: 'Kara Listede',
  ARCHIVED: 'Arşivde',
  DRAFT: 'Taslak',
  BEFORE_SALE: 'Satış Öncesi'
}

export function saleReasonLabel(reason?: string | null) {
  return saleReasonLabels[reason?.trim().toUpperCase() ?? ''] ?? null
}

export function isOffSale(state?: string | null) {
  return state?.trim().toUpperCase() === 'OFF_SALE'
}

// A marketplace's closed variants among the matched ones. allClosed: every matched variant is closed there; stockOnly:
// every closed one names a stock of 0 as its only reason. Null when none is known to be closed.
export type OffSaleSummary = { closed: number; matched: number; allClosed: boolean; stockOnly: boolean; label: string }

export function offSaleSummary(matched: number, offSale: number, offSaleStockZero = 0): OffSaleSummary | null {
  if (offSale <= 0 || matched <= 0) return null
  const closed = Math.min(offSale, matched)
  const allClosed = closed === matched
  const stockOnly = offSaleStockZero >= offSale
  const label = allClosed
    ? `Satışa Kapalı${stockOnly ? ' · Stok 0' : ''}`
    : `${closed}/${matched} varyant satışa kapalı${stockOnly ? ' (stok 0)' : ''}`
  return { closed, matched, allClosed, stockOnly, label }
}
