import { describe, expect, it } from 'vitest'
import { readProductListItem, readProductListPage } from './product-list-item'

// GET /products writes with WhenWritingNull (CatalogEndpoints.ProductListJson): a left-out member stood for null.
const listRow = {
  id: 'p1', title: 'Gömlek', description: '', status: 'ACTIVE', updatedAt: '2026-11-16T00:00:00Z', version: 2, totalStock: 3, currency: 'TRY', hasCustomMediaOrder: false,
  modelCode: 'M1', platformStatuses: [{ platform: 'Trendyol', status: 'LIVE', matchedVariantCount: 1, variantCount: 1, isChecking: false, platformCode: 'TRENDYOL', missingVariantCount: 0,
    onSaleVariantCount: 1, offSaleVariantCount: 0, offSaleStockZeroVariantCount: 0, rejectedVariantCount: 0, pendingVariantCount: 0, resubmittedVariantCount: 0, updatingVariantCount: 0, reviewVariantCount: 0 }],
  variants: [{ id: 'v1', sku: 'S1', optionSignature: 'Beden: M', status: 'ACTIVE', version: 1, onHand: 3, available: 3, salePrice: 450,
    platformStatuses: [{ platform: 'Trendyol', platformCode: 'TRENDYOL', isLinked: true, status: 'LIVE', isMatched: true, isMissing: false, offerId: 'o1' }] }]
}

describe('a GET /products row read as the card', () => {
  it('reads every left-out member as null and keeps what the row carries', () => {
    const card = readProductListItem(listRow)
    expect(card).toMatchObject({ id: 'p1', modelCode: 'M1', brandId: null, categoryId: null, primaryImageUrl: null, attributes: null, familyGalleries: null, sizeChartUrl: null })
    expect(card.platformStatuses?.[0]).toMatchObject({ platformCode: 'TRENDYOL', rejectionReason: null, offSaleReasonCounts: null })
    expect(card.variants[0]).toMatchObject({ salePrice: 450, barcode: null, mediaUrls: null, options: null, attributes: null })
    expect(card.variants[0].platformStatuses?.[0]).toMatchObject({ offerId: 'o1', connectionId: null, followsPanelPrice: null, listingStatus: null })
  })

  it('reads a page row by row and keeps its paging members', () => {
    const page = readProductListPage({ items: [listRow], nextCursor: 'c', hasMore: true, totalCount: 9 })
    expect(page).toMatchObject({ nextCursor: 'c', hasMore: true, totalCount: 9 })
    expect(page.items[0].brandId).toBeNull()
  })
})
