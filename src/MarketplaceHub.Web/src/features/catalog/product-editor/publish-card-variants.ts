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

type LinkedRow = { key: string; platformStatuses?: VariantPlatformStatus[] | null }
// The form's rows keep the platform statuses they were filled with, and a status check or a store lookup changes only
// a variant's link (not its version, stock or offers), so the form is not filled again for it. The card and Yayınla read
// the links from the latest product instead: each row takes its saved variant's link fields; offers stay the row's own.
// A variant the latest product does not carry (a new row, or a product read without statuses) keeps what it has.
export function withLatestVariantLinks<R extends LinkedRow>(rows: ReadonlyArray<R>, latestVariants: ReadonlyArray<{ id: string; platformStatuses?: VariantPlatformStatus[] | null }> | null | undefined): R[] {
  const latestById = new Map((latestVariants ?? []).flatMap(variant => variant.platformStatuses ? [[variant.id, variant.platformStatuses] as const] : []))
  return rows.map(row => {
    const latest = latestById.get(row.key)
    if (!latest) return row
    const own = row.platformStatuses ?? []
    const merged = own.map(status => {
      const fresh = latest.find(item => item.connectionId === status.connectionId)
      return fresh
        ? { ...status, isLinked: fresh.isLinked, isMatched: fresh.isMatched, isMissing: fresh.isMissing, status: fresh.status }
        : { ...status, isLinked: false, isMatched: false, isMissing: false }
    })
    const added = latest.filter(item => !own.some(status => status.connectionId === item.connectionId))
    return { ...row, platformStatuses: [...merged, ...added] }
  })
}
