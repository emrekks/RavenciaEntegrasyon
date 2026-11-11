import { isOffSale } from '../sale-state'
import type { VariantPlatformStatus } from '../shared'

type VariantRow = { platformStatuses?: VariantPlatformStatus[] | null }

// How the family's variants stand on one connection: present there (a variant link or a LIVE listing) and missing (the
// marketplace no longer knows them: link NOT_FOUND or REJECTED). A variant with neither stays out of both: no link is
// never read as missing. offSale: present variants the marketplace says are closed to sale (offSaleStockZero: those whose
// only named reason is a stock of 0; offSaleReasons: the other closures by their named reason, as the server's
// OffSaleReasonCounts); a variant whose sale state is unknown is in neither.
export function publishCardVariantLinks(rows: ReadonlyArray<VariantRow>, connectionId: string) {
  const statusOf = (row: VariantRow) => row.platformStatuses?.find(status => status.connectionId === connectionId)
  const present = rows.filter(row => statusOf(row)?.isMatched && !statusOf(row)?.isMissing)
  const closed = present.filter(row => isOffSale(statusOf(row)?.saleState))
  const offSaleReasons: Record<string, number> = {}
  for (const row of closed) {
    const reason = statusOf(row)?.saleStateReason?.trim().toUpperCase()
    if (reason && reason !== 'STOCK_ZERO') offSaleReasons[reason] = (offSaleReasons[reason] ?? 0) + 1
  }
  return {
    total: rows.length,
    linked: present.length,
    missing: rows.filter(row => statusOf(row)?.isMissing).length,
    offSale: closed.length,
    offSaleStockZero: closed.filter(row => statusOf(row)?.saleStateReason?.trim().toUpperCase() === 'STOCK_ZERO').length,
    offSaleReasons
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

type PlanRow = VariantRow & { color?: string | null; sku?: string }

/** One group of a card's variants not on the marketplace yet. existing-colour: new sizes of a colour the marketplace
 * holds (they join that colour's listing); new-colour: a colour it does not hold yet (Trendyol and Hepsiburada open it as
 * a new colour listing under the model code, a one-product marketplace adds it to the product). */
export type PublishVariantGroup = { kind: 'existing-colour' | 'new-colour'; colour: string | null; variants: number; skus: string[] }

export type PublishCardPlan =
  | { send: false; reason: 'matched' }
  | { send: false; reason: 'colour-mismatch'; message: string }
  /** variants: how many the job carries; addsToListing: the card is on the marketplace and only its new variants go;
   * liveVariants: those already there (left as they are); groups: the variants that go, by colour. */
  | { send: true; variants: number; addsToListing: boolean; hasMissingVariant: boolean; liveVariants: number; groups: PublishVariantGroup[] }

/** colourCardsInFamily: the model is split into several panel cards (one per colour), so a new colour belongs on its own
 * card; familyIsOneProduct: the marketplace holds the whole family as one product with variants (Shopify, n11,
 * Pazarama, PttAVM), so a new colour is a new variant of it. */
export type PublishCardPlanOptions = { colourCardsInFamily?: boolean; familyIsOneProduct?: boolean }

const colourKey = (colour: string | null | undefined) => colour?.trim().toLocaleLowerCase('tr-TR') ?? ''

// What Yayınla does with one colour card on one marketplace (owner, 11.11.2026: "ayrımları yapabilsin"). Every variant
// is sorted: already there = left as it is; a new size of a colour the marketplace holds = added to that colour's
// listing; a colour it does not hold yet = opened as a new colour listing under the same model code (Trendyol: its own
// content, its own pictures with the size chart last and its own colour attribute). A first send goes whole.
// Live 2026-11-05 (MZ021Y-CD): a card with any matched variant was skipped whole, so 21 new variants never went.
// Live 2026-11-11 (RYP006): three Mürdüm sizes beside six live colours on one panel card were refused as "another
// colour"; on one panel card that holds several colours they are a new colour of the model and go.
// Only a family split into one panel card per colour still refuses a variant of another colour saved into a matched
// card (MZ021Y-CD, 5.11.2026: Bordo, Haki … sizes in the Mavi card): it belongs on its own colour card, never on this one.
// A new-colour variant without pictures of its own is not refused here: the server leaves it out with its reason and
// sends the others (PublicationUnsentVariants.NewColourVariantsWithoutPictures).
export function publishCardPlan(rows: ReadonlyArray<PlanRow>, connectionId: string, options: PublishCardPlanOptions = {}): PublishCardPlan {
  const links = publishCardVariantLinks(rows, connectionId)
  const unsentCount = links.total - links.linked
  if (links.linked > 0 && unsentCount === 0) return { send: false, reason: 'matched' }
  if (links.linked === 0) return { send: true, variants: links.total, addsToListing: false, hasMissingVariant: links.missing > 0, liveVariants: 0, groups: [] }
  const isPresent = (row: PlanRow) => {
    const status = row.platformStatuses?.find(item => item.connectionId === connectionId)
    return Boolean(status?.isMatched && !status.isMissing)
  }
  const cardColours = new Set(rows.filter(isPresent).map(row => colourKey(row.color)).filter(Boolean))
  const groups = new Map<string, PublishVariantGroup>()
  for (const row of rows.filter(row => !isPresent(row))) {
    const key = colourKey(row.color)
    const kind = key && cardColours.size > 0 && !cardColours.has(key) ? 'new-colour' : 'existing-colour'
    const group = groups.get(`${kind}:${key}`) ?? { kind, colour: row.color?.trim() || null, variants: 0, skus: [] }
    group.variants++
    if (row.sku?.trim()) group.skus.push(row.sku.trim())
    groups.set(`${kind}:${key}`, group)
  }
  const ordered = [...groups.values()].sort((a, b) => (a.kind === b.kind ? 0 : a.kind === 'existing-colour' ? -1 : 1))
  const newColours = ordered.filter(group => group.kind === 'new-colour')
  if (newColours.length > 0 && options.colourCardsInFamily && !options.familyIsOneProduct) {
    const cardColour = [...new Set(rows.filter(isPresent).map(row => row.color?.trim()).filter(Boolean))].join(', ')
    const strays = newColours.reduce((sum, group) => sum + group.variants, 0)
    return {
      send: false,
      reason: 'colour-mismatch',
      message: `Kart pazaryerinde ${cardColour} rengiyle eşleşmiş; eklenen ${unsentCount} varyanttan ${strays} tanesi başka renkte (${newColours.map(group => group.colour).join(', ')}). Bu kartla gönderilirlerse bu kartın adı ve görselleriyle açılırlar; kart gönderilmedi. Bu varyantları kendi renk kartlarına ekleyin.`
    }
  }
  return { send: true, variants: unsentCount, addsToListing: true, hasMissingVariant: links.missing > 0, liveVariants: links.linked, groups: ordered }
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
        ? { ...status, isLinked: fresh.isLinked, isMatched: fresh.isMatched, isMissing: fresh.isMissing, status: fresh.status, saleState: fresh.saleState, saleStateReason: fresh.saleStateReason }
        : { ...status, isLinked: false, isMatched: false, isMissing: false, saleState: null, saleStateReason: null }
    })
    const added = latest.filter(item => !own.some(status => status.connectionId === item.connectionId))
    return { ...row, platformStatuses: [...merged, ...added] }
  })
}
