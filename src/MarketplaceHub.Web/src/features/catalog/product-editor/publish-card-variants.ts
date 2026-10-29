import type { VariantPlatformStatus } from '../shared'

type VariantRow = { platformStatuses?: VariantPlatformStatus[] | null }

// How the family's variants stand on one connection: present there (a variant link or a LIVE listing) and missing (the
// marketplace no longer knows them: link NOT_FOUND or REJECTED). A variant with neither stays out of both: no link is
// never read as missing.
export function publishCardVariantLinks(rows: ReadonlyArray<VariantRow>, connectionId: string) {
  const statusOf = (row: VariantRow) => row.platformStatuses?.find(status => status.connectionId === connectionId)
  return {
    total: rows.length,
    linked: rows.filter(row => statusOf(row)?.isMatched && !statusOf(row)?.isMissing).length,
    missing: rows.filter(row => statusOf(row)?.isMissing).length
  }
}

// The publish card's state from those counts: every variant present = live; some missing or not there = partly live
// (selectable, Yayınla sends the card again); none present and some missing = Bulunamadı (red, selectable).
export function publishCardVariantPresence(variantLinks?: { linked: number; total: number; missing?: number }) {
  const total = variantLinks?.total ?? 0
  const linked = variantLinks?.linked ?? 0
  const missing = variantLinks?.missing ?? 0
  return {
    allLinked: total > 0 && linked === total && missing === 0,
    someLinked: linked > 0 && linked < total,
    noneFound: missing > 0 && linked === 0
  }
}
