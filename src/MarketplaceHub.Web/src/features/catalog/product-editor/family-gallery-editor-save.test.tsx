import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// MZ051BYC (owner, 11.11.2026): "hangi rengi açtığın fark etmemeli". A model with two colour cards whose Siyah card's
// gallery also holds Bordo's pictures (an earlier save from the Siyah row copied them). Kaydet from the Siyah row and
// from the Bordo row sends the same picture writes: Siyah's gallery back to its own pictures, nothing on Bordo, and the
// model's order once.
const url = (name: string) => `https://cdn.example.com/${name}.jpg`
const bordo = [url('bordo-1'), url('bordo-2')]
const siyah = [url('siyah-1'), url('siyah-2')]

const fixture = vi.hoisted(() => ({ calls: [] as Array<{ method: string; path: string; body: unknown }>, opened: 'siyah' }))

function family(opened: string) {
  const variant = (id: string, productId: string, colour: string, size: string, mediaUrls: string[], index: number) => ({
    id, productId, version: 1, sku: `MZ051BYC-${index}`, barcode: `MZ051BYC-${index}`, modelCode: 'MZ051BYC', optionSignature: `Renk: ${colour} | Beden: ${size}`, status: 'ACTIVE',
    weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 1, available: 1, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY',
    offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 10, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls, options: { Renk: colour, Beden: size }, attributes: []
  })
  const galleries = [
    { productId: 'bordo', mediaUrls: bordo, variantMediaUrls: bordo, sizeChartUrl: null },
    { productId: 'siyah', mediaUrls: [...bordo, ...siyah], variantMediaUrls: siyah, sizeChartUrl: null }
  ]
  const own = galleries.find(card => card.productId === opened)!
  return {
    id: opened, version: 3, title: 'Kışlık Triko Bluz', description: 'Açıklama', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-11T15:00:00Z',
    variants: [variant('b-l', 'bordo', 'Bordo', 'L', [], 1), variant('b-xl', 'bordo', 'Bordo', 'XL', bordo, 2), variant('s-l', 'siyah', 'Siyah', 'L', [], 31), variant('s-xl', 'siyah', 'Siyah', 'XL', siyah, 32)],
    primaryImageUrl: own.mediaUrls[0], totalStock: 4, startingPrice: 90, currency: 'TRY', modelCode: 'MZ051BYC', activePlatforms: [], vatRate: 10, vatInclusion: 'INCLUDED', deliveryDays: 1, warrantyMonths: 0,
    mediaUrls: own.mediaUrls, hasCustomMediaOrder: true,
    familyMediaUrls: [...bordo, ...siyah],
    familyOrderedMediaUrls: [...bordo, ...siyah],
    familyMediaItems: [...bordo, ...siyah].map((item, index) => ({ url: item, mediaIds: [`m${index}`], sourceProductTitles: ['Kışlık Triko Bluz'] })),
    familyGalleries: galleries,
    // Siyah's description differs from the model's (MZ051BYC: two description groups on live).
    familyFieldDifferences: [{ field: 'description', colors: ['Siyah'] }],
    familyProductIds: [opened, opened === 'siyah' ? 'bordo' : 'siyah'],
    options: [{ id: 'o1', label: 'Renk', values: [{ id: 'r1', label: 'Bordo' }, { id: 'r2', label: 'Siyah' }] }, { id: 'o2', label: 'Beden', values: [{ id: 's1', label: 'L' }, { id: 's2', label: 'XL' }] }],
    attributes: []
  }
}

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    fixture.calls.push({ method, path, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null })
    await new Promise(resolve => setTimeout(resolve, 2))
    if (path === `/products/${fixture.opened}`) return family(fixture.opened)
    if (path === '/files/product-media-family-reorder') return { changedProductCount: 1 }
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
  fixture.calls.length = 0
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function settle(rounds = 10) {
  for (let index = 0; index < rounds; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}

// The picture writes Kaydet sends from the given colour row.
async function saveFrom(opened: string, newTitle?: string) {
  fixture.opened = opened
  fixture.calls.length = 0
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[`/products/${opened}`]}><main><Routes>
      <Route path="/products/:id" element={<ProductDetailPage />} />
    </Routes></main></MemoryRouter></QueryClientProvider>)
  })
  await settle()
  const note = container.querySelector('.family-field-note')?.textContent ?? ''
  if (newTitle) {
    const input = container.querySelector<HTMLInputElement>('.product-title-field input')!
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
    act(() => { setter.call(input, newTitle); input.dispatchEvent(new Event('input', { bubbles: true })) })
  }
  const captions = Array.from(container.querySelectorAll('.media-preview-strip figcaption')).map(caption => caption.firstChild?.textContent ?? '')
  const save = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
  await act(async () => { save.form!.requestSubmit(save) })
  await settle(30)
  const writes = fixture.calls.filter(call => call.path.startsWith('/files/') || call.path.endsWith('/size-chart')).map(call => ({ method: call.method, path: call.path, body: call.body }))
  const patch = fixture.calls.find(call => call.method === 'PATCH')?.body as { title: string; familySharedFields: string[] } | undefined
  act(() => root.unmount())
  root = createRoot(container)
  return { writes, captions, note, shared: patch?.familySharedFields, title: patch?.title }
}

describe('Kaydet on a model from any colour row', () => {
  it('sends the same picture writes from the Siyah row and from the Bordo row', async () => {
    const fromSiyah = await saveFrom('siyah')
    const fromBordo = await saveFrom('bordo')

    expect(fromSiyah.writes).toEqual([
      { method: 'PUT', path: '/files/product-media-reconcile', body: { productId: 'siyah', variantId: null, items: siyah.map((item, sortOrder) => ({ url: item, sortOrder })), altText: 'Kışlık Triko Bluz' } },
      { method: 'PUT', path: '/files/product-media-family-reorder', body: { productId: 'bordo', urlsInOrder: [...bordo, ...siyah] } }
    ])
    expect(fromBordo.writes).toEqual(fromSiyah.writes)
    // The strip is the same and names each picture's colour.
    expect(fromSiyah.captions).toEqual(['Bordo', 'Bordo', 'Siyah', 'Siyah'])
    expect(fromBordo.captions).toEqual(fromSiyah.captions)
    // Nothing of the model's fields was changed: no colour card gets another's name or description.
    expect(fromSiyah.shared).toEqual([])
    expect(fromBordo.shared).toEqual([])
    expect(fromSiyah.note).toContain('Renklerde farklı: Açıklama (Siyah)')
    expect(fromBordo.note).toBe(fromSiyah.note)
  })

  it('names a changed model field the same way from every colour row, for the server to write to every colour', async () => {
    const fromSiyah = await saveFrom('siyah', 'Kışlık Yumuşak Triko Bluz')
    const fromBordo = await saveFrom('bordo', 'Kışlık Yumuşak Triko Bluz')
    expect(fromSiyah.shared).toEqual(['title'])
    expect(fromBordo.shared).toEqual(['title'])
    expect(fromBordo.title).toBe(fromSiyah.title)
  })
})
