import { mappingPlatformDefinitions } from '../../marketplace/mapping-platforms'
import { productPlatformDisplayState } from '../product-platform-status'
import type { MarketplaceConnection, Product, ProductPlatformStatus } from '../shared'

// Where one catalog record stands on one connection, read from the list's product.platformStatuses. A record without
// a matching entry, or with an UNKNOWN status, stays unknown: a missing field is never read as "not published".
// notFound: every variant once matched or published there is one the marketplace no longer knows (server NOT_FOUND).
export type BulkPublishStatusBucket = 'live' | 'partial' | 'processing' | 'error' | 'notFound' | 'linked' | 'archived' | 'notPublished' | 'other' | 'unknown'
export type BulkPublishStatusTone = 'success' | 'info' | 'warning' | 'danger' | 'neutral'
export type BulkPublishStatusSummary = {
  total: number
  counts: Record<BulkPublishStatusBucket, number>
  label: string
  detail: string | null
  tone: BulkPublishStatusTone
  // Every record is live there: the platform card cannot be selected (the server would skip them all anyway).
  allLive: boolean
}

const bucketOrder: BulkPublishStatusBucket[] = ['live', 'partial', 'processing', 'error', 'notFound', 'linked', 'archived', 'notPublished', 'other', 'unknown']
const bucketCountLabels: Record<BulkPublishStatusBucket, string> = {
  live: 'yayında',
  partial: 'kısmen yayında',
  processing: 'işlemde',
  error: 'hatalı veya incelemede',
  notFound: 'pazaryerinde bulunamadı',
  linked: 'eşleşmiş, yayını doğrulanmadı',
  archived: 'arşivde',
  notPublished: 'yayında değil',
  other: 'başka durumda',
  unknown: 'durumu bilinmiyor'
}
const singleBucketLabels: Record<BulkPublishStatusBucket, string> = {
  live: 'Yayında',
  partial: 'Kısmen yayında',
  processing: 'İşlemde',
  error: 'Hatalı veya incelemede',
  notFound: 'Bulunamadı',
  linked: 'Eşleşmiş, yayını doğrulanmadı',
  archived: 'Arşivde',
  notPublished: 'Yayında değil',
  other: 'Başka durumda',
  unknown: 'Durum bilinmiyor'
}
const singleBucketTones: Record<BulkPublishStatusBucket, BulkPublishStatusTone> = {
  live: 'success',
  partial: 'info',
  processing: 'info',
  error: 'danger',
  notFound: 'danger',
  linked: 'info',
  archived: 'neutral',
  notPublished: 'neutral',
  other: 'neutral',
  unknown: 'neutral'
}

const normalizeCode = (code: string) => code.trim().toUpperCase()
const normalizeName = (name: string) => name.trim().toLocaleLowerCase('tr-TR')

// The list's product statuses carry the connection's display name and platform code, not its id. One entry with both
// matching is this connection; none or several (two stores with the same name) leave the record unknown.
export function productConnectionStatus(product: Pick<Product, 'platformStatuses'>, connection: Pick<MarketplaceConnection, 'platformCode' | 'displayName'>): ProductPlatformStatus | null {
  const entries = product.platformStatuses
  if (!entries?.length) return null
  const code = normalizeCode(connection.platformCode)
  const name = normalizeName(connection.displayName)
  const matches = entries.filter(entry => normalizeName(entry.platform ?? '') === name && (!entry.platformCode || normalizeCode(entry.platformCode) === code))
  return matches.length === 1 ? matches[0] : null
}

export function bulkPublishStatusBucket(entry: ProductPlatformStatus | null): BulkPublishStatusBucket {
  if (!entry) return 'unknown'
  if (entry.isChecking) return 'processing'
  const status = entry.status?.trim().toUpperCase() ?? ''
  const matched = entry.matchedVariantCount ?? 0
  if (!status || status === 'UNKNOWN') return matched > 0 ? 'linked' : 'unknown'
  // The server writes UNLINKED only when it found no publication record and no matched variant for this connection.
  if (status === 'UNLINKED') return 'notPublished'
  if (status === 'ARCHIVED') return 'archived'
  const state = productPlatformDisplayState([status], false, matched)
  if (state === 'active') return 'live'
  if (state === 'partial') return 'partial'
  if (state === 'processing') return 'processing'
  if (state === 'error') return 'error'
  if (state === 'missing') return 'notFound'
  if (state === 'linked') return 'linked'
  return 'other'
}

