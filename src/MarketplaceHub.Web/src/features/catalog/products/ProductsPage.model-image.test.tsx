import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Product } from '../shared'
import { productGroupImage } from './product-group-image'

// Owner, 12.11.2026 ("51 de 1. görsel yeşil ama kahve duruyor?"): live MZ051BYC's family gallery starts with Haki, but
// its newest colour card (the list's first card of the model) is Kahverengi. The model row shows the gallery's first.
const haki = '/api/v1/files/product-media/haki-1/content'
const kahve = '/api/v1/files/product-media/kahve-1/content'
const card = (id: string, colour: string, image: string) => ({
  id, title: `Kışlık Triko Bluz ${colour}`, description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 1,
  variants: [{ id: `${id}-v1`, sku: `MZ051BYC-${id}`, modelCode: 'MZ051BYC', optionSignature: `Renk:${colour}|Beden:S`, status: 'ACTIVE', version: 1, onHand: 3, available: 3, salePrice: 450, currency: 'TRY', mediaUrls: [image], options: { Renk: colour, Beden: 'S' }, productId: id }],
  primaryImageUrl: image, familyImageUrl: haki, totalStock: 3, startingPrice: 450, currency: 'TRY', modelCode: 'MZ051BYC', activePlatforms: [], platformStatuses: [], categoryPath: 'Bluz', hasCustomMediaOrder: true
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/products/summary')) return { totalCount: 1, activeCount: 1, outOfStockCount: 0, lowStockCount: 0, platforms: [] }
    if (path.startsWith('/products?')) return { items: [card('kahve', 'Kahverengi', kahve), card('haki', 'Haki', haki)], nextCursor: null, hasMore: false, totalCount: 1 }
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

describe('the products list model row picture', () => {
  it('shows the family gallery first picture, not the newest colour card picture', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    root = createRoot(container)
    await act(async () => {
      root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products']}><ProductsPage /></MemoryRouter></QueryClientProvider>)
    })
    await until(() => Boolean(container.querySelector('tbody tr img')))
    const rows = Array.from(container.querySelectorAll('tbody tr')).filter(row => row.textContent?.includes('MZ051BYC'))
    expect(rows).toHaveLength(1)
    const src = rows[0].querySelector('img')?.getAttribute('src') ?? ''
    expect(src).toContain('haki-1')
    expect(src).not.toContain('kahve-1')
  })

  it('falls back to a colour card front picture when the family has none', () => {
    const product = (id: string, primaryImageUrl: string | null, familyImageUrl?: string | null) => ({ id, primaryImageUrl, familyImageUrl }) as unknown as Product
    expect(productGroupImage({ primary: product('a', kahve, haki), products: [product('a', kahve, haki)] })).toBe(haki)
    expect(productGroupImage({ primary: product('a', kahve, null), products: [product('a', kahve, null)] })).toBe(kahve)
    expect(productGroupImage({ primary: product('a', null), products: [product('a', null), product('b', haki)] })).toBe(haki)
    expect(productGroupImage({ primary: product('a', null), products: [product('a', null)] })).toBeNull()
  })
})
