import type { Product, Variant } from '../features/catalog/shared'

// Full GET /products/{id} answers for tests (every member the contract writes), built from the few a test names.

export type TestVariantPlatformStatus = NonNullable<Variant['platformStatuses']>[number]
export function testVariantPlatformStatus(overrides: Partial<TestVariantPlatformStatus> & Pick<TestVariantPlatformStatus, 'platform' | 'platformCode'>): TestVariantPlatformStatus {
  return {
    isLinked: true, status: 'LIVE', connectionId: null, offerId: null, listPrice: null, salePrice: null, currency: null, vatRate: null, vatInclusion: null,
    roundingMode: null, safetyStock: null, offerVersion: null, isMatched: true, isMissing: false, offerStatus: null, followsPanelPrice: null,
    saleState: null, saleStateReason: null, listingStatus: null, rejectionReason: null,
    ...overrides
  }
}

export function testVariant(overrides: Partial<Variant> = {}): Variant {
  return {
    id: 'v1', sku: 'SKU-1', barcode: null, modelCode: null, optionSignature: '', status: 'ACTIVE', version: 1,
    weight: null, width: null, height: null, length: null, desi: null, costPrice: null, onHand: 0, available: 0, inventoryVersion: null,
    offerId: null, listPrice: null, salePrice: null, currency: null, offerStatus: null, priceVersion: null, offerVersion: null,
    vatRate: null, vatInclusion: null, roundingMode: null, safetyStock: null, mediaUrls: null, options: null, platformStatuses: null,
    defaultListPrice: null, defaultSalePrice: null, attributes: null, productId: null,
    ...overrides
  }
}

export function testProduct(overrides: Partial<Product> = {}): Product {
  return {
    id: 'p1', title: 'Ürün', description: '', brandId: null, categoryId: null, status: 'ACTIVE', updatedAt: '2026-11-16T00:00:00Z', version: 1,
    variants: [], primaryImageUrl: null, totalStock: 0, startingPrice: null, currency: 'TRY', modelCode: null, activePlatforms: null,
    attributes: null, options: null, mediaUrls: null, familyMediaUrls: null, platformStatuses: null, categoryPath: null, familyMediaItems: null,
    hasCustomMediaOrder: false, defaultListPrice: null, defaultSalePrice: null, warrantyMonths: null, trendyolWebColorValueId: null, vatRate: null,
    vatInclusion: null, deliveryDays: null, familyOrderedMediaUrls: null, familyProductIds: null, familyVersions: null, familyGalleries: null,
    sizeChartUrl: null, familyImageUrl: null, familyKey: null,
    ...overrides
  }
}
