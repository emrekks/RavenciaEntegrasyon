import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { keepPendingCreateMedia, pendingVariantMediaRefs, takePendingCreateMedia } from './pending-create-media'

// A new product with three images whose second upload fails after the product POST.
const fixture = vi.hoisted(() => {
  const variant = { id: 'v9', version: 1, sku: 'NEW-1', barcode: '', modelCode: '', optionSignature: '-', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: ['/api/files/a1'], options: {}, attributes: [] }
  const product = { id: 'p9', version: 2, title: 'Yeni Ürün', description: '', brandId: null, categoryId: null, status: 'DRAFT', updatedAt: '2026-11-05T10:00:00Z', variants: [variant], primaryImageUrl: '/api/files/a1', totalStock: 0, startingPrice: 90, currency: 'TRY', modelCode: '', activePlatforms: [], mediaUrls: ['/api/files/a1'], attributes: [], familyProductIds: ['p9'] }
  const calls: string[] = []
  return { product, calls, uploads: 0 }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    fixture.calls.push(`${init?.method ?? 'GET'} ${path}`)
    if (path === '/products' && init?.method === 'POST') return fixture.product
    if (path === '/files/product-media' && init?.method === 'POST') {
      fixture.uploads += 1
      if (fixture.uploads === 2) throw new Error('upload failed')
      return { fileAssetId: `a${fixture.uploads}` }
    }
    if (path === '/products/p9') return fixture.product
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async () => ({ items: [], nextCursor: null, hasMore: false })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { NewProductPage, ProductDetailPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
const where = { path: '' }
function Where() { where.path = useLocation().pathname; return null }

beforeEach(() => {
  fixture.calls.length = 0
  fixture.uploads = 0
  URL.createObjectURL = vi.fn(() => 'blob:x')
  URL.revokeObjectURL = vi.fn()
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

function setValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  act(() => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}

function unloadBlocked() {
  const event = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent
  window.dispatchEvent(event)
  return event.defaultPrevented
}

describe('new product media after a failed upload', () => {
  it('keeps the files not uploaded yet on the product page the create flow opens, as unsaved changes', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => {
      root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products/new']}><Where /><main><Routes>
        <Route path="/products/new" element={<NewProductPage />} />
        <Route path="/products/:id" element={<ProductDetailPage />} />
      </Routes></main></MemoryRouter></QueryClientProvider>)
    })
    await settle()
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Yeni Ürün')
    setValue(container.querySelector<HTMLInputElement>('input.sku-value')!, 'NEW-1')
    const input = container.querySelector<HTMLInputElement>('input[aria-label="Ürün görsellerini dosya olarak seç"]')!
    const files = ['a.jpg', 'b.jpg', 'c.jpg'].map(name => new File([new Uint8Array(10)], name, { type: 'image/jpeg' }))
    Object.defineProperty(input, 'files', { value: files, configurable: true })
    act(() => { input.dispatchEvent(new Event('change', { bubbles: true })) })
    await settle(2)
    expect(container.querySelectorAll('figure[data-media-kind="file"]').length).toBe(3)

    const draft = container.querySelector<HTMLButtonElement>('button[data-submit-intent="draft"]')
    expect(draft).not.toBeNull()
    // jsdom's click() does not pass the submitter; requestSubmit does, as the browser's click would.
    await act(async () => { draft!.form!.requestSubmit(draft!) })
    await settle(20)

    // The product was created, upload 1 went through, upload 2 failed and upload 3 was never tried.
    expect(fixture.calls.filter(call => call === 'POST /products')).toHaveLength(1)
    expect(fixture.calls.filter(call => call === 'POST /files/product-media')).toHaveLength(2)
    // The editor moved to the product's page: the two files not stored are still queued there, as unsaved changes.
    expect(where.path).toBe('/products/p9')
    expect(container.querySelectorAll('figure[data-media-kind="file"]').length).toBe(2)
    expect(unloadBlocked()).toBe(true)
    // They were handed over once: nothing is left waiting for the product.
    expect(takePendingCreateMedia('p9')).toBeUndefined()
  })
})

describe('pending create media', () => {
  it('maps a variant media refs to what the product page takes back', () => {
    const stored = new Map([['f1', '/api/v1/files/product-media/a1/content']])
    expect(pendingVariantMediaRefs(['url|https://cdn.example.com/x.jpg', 'file|f1', 'file|f2', 'file|f3'], stored, new Set(['f2']))).toEqual(['url|https://cdn.example.com/x.jpg', 'url|/api/v1/files/product-media/a1/content', 'file|f2'])
  })

  it('is taken once, and nothing is kept when nothing is left', () => {
    keepPendingCreateMedia('p1', { files: [], variantMediaRefs: {} })
    expect(takePendingCreateMedia('p1')).toBeUndefined()
    keepPendingCreateMedia('p2', { files: [{ id: 'f2' }], variantMediaRefs: { v1: ['file|f2'] } })
    expect(takePendingCreateMedia('p2')).toEqual({ files: [{ id: 'f2' }], variantMediaRefs: { v1: ['file|f2'] } })
    expect(takePendingCreateMedia('p2')).toBeUndefined()
  })
})
