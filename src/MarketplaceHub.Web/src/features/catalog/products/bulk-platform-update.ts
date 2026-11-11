import { editorOpenedDesi } from '../product-editor-state'
import { platformUpdatePlan, type PlatformUpdateField } from '../product-editor/platform-update-fields'
import { missingRequiredEditorFields, requiredEditorFieldLabels, type RequiredEditorField } from '../product-editor/required-fields'
import type { MarketplaceConnection, Product } from '../shared'
import { bulkPublishStatusBucket, productConnectionStatus } from './bulk-publish-status'

// The product list's "Platformlara Güncelle": the editor's "Platformlarda Güncelle" for several saved products. Only
// what the owner picks in the window goes, when the owner presses Gönder (a manual write: the automatic price/stock
// switch stays as it is); a product goes only to a store it is already on, and a product with an empty required field
// (required-fields.ts) is held back, as the editor's Hızlı Güncelle stops on one.

type Connection = Pick<MarketplaceConnection, 'id' | 'platformCode' | 'displayName'>

// On the store already: live, partly live or matched there. In progress, refused or not there is not (an update needs
// the marketplace's listing).
export function isOnMarketplace(record: Pick<Product, 'platformStatuses'>, connection: Pick<MarketplaceConnection, 'platformCode' | 'displayName'>) {
  const bucket = bulkPublishStatusBucket(productConnectionStatus(record, connection))
  return bucket === 'live' || bucket === 'partial' || bucket === 'linked'
}

// The menu item: off with its reason while none of the selected products is on any store that takes an update.
export function bulkPlatformUpdateMenuState(records: ReadonlyArray<Pick<Product, 'platformStatuses'>>, connections: readonly Connection[]): { disabled: boolean; detail: string } {
  if (!records.length) return { disabled: true, detail: 'Önce en az bir ürün seçin' }
  if (!connections.length) return { disabled: true, detail: 'Güncellenebilir pazar yeri bağlantısı yok' }
  const anyLive = records.some(record => connections.some(connection => isOnMarketplace(record, connection)))
  return anyLive ? { disabled: false, detail: 'Seçili ürünlerin güncellemesini gönder' } : { disabled: true, detail: 'Seçili ürünlerin hiçbiri pazar yerinde yayında değil' }
}

// The editor's required fields, read from a saved product as the editor fills its form from it (an empty warranty or
// delivery time opens as 0 and 1 there, so they are filled); the codes of a card with variants are on its rows.
export function productRequiredGaps(product: Pick<Product, 'title' | 'brandId' | 'categoryId' | 'modelCode' | 'variants' | 'defaultListPrice' | 'defaultSalePrice' | 'warrantyMonths' | 'deliveryDays'>): RequiredEditorField[] {
  const primary = product.variants[0]
  const desi = editorOpenedDesi(product.variants)
  const listPrice = product.defaultListPrice ?? primary?.listPrice ?? primary?.salePrice ?? 0
  const salePrice = product.defaultSalePrice ?? primary?.salePrice ?? 0
  return missingRequiredEditorFields({
    title: product.title ?? '',
    brandId: product.brandId ?? '',
    categoryId: product.categoryId ?? '',
    modelCode: primary?.modelCode ?? product.modelCode ?? '',
    baseSku: primary?.sku ?? '',
    barcode: primary?.barcode ?? '',
    desi: desi == null ? '' : String(desi),
    warrantyMonths: String(product.warrantyMonths ?? 0),
    deliveryDays: String(product.deliveryDays ?? 1),
    listPrice: String(listPrice),
    salePrice: String(salePrice)
  }, { automaticBarcodes: false, hasVariants: product.variants.length > 1 })
}

export function requiredGapText(gaps: readonly RequiredEditorField[]) {
  return gaps.map(field => requiredEditorFieldLabels[field]).join(', ')
}

export type BulkUpdateRecord = Pick<Product, 'id' | 'title' | 'status' | 'platformStatuses'> & { gaps: RequiredEditorField[] }

export type BulkUpdateRequest =
  | { kind: 'product'; productId: string; connectionId: string; fields: PlatformUpdateField[]; sendGroupId?: string }
  | { kind: 'priceInventory'; productId: string; connectionId: string; price: boolean; stock: boolean }

// Cards of one send to one Trendyol store go in one request per step (TrendyolUpdateGroups, up to 1,000 items); a group
// is kept small so a card does not wait long for the others of its group.
export const bulkUpdateGroupSize = 25

// What Gönder sends: for each picked store, each ready product on it gets the product information this marketplace
// takes of the pick (one job) and the price and/or stock part (one job). A draft or a product with a required gap is
// held back; a product not on the store is left out there.
export function bulkPlatformUpdateRequests(records: readonly BulkUpdateRecord[], connections: readonly Connection[], connectionIds: readonly string[], fields: readonly PlatformUpdateField[], newGroupId: () => string): BulkUpdateRequest[] {
  const requests: BulkUpdateRequest[] = []
  const ready = records.filter(record => !record.gaps.length && record.status.trim().toUpperCase() !== 'DRAFT')
  for (const connectionId of new Set(connectionIds)) {
    const connection = connections.find(item => item.id === connectionId)
    if (!connection) continue
    const plan = platformUpdatePlan(connection.platformCode, fields)
    const onStore = ready.filter(record => isOnMarketplace(record, connection))
    if (plan.productFields.length) {
      for (let start = 0; start < onStore.length; start += bulkUpdateGroupSize) {
        const group = onStore.slice(start, start + bulkUpdateGroupSize)
        const sendGroupId = group.length > 1 ? newGroupId() : undefined
        for (const record of group) requests.push({ kind: 'product', productId: record.id, connectionId, fields: [...plan.productFields], ...(sendGroupId ? { sendGroupId } : {}) })
      }
    }
    if (plan.price || plan.stock) for (const record of onStore) requests.push({ kind: 'priceInventory', productId: record.id, connectionId, price: plan.price, stock: plan.stock })
  }
  return requests
}

// How many of the records are on each store, for the store's card ("4/6 ürün bu mağazada").
export function onMarketplaceCount(records: ReadonlyArray<Pick<Product, 'platformStatuses'>>, connection: Connection) {
  return records.filter(record => isOnMarketplace(record, connection)).length
}
