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
  'UPDATE_SUBMITTED',
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
  if (isChecking || normalized.some(status => inProgressStatuses.has(status))) return 'processing'
  const present = normalized.filter(status => !absentStatuses.has(status))
  if (present.length > 0 && present.every(status => status === 'NOT_FOUND')) return 'missing'
  // A listing archived on the marketplace is there but not sold: never green.
  if (present.length > 0 && present.every(status => status === 'ARCHIVED')) return 'closed'
  if (normalized.length > 0 && normalized.every(status => status === 'LIVE')) return 'active'
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
  state: ProductPlatformDisplayState
) {
  const normalized = normalizedStatuses(statuses)
  const coverage = matchedVariantCount > 0 && variantCount > 0 ? ` (${matchedVariantCount}/${variantCount})` : ''
  const linkedCoverage = matchedVariantCount > 0 && variantCount > 0 ? ` (${matchedVariantCount}/${variantCount} Varyant)` : ''

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
