export type ProductPlatformDisplayState = 'active' | 'partial' | 'processing' | 'error' | 'inactive'

const inProgressStatuses = new Set([
  'QUEUED',
  'UPDATE_QUEUED',
  'BATCH_SUBMITTED',
  'BATCH_IN_PROGRESS',
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
  isChecking: boolean
): ProductPlatformDisplayState {
  const normalized = normalizedStatuses(statuses)
  if (isChecking || normalized.some(status => inProgressStatuses.has(status))) return 'processing'
  if (normalized.length > 0 && normalized.every(status => status === 'LIVE')) return 'active'
  if (normalized.some(status => status === 'LIVE' || status === 'PARTIAL_LIVE')) return 'partial'
  if (normalized.some(status => errorStatuses.has(status))) return 'error'

  // Variant links only prove that local and remote variant identifiers were
  // paired at some point. They do not prove that a marketplace listing exists.
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
  const coverage = variantCount > 0 ? ` (${matchedVariantCount}/${variantCount})` : ''

  if (state === 'processing') return `${platform} yayın durumu güncelleniyor`
  if (state === 'error') return `${platform} yayın işlemi başarısız veya incelemede`
  if (state === 'active') return `${platform} üzerinde tüm varyantlar yayında${coverage}`
  if (state === 'partial') return `${platform} üzerinde ürün kısmen yayında${coverage}`
  if (normalized.includes('ARCHIVED')) return `${platform} ilanı arşivlenmiş`

  if (matchedVariantCount > 0) {
    const publicationNote = normalized.includes('UNKNOWN')
      ? 'henüz yayınlanmadı'
      : 'yayın durumu doğrulanmadı'
    return `${matchedVariantCount}/${variantCount} varyant eşleşmesi var; ${publicationNote}`
  }

  return `${platform} ürün eşleşmesi bulunamadı`
}
