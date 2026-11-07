import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Live 6 Nov 2026 (product 01a1a742…, 7 colours × 3 sizes, 19 new pictures): Kaydet on the product page. Every upload
// raises a realtime "product changed", so the editor read the product again after each one while the save still ran.
// Every stored copy showed in the strip beside its own local preview, one more per upload, until the variant writes
// hid them again. The API's mediaUrls is the card's strip: its product-level pictures, then those only variants hold
// (CatalogFamilyMedia.StripUrls), so each upload's stored copy is in it as soon as it lands.
const fixture = vi.hoisted(() => {
  const state = {
    calls: [] as string[],
    productMedia: [] as string[],
    variantMedia: [] as string[],
    version: 3,
    client: undefined as import('@tanstack/react-query').QueryClient | undefined
  }
  const variant = () => ({ id: 'v1', productId: 'p1', version: 1, sku: 'SKU-1', barcode: '', modelCode: 'M-1', optionSignature: 'Renk: Bej', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [...state.variantMedia], options: { Renk: 'Bej' }, attributes: [] })
  const product = () => ({
    id: 'p1', version: state.version, title: 'Ürün', description: '', brandId: null, categoryId: null, status: 'ACTIVE', updatedAt: '2026-11-06T15:00:00Z',
    variants: [variant()], primaryImageUrl: state.productMedia[0] ?? null, totalStock: 0, startingPrice: 90, currency: 'TRY', modelCode: 'M-1', activePlatforms: [],
    mediaUrls: [...new Set([...state.productMedia, ...state.variantMedia])],
    familyMediaUrls: [...state.productMedia],
    familyMediaItems: state.productMedia.map((url, index) => ({ url, mediaIds: [`m${index + 1}`], sourceProductTitles: ['Ürün'] })),
    attributes: [], familyProductIds: ['p1']
  })
  // The realtime hub's "product changed": the open editor reads the product again while the save goes on.
  const changed = () => { state.version += 1; void state.client?.invalidateQueries({ queryKey: ['product'] }) }
  return { state, product, changed }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const { state, product, changed } = fixture
    state.calls.push(`${init?.method ?? 'GET'} ${path}`)
    // Each answer takes a moment, an upload longer than a read (as on live), so the page renders between the save's steps.
    await new Promise(resolve => setTimeout(resolve, path.startsWith('/files/') ? 30 : 5))
    if (path === '/products' && init?.method === 'POST') return product()
    if (path === '/products/p1' && init?.method === 'PATCH') { state.version += 1; return product() }
    if (path === '/files/product-media' && init?.method === 'POST') {
      const assetId = `a${state.calls.filter(call => call === 'POST /files/product-media').length}`
      const url = `/api/v1/files/product-media/${assetId}/content`
      const data = init.body as FormData
      if (data.get('variantId')) state.variantMedia.push(url)
      else state.productMedia.push(url)
      changed()
      return { fileAssetId: assetId }
    }
    if (path === '/files/product-media-reconcile') {
      const body = JSON.parse(String(init?.body)) as { variantId: string | null; items: Array<{ url: string }> }
      if (body.variantId) state.variantMedia = body.items.map(item => item.url)
      else state.productMedia = body.items.map(item => item.url)
      changed()
      return null
    }
    if (path === '/products/p1') return product()
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
  fixture.state.calls.length = 0
  fixture.state.productMedia = []
  fixture.state.variantMedia = []
  fixture.state.version = 3
  let blob = 0
  URL.createObjectURL = vi.fn(() => `blob:${++blob}`)
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

const tiles = () => Array.from(container.querySelectorAll<HTMLElement>('.media-preview-strip figure'))
const tileSources = () => tiles().map(tile => tile.querySelector('img')?.getAttribute('src') ?? '')
const stored = (count: number) => Array.from({ length: count }, (_, index) => `/api/v1/files/product-media/a${index + 1}/content`)

function pickFiles(names: string[]) {
  const input = container.querySelector<HTMLInputElement>('input[aria-label="Ürün görsellerini dosya olarak seç"]')!
  const files = names.map(name => new File([new Uint8Array(10)], name, { type: 'image/jpeg' }))
  Object.defineProperty(input, 'files', { value: files, configurable: true })
  act(() => { input.dispatchEvent(new Event('change', { bubbles: true })) })
}

// Every tile count the strip showed during the save, with the page it was on. The product's page opened after a create
// is a new editor that shows no strip until the product is in.
function watchTiles() {
  const seen: Array<{ path: string; count: number }> = []
  const observer = new MutationObserver(() => {
    const count = tiles().length
    if (seen.at(-1)?.count !== count || seen.at(-1)?.path !== where.path) seen.push({ path: where.path, count })
  })
  observer.observe(container, { childList: true, subtree: true })
  return { counts: (path: string) => seen.filter(item => item.path === path).map(item => item.count), stop: () => observer.disconnect() }
}

function renderAt(path: string, client: QueryClient) {
  fixture.state.client = client
  return act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><Where /><main><Routes>
      <Route path="/products/new" element={<NewProductPage />} />
      <Route path="/products/:id" element={<ProductDetailPage />} />
    </Routes></main></MemoryRouter></QueryClientProvider>)
  })
}

describe('new pictures saved on a colour product page while the product is read again', () => {
  it('shows each picture once during and after the save, uploads each once, and keeps them on the page', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await renderAt('/products/p1', client)
    await settle()
    pickFiles(['a.jpg', 'b.jpg', 'c.jpg'])
    await settle(2)
    expect(tiles()).toHaveLength(3)

    const watch = watchTiles()
    const save = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
    await act(async () => { save.form!.requestSubmit(save) })
    await settle(30)
    watch.stop()

    expect(fixture.state.calls.filter(call => call === 'POST /files/product-media')).toHaveLength(3)
    // Never a stored copy beside its local preview (it was 4 after the second upload: a2 + three previews), and never a
    // half-empty strip between the save and the refill (it was a2, a3 only).
    expect(watch.counts('/products/p1').every(count => count === 3)).toBe(true)
    expect(where.path).toBe('/products/p1')
    expect(tileSources()).toEqual(stored(3))
    expect(container.querySelectorAll('figure[data-media-kind="file"]')).toHaveLength(0)
  })
})

describe('new pictures on a new product', () => {
  it('uploads each once and ends on the product page with the pictures shown once', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await renderAt('/products/new', client)
    await settle()
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Ürün')
    setValue(container.querySelector<HTMLInputElement>('input.sku-value')!, 'SKU-1')
    pickFiles(['a.jpg', 'b.jpg', 'c.jpg'])
    await settle(2)
    expect(tiles()).toHaveLength(3)

    const watch = watchTiles()
    const draft = container.querySelector<HTMLButtonElement>('button[data-submit-intent="draft"]')!
    await act(async () => { draft.form!.requestSubmit(draft) })
    await settle(30)
    watch.stop()

    expect(fixture.state.calls.filter(call => call === 'POST /products')).toHaveLength(1)
    expect(fixture.state.calls.filter(call => call === 'POST /files/product-media')).toHaveLength(3)
    // "Yeni Ürün Ekle" never empties its strip before it moves to the product's page (it showed the empty Görseller box).
    expect(watch.counts('/products/new').every(count => count === 3)).toBe(true)
    expect(where.path).toBe('/products/p1')
    expect(tileSources()).toEqual(stored(3))
  })
})
