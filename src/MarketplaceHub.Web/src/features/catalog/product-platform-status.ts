import { offSaleSummary } from './sale-state'

// missing: every variant once matched or published there is one the marketplace no longer knows (server NOT_FOUND).
// closed: listed there, but archived or every matched variant is closed to sale beyond a stock of 0 (Satışa Kapalı).
// blocked: some or all matched variants are blacklisted or locked there (red).
export type ProductPlatformDisplayState = 'active' | 'linked' | 'partial' | 'processing' | 'error' | 'missing' | 'inactive' | 'closed' | 'blocked'

// A record with nothing there (no match, no publication) says nothing about the others: a family whose published cards
// are all gone and whose other cards were never sent reads "Bulunamadı", not "kısmen".
const absentStatuses = new Set(['UNLINKED', 'UNKNOWN'])

const inProgressStatuses = new Set([
  'QUEUED',
  'UPDATE_QUEUED',
  'BATCH_SUBMITTED',
  'BATCH_IN_PROGRESS',
  'IMPORT_SUBMITTED',
  'IMPORT_IN_PROGRESS',
  'CREATE_ACCEPTED',
  // Sent and waiting for the marketplace's word (the create batch had no line for the variant; Shopify's submit).
  'CREATE_RESULT_UNKNOWN',
  'SHOPIFY_SUBMITTED',
  'UPDATE_SUBMITTED',
  'UPDATE_UNAPPROVED_SUBMITTED',
  'UPDATE_CONTENT_SUBMITTED',
  'UPDATE_VARIANTS_SUBMITTED',
  'UPDATE_DELIVERY_SUBMITTED',
  'UPDATE_IN_PROGRESS',
  'APPROVAL_PENDING',
  'ARCHIVE_QUEUED',
  'ARCHIVE_ACCEPTED',
  'ARCHIVE_BATCH_SUBMITTED',
  'ARCHIVE_RECONCILING',
  'ARCHIVE_PARTIAL_PENDING',
  'UNARCHIVE_QUEUED',
  'UNARCHIVE_ACCEPTED'
])

// A card whose product update is on its way: queued, a phase sent and waiting for the batch result, or between phases
// (the server's ProductListingHealth.UpdateInFlightStatuses). Only the update job writes these on the card.
const updateInFlightStatuses = new Set(['UPDATE_QUEUED', 'UPDATE_SUBMITTED', 'UPDATE_UNAPPROVED_SUBMITTED', 'UPDATE_CONTENT_SUBMITTED', 'UPDATE_VARIANTS_SUBMITTED', 'UPDATE_DELIVERY_SUBMITTED', 'UPDATE_IN_PROGRESS'])
export const isProductUpdateInFlight = (status: string | null | undefined) => updateInFlightStatuses.has(status?.trim().toUpperCase() ?? '')

const errorStatuses = new Set([
  'REJECTED',
  'CREATE_REJECTED',
  'UPDATE_REJECTED',
  'PARTIAL_REJECTED',
  'PARTIAL_FAILURE',
  'UPDATE_PARTIAL_FAILURE',
  'ARCHIVE_REJECTED',
  'ARCHIVE_PARTIAL_FAILURE',
  'UPDATE_BLOCKED',
  'BLOCKED',
  'MANUAL_REVIEW',
  'LOCKED',
  'BLACKLISTED'
])

const isMarketplaceRefusal = (status: string) => status === 'REJECTED' || status === 'PARTIAL_REJECTED'

function normalizedStatuses(statuses: readonly string[]) {
  return statuses.map(status => status.trim().toUpperCase()).filter(Boolean)
}

