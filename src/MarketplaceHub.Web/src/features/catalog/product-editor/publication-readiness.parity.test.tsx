import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// One saved record with gaps the editor's publish card lists: no description, no image, no Web Color mapping in its
// category and no category mapping on the store.
const fixture = vi.hoisted(() => {
  const variant = { id: 'v1', version: 1, sku: 'RYP006-S', barcode: 'RYP0060001', modelCode: 'RYP006', optionSignature: '-', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: {}, attributes: [] }
  const product = { id: 'p1', version: 1, title: 'Yonca Desen Elbise', description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant], primaryImageUrl: null, totalStock: 3, startingPrice: 90, currency: 'TRY', modelCode: 'RYP006', activePlatforms: [], mediaUrls: [], attributes: [], familyProductIds: ['p1'] }
  const connection = { id: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağaza', externalStoreId: '930112', status: 'ACTIVE', externalWritesEnabled: true }
  return { product, connection }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string) => {
    if (path === '/products/p1') return fixture.product
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return []
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/mappings/categories/')) return null
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => ({
    items: path.startsWith('/connections') ? [fixture.connection]
      : path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise' }]
        : path.startsWith('/catalog/brands') ? [{ id: 'b1', name: 'Ravencia', isActive: true }]
          : [],
    nextCursor: null,
    hasMore: false
  })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { NewProductPage } from './ProductEditorPages'
import { checkProductReadiness } from '../products/bulk-publish-readiness'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function settle(rounds = 8) {
  for (let index = 0; index < rounds; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}

describe('publish readiness parity', () => {
  it('lists the same gaps for a record in the editor publish step and in the bulk publish check', async () => {
    const editorClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={editorClient}><MemoryRouter><main><NewProductPage editProductId="p1" publishOnly /></main></MemoryRouter></QueryClientProvider>) })
    await settle()
    const editorIssues = [...document.querySelectorAll('.publish-issue-tooltip li')].map(item => item.textContent?.replace(/\s+/g, ' ').trim())
    expect(editorIssues.length).toBeGreaterThan(0)

    const bulkClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const bulk = await checkProductReadiness(bulkClient, 'p1', null, [fixture.connection])
    const bulkIssues = bulk.get('ty')!.issues.map(issue => `${issue.attribute} ${issue.detail}`.replace(/\s+/g, ' ').trim())

    expect(bulkIssues).toEqual(editorIssues)
    // The gaps themselves, so a change on either side shows here too.
    expect(bulkIssues).toEqual([
      'Temel Ürün Verileri Eksik alanlar: açıklama.',
      'Görsel Kalitesi En az bir yüksek çözünürlüklü görsel ekleyin.',
      'Kategori ve Web Color Web Color için kategori özelliği eşleştirilmemiş.',
      'Kategori eşlemesi Yayın öncesi güncel Trendyol Mağaza kategori eşlemesi gerekir.'
    ])
  })
})
