import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { settleUntilQuiet } from '../../../test/render'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// WEB-SAYFALAR-2: Kaydet writes the model's shared fields to every colour card. It sends the colour cards' versions as
// the form was filled (loadedFamilyVersions); the server refuses a save over a card changed meanwhile (409
// FAMILY_CHANGED). The editor's own save raises the cards' versions, so the next save must carry the new ones.
const fixture = vi.hoisted(() => ({
  calls: [] as Array<{ method: string; path: string; body: unknown }>,
  version: 3,
  bordoVersion: 7,
  refuse: false
}))

function family() {
  const variant = (id: string, productId: string, colour: string, index: number) => ({
    id, productId, version: 1, sku: `MZ060-${index}`, barcode: `MZ060-${index}`, modelCode: 'MZ060', optionSignature: `Renk: ${colour} | Beden: L`, status: 'ACTIVE',
    weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 1, available: 1, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY',
    offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 10, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: { Renk: colour, Beden: 'L' }, attributes: []
  })
  return {
    id: 'siyah', version: fixture.version, title: 'Triko Bluz', description: 'Açıklama', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-15T10:00:00Z',
    variants: [variant('b-l', 'bordo', 'Bordo', 1), variant('s-l', 'siyah', 'Siyah', 2)],
    primaryImageUrl: null, totalStock: 2, startingPrice: 90, currency: 'TRY', modelCode: 'MZ060', activePlatforms: [], vatRate: 10, vatInclusion: 'INCLUDED', deliveryDays: 1, warrantyMonths: 0,
    mediaUrls: [], hasCustomMediaOrder: false, familyMediaUrls: [], familyMediaItems: [],
    familyGalleries: [{ productId: 'bordo', mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null }, { productId: 'siyah', mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null }],
    familyProductIds: ['siyah', 'bordo'],
    familyVersions: { siyah: fixture.version, bordo: fixture.bordoVersion },
    options: [{ id: 'o1', label: 'Renk', values: [{ id: 'r1', label: 'Bordo' }, { id: 'r2', label: 'Siyah' }] }, { id: 'o2', label: 'Beden', values: [{ id: 's1', label: 'L' }] }],
    attributes: []
  }
}

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    fixture.calls.push({ method, path, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null })
    await new Promise(resolve => setTimeout(resolve, 2))
    if (path === '/products/siyah' && method === 'PATCH') {
      if (fixture.refuse) throw new original.ApiRequestError('Modelin başka bir rengi siz düzenlerken değişti; sayfayı yenileyip tekrar deneyin.', 409, 'FAMILY_CHANGED')
      // The save writes the shared fields to Bordo too: both cards move on.
      fixture.version++
      fixture.bordoVersion++
      return family()
    }
    if (path === '/products/siyah') return family()
    if (path.includes('/capabilities') || path.includes('/attribute-requirements')) return []
    return null
  }
  const pages = async () => ({ items: [], nextCursor: null, hasMore: false })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { ProductDetailPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  Object.assign(fixture, { calls: [], version: 3, bordoVersion: 7, refuse: false })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

// W3: until the page and its requests are quiet, not a fixed number of 10 ms rounds of the heavy editor.
async function settle() {
  await settleUntilQuiet(() => fixture.calls.length)
}

async function open() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products/siyah']}><main><Routes>
      <Route path="/products/:id" element={<ProductDetailPage />} />
    </Routes></main></MemoryRouter></QueryClientProvider>)
  })
  await settle()
}

async function save() {
  const button = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
  await act(async () => { button.form!.requestSubmit(button) })
  await settle()
}

const patches = () => fixture.calls.filter(call => call.method === 'PATCH').map(call => call.body as { loadedFamilyVersions: Record<string, number> | null })

describe('Kaydet and the colour cards changed meanwhile (WEB-SAYFALAR-2)', () => {
  it('sends the colour cards’ versions, and the next save the versions its own save left', async () => {
    await open()
    await save()
    await save()

    expect(patches().map(patch => patch.loadedFamilyVersions)).toEqual([{ siyah: 3, bordo: 7 }, { siyah: 4, bordo: 8 }])
    expect(container.querySelector('.product-stale-banner')).toBeNull()
  })

  it('shows the stale banner when the server says another colour card changed', async () => {
    fixture.refuse = true
    await open()
    await save()

    expect(patches()).toHaveLength(1)
    expect(container.querySelector('.product-stale-banner')).not.toBeNull()
  })
})
