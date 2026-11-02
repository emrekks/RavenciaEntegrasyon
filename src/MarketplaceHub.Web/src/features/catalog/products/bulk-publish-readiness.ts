import { useCallback, useEffect, useState } from 'react'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../../shared/api'
import { fixableValueMappings, type PublicationAttributeIssues, type PublicationValueMappingFix } from '../publication-attribute-readiness'
import { isPublicationLive, isPublicationStatusPending } from '../publication-status'
import { connectionCapabilitiesQueryKey, loadConnectionCapabilities, loadPublicationAttributeIssues, missingPublicationChecks, publicationAttributeSelectionsFor, publicationBlockingIssues, publicationProductChecks, publicationReadinessDraftFromProduct, publicationReadinessQueryKey, shopifyCapabilityIssues, sortRequirements, type CategoryRequirement, type ConnectionCapability } from '../product-editor/publication-readiness'
import type { Category, MarketplaceConnection, Product } from '../shared'
import { bulkPublishChunks } from './bulk-publish-run'
import { bulkPublishStatusBucket, productConnectionStatus } from './bulk-publish-status'

// The bulk publish popup runs the editor's publish checks on every selected record, a few at a time, so a card says
// what the editor's card would say for each product: the same gaps, the same value mappings to complete.

export const bulkReadinessConcurrency = 6

export type BulkReadinessIssue = { attribute: string; detail: string }
export type BulkProductReadiness = {
  productId: string
  title: string
  // Already on the marketplace (live, matched or in progress): the server skips it, so it is not checked.
  skipped: boolean
  // The list had no status for it here and the publication status says it is not out there (the editor's "Henüz
  // yayınlanmadı"): the card counts it as not published instead of unknown.
  notPublished?: boolean
  issues: BulkReadinessIssue[]
  // Optional attributes the store would get without a value: they do not block, the card lists them as warnings.
  warnings?: Array<BulkReadinessIssue & { fixable: boolean }>
  fixes: PublicationValueMappingFix[]
  categoryScope?: string
  // The panel category, for "Kategori Eşle" when the store has no current mapping of it.
  categoryId?: string
  categoryName?: string
  categoryMapping?: 'missing' | 'stale'
  // The mapping check could not read what it needs (not a gap of the product): the card offers "Yeniden Dene".
  checkFailed?: boolean
}
export type BulkConnectionReadiness = { entries: BulkProductReadiness[]; blocked: BulkProductReadiness[]; readyCount: number; skippedCount: number }

type Connection = Pick<MarketplaceConnection, 'id' | 'platformCode' | 'displayName'>
type PublicationStatus = { actualStatus?: string | null; lastJobStatus?: string | null }

// Whether a record needs the check on a connection: what the list knows first, else the publication status the editor's
// card reads. Live or in progress is skipped (the editor's card says "Zaten yayında" and checks nothing).
async function marketplaceStanding(client: QueryClient, listRecord: Product | null, productId: string, connection: Connection): Promise<'skip' | 'check' | 'notPublished'> {
  const bucket = bulkPublishStatusBucket(listRecord ? productConnectionStatus(listRecord, connection) : null)
  if (bucket === 'live' || bucket === 'processing' || bucket === 'linked') return 'skip'
  if (bucket !== 'unknown') return 'check'
  const status = await client.query({
    queryKey: ['publication-status', productId, connection.id],
    queryFn: () => hubApi<PublicationStatus>(`/products/${productId}/publication-status/${connection.id}`, { cache: 'no-store' }),
    staleTime: 15_000,
    retry: false
  }).catch(() => null)
  // Unread stays unknown: it is checked, but not called "not published".
  if (!status) return 'check'
  return isPublicationLive(status.actualStatus) || isPublicationStatusPending(status.actualStatus, status.lastJobStatus) ? 'skip' : 'notPublished'
}

