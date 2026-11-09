import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// GET /products as the server sends it since 9 Nov 2026: the slim list card, its null fields left out (no description
// text, no attributes, options or picture strip on the card, no channel offers on the variants, one variant picture).
const listPage = (after: string | null) => ({
  items: after ? [] : [{
    id: 'p1', title: 'Triko Kazak', description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 3,
    variants: [
      { id: 'v1', sku: 'RYP-1-S', barcode: '869001', modelCode: 'RYP001', optionSignature: 'Renk:Bej|Beden:S', status: 'ACTIVE', version: 1, onHand: 4, available: 4, salePrice: 450, listPrice: 500, currency: 'TRY', mediaUrls: ['/api/v1/files/product-media/a/content'], options: { Renk: 'Bej', Beden: 'S' }, productId: 'p1' },
      { id: 'v2', sku: 'RYP-1-M', modelCode: 'RYP001', optionSignature: 'Renk:Bej|Beden:M', status: 'ACTIVE', version: 1, onHand: 0, available: 0, options: { Renk: 'Bej', Beden: 'M' }, productId: 'p1' }
    ],
    primaryImageUrl: '/api/v1/files/product-media/a/content', totalStock: 4, startingPrice: 450, currency: 'TRY', modelCode: 'RYP001', activePlatforms: ['Trendyol'],
    platformStatuses: [{ platform: 'Trendyol', status: 'ACTIVE', matchedVariantCount: 2, variantCount: 2, isChecking: false, platformCode: 'TRENDYOL', missingVariantCount: 0, onSaleVariantCount: 2, offSaleVariantCount: 0, offSaleStockZeroVariantCount: 0 }],
    categoryPath: 'Giyim > Kazak', hasCustomMediaOrder: false
  }],
  nextCursor: after ? undefined : 'cursor-2', hasMore: !after, totalCount: 2
})
const requested: string[] = []

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    requested.push(path)
    if (path.startsWith('/products/summary')) return { totalCount: 2, activeCount: 2, outOfStockCount: 0, lowStockCount: 0, platforms: ['Trendyol'] }
    if (path.startsWith('/products?')) return listPage(new URLSearchParams(path.split('?')[1]).get('after'))
    if (path.startsWith('/jobs')) return []
    throw new Error(`Test API fixture does not handle ${path}`)
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { ProductsPage } from './ProductsPage'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  container.remove()
})

async function until(test: () => boolean, timeoutMs = 3000) {
  const started = Date.now()
  while (!test()) {
    if (Date.now() - started > timeoutMs) throw new Error('Timed out waiting for the products list.')
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
  }
}

async function render() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products']}><ProductsPage /></MemoryRouter></QueryClientProvider>)
  })
  await until(() => Boolean(container.textContent?.includes('Triko Kazak')))
}

const nextPageRequests = () => requested.filter(path => path.startsWith('/products?') && path.includes('after='))

describe('ProductsPage on the slim list answer', () => {
  it('shows the card, its price, stock and platform marks from the slim card', async () => {
    await render()
    const row = Array.from(container.querySelectorAll('tbody tr')).find(item => item.textContent?.includes('Triko Kazak'))!
    expect(row).toBeTruthy()
    expect(row.textContent).toContain('RYP001')
    expect(row.querySelector('td[data-l="Stok"]')?.textContent).toContain('4')
    expect(row.querySelector('td[data-l="Fiyat"]')?.textContent).toMatch(/450/)
    // The list price shows struck through beside the cheapest variant's sale price.
    expect(row.querySelector('td[data-l="Fiyat"] small')?.textContent).toMatch(/500/)
    const trendyol = row.querySelector('.rr-pm[aria-label^="Trendyol"]')
    expect(trendyol?.className).toContain('rr-on')
    expect(row.querySelector('img')?.getAttribute('src')).toBe('/api/v1/files/product-media/a/content')
  })

  it('reads the next page ahead only after this page, not at once', async () => {
    await render()
    // The page is on screen and its answer named a next page, but the next page is not read with it.
    expect(nextPageRequests()).toEqual([])
    // Nothing is loading on screen here, so it is read once the browser is idle.
    await until(() => nextPageRequests().length > 0)
    expect(nextPageRequests()).toEqual(['/products?limit=20&after=cursor-2'])
  })
})