export function productPlatformDisplayState(
  statuses: readonly string[],
  isChecking: boolean,
  matchedVariantCount = 0,
  variantCount = 0
): ProductPlatformDisplayState {
  const normalized = normalizedStatuses(statuses)
  // A card or variant the marketplace refused (REJECTED, PARTIAL_REJECTED) makes the row red: the refusal wins over
  // waiting, live and linked ones (owner, 14 Nov 2026: MZ056, one rejected colour card of ten read blue).
  if (normalized.some(isMarketplaceRefusal)) return 'error'
  if (isChecking || normalized.some(status => inProgressStatuses.has(status))) return 'processing'
  const present = normalized.filter(status => !absentStatuses.has(status))
  if (present.length > 0 && present.every(status => status === 'NOT_FOUND')) return 'missing'
  // A listing archived on the marketplace is there but not sold: never green.
  if (present.length > 0 && present.every(status => status === 'ARCHIVED')) return 'closed'
  // Green only when every variant of the row is there: a LIVE card counts a variant once it is linked or its own
  // listing is LIVE, so one it never sent (no listing, no link) is known not to be live and the row is partly live
  // (live 2026-11-10: RYP006 on Trendyol and Shopify, Mürdüm added after the send, 18 of 21). Without counts the
  // card's own word stands.
  const everyVariantThere = variantCount <= 0 || matchedVariantCount >= variantCount
  if (normalized.length > 0 && normalized.every(status => status === 'LIVE')) return everyVariantThere ? 'active' : 'partial'
  // Colour cards published from the panel (LIVE) beside ones imported and matched (LINKED), every variant there: the
  // product is live, not partly (live 2026-11-06: MZ028IH on Trendyol, 1 LIVE + 9 LINKED, 10/10).
  if (variantCount > 0 && matchedVariantCount >= variantCount && normalized.includes('LIVE') && normalized.every(status => status === 'LIVE' || status === 'LINKED')) return 'active'
  // Some variants approved and live, the rest still waiting for the marketplace's approval (live 2026-10-30:
  // MZ054YNC on Pazarama, 12 of 24 LIVE) is partly live, not "being processed".
  if (normalized.some(status => status === 'LIVE' || status === 'PARTIAL_LIVE' || status === 'APPROVAL_PARTIAL_PENDING')) return 'partial'
  // Some variants failed and the rest did not: partly working, not a whole failure.
  if (normalized.some(status => errorStatuses.has(status))) return normalized.every(status => errorStatuses.has(status)) ? 'error' : 'partial'
  // Some cards are gone and the others are still matched: partly there.
  if (normalized.includes('NOT_FOUND')) return 'partial'

  // A catalog import/mapping confirms that the local variants are connected
  // to marketplace variants; publication state remains a separate status.
  if (matchedVariantCount > 0 || normalized.some(status => status === 'LINKED' || status === 'PARTIAL_LINKED' || status === 'MAPPED')) return 'linked'

  return 'inactive'
}

export function productPlatformDisplayLabel(
  platform: string,
  statuses: readonly string[],
  matchedVariantCount: number,
  variantCount: number,
  state: ProductPlatformDisplayState,
  rejectionReason?: string | null
) {
  const normalized = normalizedStatuses(statuses)
  const coverage = matchedVariantCount > 0 && variantCount > 0 ? ` (${matchedVariantCount}/${variantCount})` : ''
  const linkedCoverage = matchedVariantCount > 0 && variantCount > 0 ? ` (${matchedVariantCount}/${variantCount} Varyant)` : ''

  // The marketplace's own approval word (Trendyol's unapproved list, the panel's approval check): waiting or refused,
  // with the reasons it gave when it gave any (owner, 14 Nov 2026). No reason is made up.
  const reason = rejectionReason?.trim() ? ` · ${rejectionReason.trim()}` : ''
  const every = (...values: string[]) => normalized.length > 0 && normalized.every(status => values.includes(status))
  if (state === 'processing' && every('APPROVAL_PENDING')) return `${platform}: Onay Bekliyor`
  if (state === 'error' && every('REJECTED')) return `${platform}: Reddedildi${reason}`
  if (state === 'error' && normalized.some(isMarketplaceRefusal)) return `${platform}: Bazı Varyantlar Reddedildi${reason}`

  if (state === 'processing') return `${platform}: Yayın Durumu Güncelleniyor`
  if (state === 'error') return `${platform}: Yayın İşlemi Başarısız veya İncelemede`
  if (state === 'missing') return `${platform}: Pazaryerinde Bulunamadı`
  if (state === 'closed') return `${platform}: Satışa Kapalı · Arşivde`
  if (state === 'active') return `${platform}: Ürün Yayında${coverage}`
  if (state === 'partial' && normalized.includes('APPROVAL_PARTIAL_PENDING')) return `${platform}: Kısmen Yayında · Kısmi Onay Bekliyor${coverage}`
  if (state === 'partial') return `${platform}: Kısmen Yayında${coverage}`
  if (state === 'linked') return `${platform} Bağlantısı Var${linkedCoverage}`

  if (matchedVariantCount > 0) {
    const publicationNote = normalized.includes('UNKNOWN')
      ? 'Henüz Yayınlanmadı'
      : 'Yayın Durumu Doğrulanmadı'
    return `${platform}: ${matchedVariantCount}/${variantCount} Varyant Eşleşmesi Var · ${publicationNote}`
  }

  return `${platform}: Ürün Eşleşmesi Bulunamadı`
}