// The "Yayın Durumu" of one platform card in the bulk publish popup: the selected records' standing on that connection.
// A null record (selected, but not loaded in the list) counts as unknown.
export function summarizeBulkPublishStatus(products: ReadonlyArray<Pick<Product, 'platformStatuses'> | null>, connection: Pick<MarketplaceConnection, 'platformCode' | 'displayName'>): BulkPublishStatusSummary {
  const counts = Object.fromEntries(bucketOrder.map(bucket => [bucket, 0])) as Record<BulkPublishStatusBucket, number>
  for (const product of products) counts[bulkPublishStatusBucket(product ? productConnectionStatus(product, connection) : null)] += 1
  const total = products.length
  const present = bucketOrder.filter(bucket => counts[bucket] > 0)
  if (!total) return { total, counts, label: 'Durum bilinmiyor', detail: null, tone: 'neutral', allLive: false }

  const format = (value: number) => value.toLocaleString('tr-TR')
  if (present.length === 1) {
    const [bucket] = present
    const label = total > 1 && bucket === 'live' ? `${format(total)}/${format(total)} Kayıt Yayında` : singleBucketLabels[bucket]
    return { total, counts, label, detail: null, tone: singleBucketTones[bucket], allLive: bucket === 'live' }
  }

  const detail = present.map(bucket => `${format(counts[bucket])} ${bucketCountLabels[bucket]}`).join(' · ')
  const tone: BulkPublishStatusTone = counts.error + counts.notFound > 0 ? 'warning' : counts.live + counts.partial + counts.processing + counts.linked > 0 ? 'info' : 'neutral'
  return { total, counts, label: `${format(counts.live)}/${format(total)} Kayıt Yayında`, detail, tone, allLive: false }
}

// The pill's hover list: per selected model code (product family), how many of its catalog records are live on this
// connection. Unknown records stay unknown, apart from the missing ones; incomplete models come first.
export type BulkPublishModelRow = { modelCode: string; live: number; total: number; missing: number; unknown: number }
export type BulkPublishModelTooltip = { rows: Array<BulkPublishModelRow & { text: string; complete: boolean }>; moreCount: number }

type ModelRecord = Pick<Product, 'platformStatuses' | 'modelCode' | 'title'> & { variants?: ReadonlyArray<{ modelCode?: string | null }> }

function recordModelCode(product: ModelRecord | null) {
  if (!product) return 'Yüklenmemiş kayıt'
  return product.modelCode?.trim() || product.variants?.map(variant => variant.modelCode?.trim()).find(Boolean) || product.title.trim() || 'Model kodu yok'
}

export function summarizeBulkPublishModels(products: ReadonlyArray<ModelRecord | null>, connection: Pick<MarketplaceConnection, 'platformCode' | 'displayName'>): BulkPublishModelRow[] {
  const rows = new Map<string, BulkPublishModelRow>()
  for (const product of products) {
    const modelCode = recordModelCode(product)
    const key = modelCode.toLocaleUpperCase('tr-TR')
    const row = rows.get(key) ?? { modelCode, live: 0, total: 0, missing: 0, unknown: 0 }
    const bucket = bulkPublishStatusBucket(product ? productConnectionStatus(product, connection) : null)
    row.total += 1
    if (bucket === 'live') row.live += 1
    else if (bucket === 'unknown') row.unknown += 1
    else row.missing += 1
    rows.set(key, row)
  }
  return [...rows.values()].sort((left, right) => (right.missing - left.missing) || (right.unknown - left.unknown) || left.modelCode.localeCompare(right.modelCode, 'tr'))
}

export function bulkPublishModelTooltip(rows: readonly BulkPublishModelRow[], limit = 8): BulkPublishModelTooltip {
  const format = (value: number) => value.toLocaleString('tr-TR')
  const shown = rows.slice(0, limit)
  return {
    rows: shown.map(row => {
      const complete = row.live === row.total
      const parts = [`${format(row.live)}/${format(row.total)} kayıt`]
      if (complete) parts.push('Tam')
      if (row.missing) parts.push(`${format(row.missing)} eksik`)
      if (row.unknown) parts.push(`${format(row.unknown)} bilinmiyor`)
      return { ...row, complete, text: parts.join(' · ') }
    }),
    moreCount: rows.length - shown.length
  }
}

// Platform codes are stored upper-case; show the brand names the way they are written.
export function bulkPublishPlatformName(code: string) {
  const normalized = normalizeCode(code)
  const known = mappingPlatformDefinitions.find(item => item.code === normalized)?.label
  if (known) return known
  if (normalized === 'TRENDYOL_EFATURAM') return 'E-Faturam'
  return code.charAt(0).toLocaleUpperCase('tr-TR') + code.slice(1).toLocaleLowerCase('tr-TR')
}
