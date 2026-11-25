import type { ApiResponse } from '../../shared/api/generated/api-types'
import type { Product, Variant } from './shared'

// GET /products answers each card as GET /products/{id} does but leaves its null members out (ProductListJson, at every
// level), so a missing member there means exactly null. The list pages read their rows through readProductListItem,
// the one boundary: past it a list row is a Product whose unset members are null, as the editor's card is.
export type ProductListItem = ApiResponse<'getProducts'>['items'][number]
type ListVariant = ProductListItem['variants'][number]
type ListVariantStatus = NonNullable<ListVariant['platformStatuses']>[number]
type ListAttribute = NonNullable<ProductListItem['attributes']>[number]

const orNull = <T,>(value: T | null | undefined): T | null => value ?? null

function readAttribute(item: ListAttribute): NonNullable<Product['attributes']>[number] {
  return { ...item, valueId: orNull(item.valueId), textValue: orNull(item.textValue), numberValue: orNull(item.numberValue), booleanValue: orNull(item.booleanValue) }
}

function readVariantStatus(item: ListVariantStatus): NonNullable<Variant['platformStatuses']>[number] {
  return {
    ...item, connectionId: orNull(item.connectionId), offerId: orNull(item.offerId), listPrice: orNull(item.listPrice), salePrice: orNull(item.salePrice),
    currency: orNull(item.currency), vatRate: orNull(item.vatRate), vatInclusion: orNull(item.vatInclusion), roundingMode: orNull(item.roundingMode),
    safetyStock: orNull(item.safetyStock), offerVersion: orNull(item.offerVersion), offerStatus: orNull(item.offerStatus), followsPanelPrice: orNull(item.followsPanelPrice),
    saleState: orNull(item.saleState), saleStateReason: orNull(item.saleStateReason), listingStatus: orNull(item.listingStatus), rejectionReason: orNull(item.rejectionReason)
  }
}

function readVariant(item: ListVariant): Variant {
  return {
    ...item, barcode: orNull(item.barcode), modelCode: orNull(item.modelCode), weight: orNull(item.weight), width: orNull(item.width), height: orNull(item.height),
    length: orNull(item.length), desi: orNull(item.desi), costPrice: orNull(item.costPrice), inventoryVersion: orNull(item.inventoryVersion), offerId: orNull(item.offerId),
    listPrice: orNull(item.listPrice), salePrice: orNull(item.salePrice), currency: orNull(item.currency), offerStatus: orNull(item.offerStatus),
    priceVersion: orNull(item.priceVersion), offerVersion: orNull(item.offerVersion), vatRate: orNull(item.vatRate), vatInclusion: orNull(item.vatInclusion),
    roundingMode: orNull(item.roundingMode), safetyStock: orNull(item.safetyStock), mediaUrls: orNull(item.mediaUrls), options: orNull(item.options),
    platformStatuses: item.platformStatuses ? item.platformStatuses.map(readVariantStatus) : null, defaultListPrice: orNull(item.defaultListPrice),
    defaultSalePrice: orNull(item.defaultSalePrice), attributes: item.attributes ? item.attributes.map(readAttribute) : null, productId: orNull(item.productId)
  }
}

/** A row of GET /products as the full card type, every member the list left out as the null it stood for. */
export function readProductListItem(item: ProductListItem): Product {
  return {
    ...item, brandId: orNull(item.brandId), categoryId: orNull(item.categoryId), primaryImageUrl: orNull(item.primaryImageUrl), startingPrice: orNull(item.startingPrice),
    modelCode: orNull(item.modelCode), activePlatforms: orNull(item.activePlatforms), attributes: item.attributes ? item.attributes.map(readAttribute) : null,
    options: orNull(item.options), mediaUrls: orNull(item.mediaUrls), familyMediaUrls: orNull(item.familyMediaUrls),
    platformStatuses: item.platformStatuses ? item.platformStatuses.map(status => ({ ...status, offSaleReasonCounts: orNull(status.offSaleReasonCounts), rejectionReason: orNull(status.rejectionReason) })) : null,
    categoryPath: orNull(item.categoryPath), familyMediaItems: orNull(item.familyMediaItems), defaultListPrice: orNull(item.defaultListPrice),
    defaultSalePrice: orNull(item.defaultSalePrice), warrantyMonths: orNull(item.warrantyMonths), trendyolWebColorValueId: orNull(item.trendyolWebColorValueId),
    vatRate: orNull(item.vatRate), vatInclusion: orNull(item.vatInclusion), deliveryDays: orNull(item.deliveryDays), familyOrderedMediaUrls: orNull(item.familyOrderedMediaUrls),
    familyProductIds: orNull(item.familyProductIds), familyVersions: orNull(item.familyVersions),
    familyGalleries: item.familyGalleries ? item.familyGalleries.map(gallery => ({ ...gallery, sizeChartUrl: orNull(gallery.sizeChartUrl) })) : null,
    sizeChartUrl: orNull(item.sizeChartUrl), familyImageUrl: orNull(item.familyImageUrl), familyKey: orNull(item.familyKey),
    variants: item.variants.map(readVariant)
  }
}

/** A cursor page of GET /products with its rows read as cards. */
export function readProductListPage<P extends { items: ProductListItem[] }>(page: P): Omit<P, 'items'> & { items: Product[] } {
  return { ...page, items: page.items.map(readProductListItem) }
}