// One record on every connection: its product checks once, then each marketplace's mapping check.
export async function checkProductReadiness(client: QueryClient, productId: string, listRecord: Product | null, connections: Connection[]): Promise<Map<string, BulkProductReadiness>> {
  const results = new Map<string, BulkProductReadiness>()
  let product: Product
  try {
    product = await client.query({ queryKey: ['product', productId], queryFn: () => hubApi<Product>(`/products/${productId}`), staleTime: 15_000, retry: false })
  } catch {
    for (const connection of connections) results.set(connection.id, { productId, title: listRecord?.title ?? 'Ürün kartı', skipped: false, issues: [{ attribute: 'Ürün kaydı', detail: 'Ürün okunamadı; listeyi yenileyip yeniden deneyin.' }], fixes: [] })
    return results
  }
  const categoryId = product.categoryId ?? ''
  // The editor's category list (same query), for the category's name in the "not mapped" gap.
  const categories = categoryId ? await client.query({ queryKey: ['categories', 'new-product'], queryFn: () => loadAllPages<Category>('/catalog/categories'), staleTime: 60_000, retry: false }).catch(() => null) : null
  const categoryName = categories?.items.find(item => item.id === categoryId)?.name ?? ''
  let requirements: CategoryRequirement[] = []
  let requirementsFailed = false
  if (categoryId) {
    try {
      requirements = sortRequirements(await client.query({ queryKey: ['category-requirements', categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${categoryId}/attribute-requirements`), staleTime: 15_000, retry: false }))
    } catch { requirementsFailed = true }
  }
  const draft = publicationReadinessDraftFromProduct(product, requirements)
  const productChecks = publicationProductChecks(draft, { items: requirements, isLoading: false, isError: requirementsFailed, isSuccess: Boolean(categoryId) && !requirementsFailed })
  const selectedAttributes = publicationAttributeSelectionsFor(draft, requirements)
  for (const connection of connections) {
    const standing = await marketplaceStanding(client, listRecord, productId, connection)
    if (standing === 'skip') {
      results.set(connection.id, { productId, title: product.title, skipped: true, issues: [], fixes: [] })
      continue
    }
    const isShopify = connection.platformCode.trim().toUpperCase() === 'SHOPIFY'
    const cardName = connection.displayName
    // As the editor's card: a Shopify store without its verified write permissions blocks (the server would answer
    // SHOPIFY_PUBLICATION_SCOPES_REQUIRED). The card's own query, so each store is read once.
    let capabilities: ConnectionCapability[] | undefined
    let capabilitiesFailed = false
    if (isShopify) {
      try {
        capabilities = await client.query({ queryKey: connectionCapabilitiesQueryKey(connection.id), queryFn: () => loadConnectionCapabilities(connection.id), staleTime: 15_000, retry: false })
      } catch { capabilitiesFailed = true }
    }
    let mapping: PublicationAttributeIssues | undefined
    let mappingError: unknown = null
    // As the editor's card: Shopify needs no category mapping, and nothing is checked without a category.
    if (!isShopify && categoryId) {
      try {
        mapping = await client.query({ queryKey: publicationReadinessQueryKey(connection.id, categoryId, selectedAttributes), queryFn: () => loadPublicationAttributeIssues(client, connection, cardName, categoryId, selectedAttributes, categoryName), staleTime: 15_000, retry: false })
      } catch (reason) { mappingError = reason }
    }
    results.set(connection.id, {
      productId,
      title: product.title,
      skipped: false,
      notPublished: standing === 'notPublished',
      issues: publicationBlockingIssues(missingPublicationChecks(productChecks, connection.platformCode), shopifyCapabilityIssues(connection.platformCode, capabilities, capabilitiesFailed), mapping?.requiredIssues ?? [], mappingError, cardName),
      warnings: (mapping?.optionalWarnings ?? []).map(issue => ({ attribute: issue.attribute, detail: issue.detail, fixable: Boolean(issue.fix) })),
      fixes: fixableValueMappings(mapping),
      categoryScope: mapping?.categoryScope,
      categoryId,
      categoryName,
      categoryMapping: mapping?.categoryMapping,
      checkFailed: Boolean(mappingError)
    })
  }
  return results
}

export function summarizeConnectionReadiness(entries: BulkProductReadiness[]): BulkConnectionReadiness {
  const blocked = entries.filter(entry => !entry.skipped && entry.issues.length > 0)
  const skippedCount = entries.filter(entry => entry.skipped).length
  return { entries, blocked, readyCount: entries.length - blocked.length - skippedCount, skippedCount }
}

// Runs `task` over `items`, at most `limit` at once.
export async function runLimited<T>(items: T[], limit: number, task: (item: T) => Promise<void>, cancelled: () => boolean) {
  let next = 0
  const worker = async () => {
    while (!cancelled() && next < items.length) await task(items[next++])
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker))
}

// The popup's check of the selection: "x/y ürün kontrol edildi" while it runs, then each connection's summary.
export function useBulkPublicationReadiness(productIds: string[], connections: Connection[], listRecords: Array<Product | null> | null) {
  const client = useQueryClient()
  const [results, setResults] = useState<Map<string, Map<string, BulkProductReadiness>>>(new Map())
  const [run, setRun] = useState(0)
  const connectionKey = connections.map(connection => connection.id).join(',')
  const productKey = productIds.join(',')
  useEffect(() => {
    let stopped = false
    setResults(new Map())
    const records = new Map((listRecords ?? []).filter((record): record is Product => record !== null).map(record => [record.id, record]))
    void runLimited(productIds, bulkReadinessConcurrency, async productId => {
      const checked = await checkProductReadiness(client, productId, records.get(productId) ?? null, connections)
      if (!stopped) setResults(current => new Map(current).set(productId, checked))
    }, () => stopped)
    return () => { stopped = true }
    // The selection and the connections are the inputs; a recheck (after a value mapping was saved) runs it again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, productKey, connectionKey, run])
  const recheck = useCallback(() => setRun(value => value + 1), [])
  const done = results.size >= productIds.length
  const byConnection = new Map(connections.map(connection => [connection.id, summarizeConnectionReadiness(productIds.flatMap(id => results.get(id)?.get(connection.id) ?? []))]))
  return { checked: results.size, total: productIds.length, done, byConnection, recheck }
}

// The run's slices: a store whose records all passed gets every selected record (the server skips what is already
// out there); a store with gaps gets only the records without them. Stores that get the same records share slices.
export function bulkPublishPlan(productIds: string[], records: Parameters<typeof bulkPublishChunks>[1], connectionIds: string[], readiness: Map<string, BulkConnectionReadiness>, size = 50) {
  const groups = new Map<string, { productIds: string[]; connectionIds: string[] }>()
  for (const connectionId of connectionIds) {
    const blocked = new Set((readiness.get(connectionId)?.blocked ?? []).map(entry => entry.productId))
    const ids = productIds.filter(id => !blocked.has(id))
    if (!ids.length) continue
    const key = ids.join(',')
    const group = groups.get(key)
    if (group) group.connectionIds.push(connectionId)
    else groups.set(key, { productIds: ids, connectionIds: [connectionId] })
  }
  const chunks = [...groups.values()].flatMap(group => bulkPublishChunks(group.productIds, records, size).map(part => ({ productIds: part, connectionIds: group.connectionIds })))
  return { chunks, total: chunks.reduce((count, chunk) => count + chunk.productIds.length, 0) }
}
