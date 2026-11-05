import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// An existing card with two variants priced apart in the panel (A = 100, B = 200), each with an N11 offer that follows
// the panel price (version 3): A's offer is at A's price, B's offer still at 180 (the panel moved on). A saved N11
// listing profile carries a title, description and delivery override. The card is published again from the editor.
const fixture = vi.hoisted(() => {
  const status = (offerId: string, price: number) => ({ connectionId: 'ty', platformCode: 'N11', platform: 'N11', status: 'FAILED', offerId, offerVersion: 3, listPrice: price, salePrice: price, currency: 'TRY', vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, offerStatus: 'ACTIVE', followsPanelPrice: true })
  const variant = (id: string, sku: string, barcode: string, size: string, price: number, offerId: string, offerPrice: number) => ({ id, productId: 'p1', version: 1, sku, barcode, modelCode: 'RYP020', optionSignature: `Beden: ${size}`, status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1, offerId, listPrice: offerPrice, salePrice: offerPrice, defaultListPrice: price, defaultSalePrice: price, currency: 'TRY', offerStatus: 'ACTIVE', priceVersion: 1, offerVersion: 3, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, mediaUrls: ['https://cdn.example.com/a.jpg'], options: { Beden: size }, attributes: [], platformStatuses: [status(offerId, offerPrice)] })
  const product = { id: 'p1', version: 1, title: 'Yonca Desen Elbise', description: '<p>Açıklama metni yeterince uzun bir açıklama.</p>', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant('vA', 'RYP020-S', 'RYP0200001', 'S', 100, 'oA', 100), variant('vB', 'RYP020-M', 'RYP0200002', 'M', 200, 'oB', 180)], primaryImageUrl: 'https://cdn.example.com/a.jpg', totalStock: 6, startingPrice: 100, currency: 'TRY', modelCode: 'RYP020', activePlatforms: [], mediaUrls: ['https://cdn.example.com/a.jpg'], attributes: [], familyProductIds: ['p1'], defaultListPrice: 100, defaultSalePrice: 100, vatRate: 20, vatInclusion: 'INCLUDED', deliveryDays: 2 }
  const connection = { id: 'ty', platformCode: 'N11', displayName: 'N11 Mağaza', externalStoreId: '930112', status: 'ACTIVE', externalWritesEnabled: true }
  const calls: Array<{ path: string; method: string; headers: Record<string, string>; body: unknown }> = []
  return { product, connection, calls }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    let body: unknown = init?.body
    if (typeof body === 'string') { try { body = JSON.parse(body) } catch { /* keep */ } }
    fixture.calls.push({ path, method, headers: (init?.headers ?? {}) as Record<string, string>, body })
    if (method === 'PATCH' && path === '/products/p1') return { ...fixture.product, version: 2 }
    if (path === '/products/p1') return fixture.product
    if (path.startsWith('/products/p1/listing-profiles/ty') && method === 'GET') return { id: 'lp1', productId: 'p1', connectionId: 'ty', titleOverride: 'N11 Özel Başlık', descriptionOverride: 'N11 özel açıklama', externalCategoryId: null, externalBrandId: null, deliveryTimeDays: 5, enabled: true, version: 4 }
    if (path.startsWith('/products/p1/listing-profiles/ty')) return { version: 5 }
    if (path.startsWith('/products/p1/publication-jobs')) return { jobId: 'j1', status: 'PENDING' }
    if (method === 'POST' && path === '/channel-offers') return { id: 'x', version: 4, listPrice: 1, salePrice: 1, currency: 'TRY' }
    if (method === 'PATCH' && path.startsWith('/channel-offers/')) return { id: 'x', version: 4, listPrice: 1, salePrice: 1, currency: 'TRY' }
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return []
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'FAILED', lastJobStatus: 'FAILED', profileId: 'lp1' }
    if (path.startsWith('/mappings/categories?')) return [{ id: 'cm1', connectionId: 'ty', snapshotId: 's1', localId: 'c1', scopeExternalId: '', externalId: 'ty-cat', status: 'VERIFIED', verifiedAt: null, version: 1 }]
    if (path.startsWith('/mappings/categories/c1')) return { id: 'cm1', connectionId: 'ty', snapshotId: 's1', localId: 'c1', scopeExternalId: '', externalId: 'ty-cat', status: 'VERIFIED', verifiedAt: null, version: 1 }
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', resourceType: 'CATEGORIES', fetchedAt: '2026-11-01T10:00:00Z', items: [{ externalId: 'ty-cat', parentExternalId: null, name: 'Elbise', path: 'Giyim > Elbise', depth: 0, isLeaf: true, isActive: true, isRequired: false, allowsCustomValue: false, allowsMultipleValues: false }] }
    if (path.startsWith('/reference-data/categories/ty-cat/attributes?')) return { snapshotId: 'a1', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-11-01T10:00:00Z', items: [] }
    if (path.startsWith('/mappings/attributes?')) return []
    if (path.startsWith('/mappings/attribute-values?')) return []
    if (path.includes('/attribute-defaults')) return []
    if (path.startsWith('/mappings/conflicts')) return {}
    if (path.startsWith('/mappings/attribute-name-hints')) return {}
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => ({
    items: path.startsWith('/connections') ? [fixture.connection]
      : path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise', depth: 0, version: 1 }]
        : path.startsWith('/catalog/brands') ? [{ id: 'b1', name: 'Ravencia', isActive: true }]
          : [],
    nextCursor: null,
    hasMore: false
  })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { NewProductPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  fixture.calls.length = 0
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function waitFor(check: () => boolean) {
  for (let index = 0; index < 250 && !check(); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

const cardHead = () => document.querySelector<HTMLButtonElement>('button.publish-platform-card-head')

describe('Yayınla from the editor on an existing card', () => {
  it('writes each variant its own panel price at the loaded offer version and keeps the listing profile overrides', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" publishOnly /></main></MemoryRouter></QueryClientProvider>) })
    await waitFor(() => Boolean(cardHead()) && !cardHead()!.disabled)
    expect(cardHead()?.disabled, `card head disabled; calls: ${fixture.calls.map(call => call.method + ' ' + call.path).join(' | ')}`).toBe(false)
    await act(async () => { cardHead()!.click() })
    await waitFor(() => cardHead()?.getAttribute('aria-pressed') === 'true')
    expect(cardHead()?.getAttribute('aria-pressed')).toBe('true')
    // Let the card's checks settle (a gap would drop it from the targets).
    for (let index = 0; index < 20; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
    const publish = [...document.querySelectorAll<HTMLButtonElement>('.publish-selection-footer button[type="submit"]')][0]
    expect(publish, 'Yayınla button').toBeTruthy()
    await act(async () => { publish.click() })
    await waitFor(() => fixture.calls.some(call => call.path.includes('/publication-jobs')))
    for (let index = 0; index < 30; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })

    const writes = fixture.calls.filter(call => call.method !== 'GET')
    expect(writes.some(call => call.path.includes('/publication-jobs')), 'publication job').toBe(true)
    const offerWrites = writes.filter(call => call.path.startsWith('/channel-offers') && !call.path.includes('panel-price-follow'))
    // The editor holds B's panel price as 200 (it saves it back on the product PATCH).
    const productPatch = writes.find(call => call.method === 'PATCH' && call.path === '/products/p1')!.body as { variantUpdates: Array<{ id: string; defaultListPrice: number; defaultSalePrice: number; loadedVersion: number }> }
    expect(productPatch.variantUpdates.find(update => update.id === 'vB')).toMatchObject({ defaultListPrice: 200, defaultSalePrice: 200, loadedVersion: 1 })

    // CAT-02: A's offer already sells at A's panel price: nothing is written to it.
    expect(offerWrites.filter(call => call.path === '/channel-offers/oA' || (call.body as { variantId?: string }).variantId === 'vA')).toEqual([])
    // CAT-01 + CAT-02: B follows the panel, so its offer takes B's own panel price (200, not the card's 100), written by
    // PATCH at the offer version the editor loaded. No offer is POST-upserted.
    expect(offerWrites).toHaveLength(1)
    const toB = offerWrites[0]
    expect({ method: toB.method, path: toB.path, ifMatch: toB.headers['If-Match'] ?? null }).toEqual({ method: 'PATCH', path: '/channel-offers/oB', ifMatch: '"v3"' })
    expect(toB.body).toMatchObject({ listPrice: 200, salePrice: 200, followsPanelPrice: true })

    // CAT-06: the saved profile's overrides survive the re-publish.
    const profilePut = writes.find(call => call.method === 'PUT' && call.path === '/products/p1/listing-profiles/ty')!
    expect(profilePut.headers['If-Match']).toBe('"v4"')
    expect(profilePut.body).toEqual({ titleOverride: 'N11 Özel Başlık', descriptionOverride: 'N11 özel açıklama', externalCategoryId: null, externalBrandId: null, deliveryTimeDays: 5, enabled: true })
  }, 30000)
})