// What the server counted for one marketplace's matched variants (ProductPlatformStatusView).
export type ProductPlatformSaleCounts = {
  matched: number
  variantCount: number
  onSale: number
  offSale: number
  offSaleStockZero: number
  reasons?: Readonly<Record<string, number>> | null
}

// The sale state over the publication state (owner, 6 Nov 2026, every marketplace). A stock of 0 is never a closure:
// those variants read as on sale. Every matched variant closed for another reason reads "Satışa Kapalı · {reason}"
// (orange); some closed read "Kısmen Satışta · x/n Varyant Satışa Kapalı ({reason})" (yellow); any blacklisted or
// locked one makes it red. An imported (LINKED) card whose every variant is matched and has a known sale state is
// "Ürün Yayında"; with any unknown it keeps "Bağlantısı Var". An unknown sale state adds no sale words. A card being
// processed, failed or gone keeps its own words.
export function withProductPlatformSaleState(
  platform: string,
  state: ProductPlatformDisplayState,
  label: string,
  counts: ProductPlatformSaleCounts | null
): { state: ProductPlatformDisplayState; label: string } {
  if (!counts || !['active', 'linked', 'partial'].includes(state)) return { state, label }
  const { matched, variantCount } = counts
  const coverage = matched > 0 && variantCount > 0 ? ` (${matched}/${variantCount})` : ''
  const allKnown = matched > 0 && counts.onSale + counts.offSale >= matched
  if (state === 'linked' && variantCount > 0 && matched >= variantCount && allKnown) {
    state = 'active'
    label = `${platform}: Ürün Yayında${coverage}`
  }
  const sale = offSaleSummary(matched, counts.offSale, counts.offSaleStockZero, counts.reasons)
  if (!sale) return { state, label }
  if (sale.allClosed) return { state: sale.blocking ? 'blocked' : 'closed', label: `${platform}: ${sale.label}` }
  const partialLabel = state === 'active' ? `${platform}: Kısmen Satışta · ${sale.label}` : `${label} · ${sale.label}`
  return { state: sale.blocking ? 'blocked' : 'partial', label: partialLabel }
}

// What the server counted for one marketplace across a model group's cards (ProductPlatformStatusView).
export type ProductPlatformBreakdownCounts = {
  variantCount: number
  matched: number
  missing: number
  onSale: number
  offSale: number
  offSaleStockZero: number
  rejected: number
  pending: number
  // While a card's update is on its way (server ResubmittedVariantCount / UpdatingVariantCount): refused variants sent
  // again, not in rejected; and matched ones the update goes out for, also in matched.
  resubmitted?: number
  updating?: number
  // Linked variants whose listing waits for a person (server ReviewVariantCount, MANUAL_REVIEW): not in matched.
  review?: number
}

