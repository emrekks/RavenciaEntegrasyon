// missing: every variant once matched or published there is one the marketplace no longer knows (server NOT_FOUND).
export type ProductPlatformDisplayState = 'active' | 'linked' | 'partial' | 'processing' | 'error' | 'missing' | 'inactive'

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
  'APPROVAL_PARTIAL_PENDING',
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
  matchedVariantCount = 0
): ProductPlatformDisplayState {
  const normalized = normalizedStatuses(statuses)
  if (isChecking || normalized.some(status => inProgressStatuses.has(status))) return 'processing'
  const present = normalized.filter(status => !absentStatuses.has(status))
  if (present.length > 0 && present.every(status => status === 'NOT_FOUND')) return 'missing'
  if (normalized.length > 0 && normalized.every(status => status === 'LIVE')) return 'active'
  if (normalized.some(status => status === 'LIVE' || status === 'PARTIAL_LIVE')) return 'partial'
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
  const linkedCoverage = matchedVariantCount > 0 && variantCount > 0 ? ` (${matchedVariantCount}/${variantCount} varyant)` : ''

  if (state === 'processing') return `${platform} yayın durumu güncelleniyor`
  if (state === 'error') return `${platform} yayın işlemi başarısız veya incelemede`
  if (state === 'missing') return `${platform}: Pazaryerinde Bulunamadı`
  if (state === 'active') return `${platform} üzerinde tüm varyantlar yayında${coverage}`
  if (state === 'partial') return `${platform} üzerinde ürün kısmen yayında${coverage}`
  if (normalized.includes('ARCHIVED')) return `${platform} ilanı arşivlenmiş${coverage}`
  if (state === 'linked') return `${platform} bağlantısı var${linkedCoverage}`

  if (matchedVariantCount > 0) {
    const publicationNote = normalized.includes('UNKNOWN')
      ? 'henüz yayınlanmadı'
      : 'yayın durumu doğrulanmadı'
    return `${matchedVariantCount}/${variantCount} varyant eşleşmesi var; ${publicationNote}`
  }

  return `${platform} ürün eşleşmesi bulunamadı`
}
