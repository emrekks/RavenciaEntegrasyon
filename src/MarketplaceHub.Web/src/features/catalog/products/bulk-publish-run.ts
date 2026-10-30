// What the bulk "Platformlarda Yayınla" popup sends and reports: the slices it posts, the cards with no KDV Oranı on
// their product card, and the notification that names every refused card with its reason.
import type { NotificationDetail } from '../../../shared/notifications'
import type { MarketplaceConnection, Product } from '../shared'
import { bulkPublishPlatformName } from './bulk-publish-status'

export type BulkPublicationFailure = { productId: string; productTitle: string; connectionId: string; code: string; message: string }
export type BulkPublicationResult = { queued: number; skipped: number; failed: BulkPublicationFailure[] }

type FamilyRecord = Pick<Product, 'id' | 'modelCode' | 'variants'>

function familyKey(record: FamilyRecord | undefined, id: string) {
  const modelCode = record?.modelCode?.trim() || record?.variants.map(variant => variant.modelCode?.trim()).find(Boolean)
  return modelCode ? `model:${modelCode.toLocaleUpperCase('tr-TR')}` : `product:${id}`
}

// The selection in slices of about `size` records. The colour cards of one model family stay in one slice: PTT AVM and
// Shopify send a family as one product, and the server counts a sibling it adds as selected only when it is in the
// same request (cut between two slices, the second slice read the family's queued cards as already in progress).
export function bulkPublishChunks(productIds: readonly string[], records: readonly FamilyRecord[], size: number): string[][] {
  const byId = new Map(records.map(record => [record.id, record]))
  const families = new Map<string, string[]>()
  for (const id of productIds) {
    const key = familyKey(byId.get(id), id)
    const family = families.get(key)
    if (family) family.push(id)
    else families.set(key, [id])
  }
  const chunks: string[][] = []
  let current: string[] = []
  for (const family of families.values()) {
    if (current.length && current.length + family.length > size) { chunks.push(current); current = [] }
    current.push(...family)
  }
  if (current.length) chunks.push(current)
  return chunks
}

// How many selected records have no KDV Oranı on their product card and no marketplace offer to take one from: the
// server refuses them (VAT_RATE_REQUIRED) unless the store has a default VAT. A record the list does not hold is not
// counted; its card is unknown here.
export function bulkPublishMissingVat(records: ReadonlyArray<Pick<Product, 'variants' | 'vatRate'> | null> | null) {
  let missing = 0
  for (const record of records ?? []) {
    if (record && record.vatRate == null && record.variants.length && record.variants.every(variant => !variant.offerId)) missing++
  }
  return missing
}

// One slice's request: the VAT is never sent, the server takes it from each product card.
export function bulkPublishRequestBody(productIds: readonly string[], connectionIds: readonly string[]) {
  return JSON.stringify({ productIds, connectionIds })
}

export function addBulkPublicationResult(total: BulkPublicationResult, part: BulkPublicationResult): BulkPublicationResult {
  return { queued: total.queued + part.queued, skipped: total.skipped + part.skipped, failed: [...total.failed, ...part.failed] }
}

export function bulkPublishResultMessage(result: BulkPublicationResult, interrupted = false) {
  const parts = [`${result.queued.toLocaleString('tr-TR')} yayın işi kuyruğa alındı`]
  if (result.skipped) parts.push(`${result.skipped.toLocaleString('tr-TR')} kayıt zaten yayında veya işlemde`)
  if (result.failed.length) parts.push(`${new Set(result.failed.map(item => item.productId)).size.toLocaleString('tr-TR')} kayıt ${result.failed.length.toLocaleString('tr-TR')} gönderimde reddedildi`)
  return `${parts.join(', ')}.${interrupted ? ' Gönderim yarıda kesildi; kalanlar gönderilmedi.' : ''}`
}

// One row per refused record and reason: the record (with its model code when the list holds it) and the stores that
// refused it for that reason. A reason shared by every store reads once, so six refusals of one card are one row.
export function bulkPublishFailureDetails(failed: readonly BulkPublicationFailure[], connections: ReadonlyArray<Pick<MarketplaceConnection, 'id' | 'platformCode' | 'displayName'>>, records: ReadonlyArray<Pick<Product, 'id' | 'title' | 'modelCode'>> = [], limit = 40): NotificationDetail[] {
  const connectionName = (id: string) => {
    const connection = connections.find(item => item.id === id)
    return connection ? connection.displayName?.trim() || bulkPublishPlatformName(connection.platformCode) : 'Bilinmeyen Mağaza'
  }
  const byId = new Map(records.map(record => [record.id, record]))
  const groups = new Map<string, { productId: string; title: string; message: string; stores: string[] }>()
  for (const failure of failed) {
    const key = `${failure.productId}\u0000${failure.message}`
    const group = groups.get(key)
    const store = connectionName(failure.connectionId)
    if (group) { if (!group.stores.includes(store)) group.stores.push(store) }
    else groups.set(key, { productId: failure.productId, title: failure.productTitle, message: failure.message, stores: [store] })
  }
  const rows = [...groups.values()].map(group => {
    const record = byId.get(group.productId)
    const title = group.title.trim() || record?.title.trim() || 'Ürün Kartı'
    const model = record?.modelCode?.trim()
    return { label: model && !title.includes(model) ? `${title} (${model})` : title, value: `${group.stores.join(', ')}: ${group.message}`, tone: 'error' as const }
  })
  if (rows.length <= limit) return rows
  return [...rows.slice(0, limit - 1), { label: 'Diğerleri', value: `${(rows.length - limit + 1).toLocaleString('tr-TR')} kayıt daha reddedildi.`, tone: 'error' as const }]
}