// The dot follows the same counts as the tooltip (owner, 15 Nov 2026: MZ056 read green while 6 variants waited for
// Trendyol and the other cards were being updated). A variant still refused makes it red; a refused variant sent again
// or a card being updated makes it "processing" (blue) until the marketplace answers; green stays for a family whose
// every variant is live with nothing on its way.
export function withProductPlatformWork(state: ProductPlatformDisplayState, counts: Pick<ProductPlatformBreakdownCounts, 'rejected' | 'resubmitted' | 'updating' | 'review'>): ProductPlatformDisplayState {
  if ((counts.rejected > 0 || (counts.review ?? 0) > 0) && state !== 'missing' && state !== 'blocked') return 'error'
  if (state === 'error' || state === 'missing' || state === 'blocked') return state
  if ((counts.resubmitted ?? 0) > 0 || (counts.updating ?? 0) > 0) return 'processing'
  return state
}

// The list mark's tooltip (owner, 14 Nov 2026: "kaçı aktif kaçı pasifte"): the group's variants on one marketplace by
// state, only the states that hold any. Aktif: matched and on sale (a stock of 0 is never a closure); a matched variant
// whose sale state the marketplace has not said is counted as matched with its sale state unknown, never as active.
// Pasif: neither matched, lost, refused nor waiting there (the marketplace's catalog read did not name it: owner, 14 Nov
// 2026, MZ059YNC-03 on n11, refused there on 28 Oct and absent from its read). Nothing is inferred beyond the counts.
export function productPlatformBreakdownLines(counts: ProductPlatformBreakdownCounts, rejectionReason?: string | null): string[] {
  const active = counts.onSale + counts.offSaleStockZero
  const closed = Math.max(0, counts.offSale - counts.offSaleStockZero)
  const unknownSale = unknownSaleCount(counts)
  const resubmitted = counts.resubmitted ?? 0
  const review = counts.review ?? 0
  const passive = Math.max(0, counts.variantCount - counts.matched - counts.missing - counts.rejected - counts.pending - resubmitted - review)
  const reason = rejectionReason?.trim() ? ` · ${rejectionReason.trim()}` : ''
  const lines: Array<[number, string]> = [
    [active, 'Aktif'],
    [unknownSale, 'Eşleşti · Satış Durumu Bilinmiyor'],
    [closed, 'Satışa Kapalı'],
    // Live there, with the panel's update sent and not yet answered: part of the matched ones, not another state.
    [counts.updating ?? 0, 'Yayında · Güncelleniyor'],
    [resubmitted, 'Onaya Gönderildi (Yeniden)'],
    [counts.pending, 'Onay Bekliyor'],
    [counts.rejected, `Reddedildi${reason}`],
    [review, 'İncelemede · Sonuç Kesinleşmedi'],
    [counts.missing, 'Bulunamadı'],
    [passive, 'Pasif · Eşleşme Yok']
  ]
  return lines.filter(([count]) => count > 0).map(([count, text]) => `${count} ${text}`)
}

const unknownSaleCount = (counts: ProductPlatformBreakdownCounts) => Math.max(0, counts.matched - counts.onSale - counts.offSale)

export function productPlatformBreakdownTip(platform: string, counts: ProductPlatformBreakdownCounts, rejectionReason?: string | null): string | null {
  if (counts.variantCount <= 0) return null
  // Owner, 14 Nov 2026: the head reads "27/33 varyant aktif"; only the variants that are not active get a line.
  // While the sale state of some matched variants is unknown, an active count would read as "none are active" (n11
  // MZ050YNC: "0/24 varyant aktif" though all 24 are matched and n11 had not said): the head then counts the matched
  // ones, and every line (Aktif too) follows.
  if (unknownSaleCount(counts) > 0)
    return [`${platform} · ${counts.matched}/${counts.variantCount} varyant eşleşti`, ...productPlatformBreakdownLines(counts, rejectionReason)].join('\n')
  const active = counts.onSale + counts.offSaleStockZero
  const rest = productPlatformBreakdownLines(counts, rejectionReason).filter(line => !/^\d+ Aktif$/.test(line))
  return [`${platform} · ${active}/${counts.variantCount} varyant aktif`, ...rest].join('\n')
}
