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

type PlanRow = VariantRow & { color?: string | null }

export type PublishCardPlan =
  | { send: false; reason: 'matched' }
  | { send: false; reason: 'colour-mismatch'; message: string }
  /** variants: how many the job carries; addsToListing: the card is on the marketplace and only its new variants go. */
  | { send: true; variants: number; addsToListing: boolean; hasMissingVariant: boolean }

const colourKey = (colour: string | null | undefined) => colour?.trim().toLocaleLowerCase('tr-TR') ?? ''

// What Yayınla does with one colour card on one marketplace. A card whose every variant is there is left as it is; a
// card partly there sends only the variants not there yet (a size added since, or one the marketplace lost), which the
// server adds to the listing under the card's model code. Live 2026-11-05 (MZ021Y-CD): a card with any matched variant
// was skipped whole, so 21 new variants never went. A variant of another colour saved into a matched card is not sent
// with it: it would go out under this card's title and images (on Trendyol as a new colour content).
export function publishCardPlan(rows: ReadonlyArray<PlanRow>, connectionId: string): PublishCardPlan {
  const links = publishCardVariantLinks(rows, connectionId)
  const unsentCount = links.total - links.linked
  if (links.linked > 0 && unsentCount === 0) return { send: false, reason: 'matched' }
  if (links.linked === 0) return { send: true, variants: links.total, addsToListing: false, hasMissingVariant: links.missing > 0 }
  const isPresent = (row: PlanRow) => {
    const status = row.platformStatuses?.find(item => item.connectionId === connectionId)
    return Boolean(status?.isMatched && !status.isMissing)
  }
  const cardColours = new Set(rows.filter(isPresent).map(row => colourKey(row.color)).filter(Boolean))
  const otherColours = [...new Map(rows
    .filter(row => !isPresent(row) && colourKey(row.color) && !cardColours.has(colourKey(row.color)))
    .map(row => [colourKey(row.color), row.color!.trim()] as const)).values()]
  if (cardColours.size > 0 && otherColours.length > 0) {
    const cardColour = [...new Set(rows.filter(isPresent).map(row => row.color?.trim()).filter(Boolean))].join(', ')
    const strays = rows.filter(row => !isPresent(row) && otherColours.some(colour => colourKey(colour) === colourKey(row.color))).length
    return {
      send: false,
      reason: 'colour-mismatch',
      message: `Kart pazaryerinde ${cardColour} rengiyle eşleşmiş; eklenen ${unsentCount} varyanttan ${strays} tanesi başka renkte (${otherColours.join(', ')}). Bu kartla gönderilirlerse bu kartın adı ve görselleriyle açılırlar; kart gönderilmedi. Bu varyantları kendi renk kartlarına ekleyin.`
    }
  }
  return { send: true, variants: unsentCount, addsToListing: true, hasMissingVariant: links.missing > 0 }
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
