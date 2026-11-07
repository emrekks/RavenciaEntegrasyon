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

// A blacklisted or locked variant is the marketplace's own block, not a pause: it shows red (the error colour), over
// every other closure, whether all variants or only some are closed.
const blockingReasons = new Set(['BLACKLISTED', 'LOCKED'])

// A marketplace's variants closed to sale among the matched ones, by the owner's rule (6 Nov 2026, every marketplace):
// a closure only for a stock of 0 is no closure at all (that variant reads as on sale), so only the other closures
// count. reasons: those closures by their named reason (server OffSaleReasonCounts); a closure without a named one is
// the rest. The reason is named only when every counted closure has the same one. blocking: one of them is blacklisted
// or locked. Null when none is known to be closed beyond a stock of 0.
export type OffSaleSummary = { closed: number; matched: number; allClosed: boolean; reason: string | null; reasonLabel: string | null; blocking: boolean; label: string }

export function offSaleSummary(matched: number, offSale: number, offSaleStockZero = 0, reasons?: Readonly<Record<string, number>> | null): OffSaleSummary | null {
  if (matched <= 0) return null
  const beyondStock = Math.max(0, offSale - offSaleStockZero)
  if (beyondStock <= 0) return null
  const named = Object.entries(reasons ?? {})
    .map(([reason, count]) => [reason.trim().toUpperCase(), count] as const)
    .filter(([reason, count]) => count > 0 && reason !== 'STOCK_ZERO')
  const namedTotal = named.reduce((sum, [, count]) => sum + count, 0)
  const unnamed = Math.max(0, beyondStock - namedTotal)
  const kinds = named.length + (unnamed > 0 ? 1 : 0)
  const reason = kinds === 1 && named.length === 1 ? named[0][0] : null
  const reasonLabel = saleReasonLabel(reason)
  const blocking = named.some(([code]) => blockingReasons.has(code))
  const closed = Math.min(beyondStock, matched)
  const allClosed = closed === matched
  const label = allClosed
    ? `Satışa Kapalı${reasonLabel ? ` · ${reasonLabel}` : ''}`
    : `${closed}/${matched} Varyant Satışa Kapalı${reasonLabel ? ` (${reasonLabel})` : ''}`
  return { closed, matched, allClosed, reason, reasonLabel, blocking, label }
}
