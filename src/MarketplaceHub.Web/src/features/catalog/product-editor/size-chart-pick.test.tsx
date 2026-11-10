import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Variant } from '../shared'
import { thumbnailUrl } from '../../../shared/product-thumbnail'

// "Beden Tablosu Seç" beside Tümünü Temizle (owner, 10 Nov 2026: "bunu tümünü temizlenin yanına seçenek olarak sun,
// ordan tıklayıp o görseli seçelim"): the per-picture ⋮ menu is gone; the button turns on a pick mode in which a tap on
// one of the card's own pictures marks it as the size chart.
const pictures = ['a', 'b', 'c'].map(name => `https://cdn.dsmcdn.com/pick/${name}.jpg`)

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const variant = {
    id: 'v1', productId: 'p1', version: 1, sku: 'PICK-01', barcode: '', modelCode: 'PICK', optionSignature: 'Beden: M', status: 'ACTIVE', weight: null, width: null,
    height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY',
    offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 10, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [],
    options: { Beden: 'M' }, attributes: []
  } as unknown as Variant
  const respond = async (path: string) => {
    if (path === '/products/p1') return {
      id: 'p1', version: 3, title: 'Kazak', description: '', brandId: null, categoryId: null, status: 'ACTIVE', updatedAt: '2026-11-10T10:00:00Z',
      variants: [variant], primaryImageUrl: pictures[0], totalStock: 0, startingPrice: 90, currency: 'TRY', modelCode: 'PICK', activePlatforms: [],
      mediaUrls: [...pictures], familyMediaUrls: [...pictures],
      familyMediaItems: pictures.map((url, index) => ({ url, mediaIds: [`m${index + 1}`], sourceProductTitles: ['Kazak'] })),
      hasCustomMediaOrder: false, attributes: [], familyProductIds: ['p1']
    }
    if (path.includes('/capabilities')) return []
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

const button = (label: string) => Array.from(container.querySelectorAll<HTMLButtonElement>('.product-media-header-actions button')).find(item => item.textContent === label)
const hint = () => container.querySelector('.size-chart-pick-hint')
const tiles = () => Array.from(container.querySelectorAll<HTMLElement>('.media-preview-strip figure'))
const tileSource = (tile: HTMLElement) => tile.querySelector('img')?.getAttribute('src')
const click = async (element: Element | null | undefined) => { await act(async () => { (element as HTMLElement).click() }) }

async function renderEditor() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products/p1']}><main><Routes>
      <Route path="/products/:id" element={<ProductDetailPage />} />
    </Routes></main></MemoryRouter></QueryClientProvider>)
  })
  await settle()
}

describe('Beden Tablosu Seç', () => {
  it('has no per-picture menu; the button sits beside Tümünü Temizle', async () => {
    await renderEditor()
    expect(tiles()).toHaveLength(3)
    expect(container.querySelector('.size-chart-menu-btn, .size-chart-menu')).toBeNull()
    expect(Array.from(container.querySelectorAll('.product-media-header-actions button')).map(item => item.textContent)).toEqual(['Beden Tablosu Seç', 'Tümünü Temizle'])
  })

  it('enters pick mode and leaves it with Escape or Vazgeç without marking anything', async () => {
    await renderEditor()
    await click(button('Beden Tablosu Seç'))
    expect(hint()?.textContent).toContain('Beden tablosu olacak görsele dokunun')
    expect(container.querySelector('.media-preview-strip.is-size-chart-picking')).not.toBeNull()
    expect(container.querySelectorAll('.size-chart-pick-target')).toHaveLength(3)
    expect(tiles().every(tile => tile.getAttribute('draggable') === 'false')).toBe(true)

    await act(async () => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })) })
    expect(hint()).toBeNull()
    expect(container.querySelector('.size-chart-pick-target')).toBeNull()

    await click(button('Beden Tablosu Seç'))
    await click(Array.from(hint()?.querySelectorAll('button') ?? []).find(item => item.textContent === 'Vazgeç'))
    expect(hint()).toBeNull()
    expect(container.querySelector('[data-size-chart]')).toBeNull()
    expect(button('İşareti Kaldır')).toBeUndefined()
  })

  it('marks the tapped picture, pins it last, leaves pick mode, and İşareti Kaldır takes the mark off', async () => {
    await renderEditor()
    await click(button('Beden Tablosu Seç'))
    await click(tiles()[0].querySelector('.size-chart-pick-target'))
    expect(hint()).toBeNull()
    const last = tiles().at(-1)!
    expect(tileSource(last)).toBe(thumbnailUrl(pictures[0]))
    expect(last.dataset.sizeChart).toBe('true')
    expect(last.querySelector('.size-chart-badge')?.textContent).toBe('Beden Tablosu')

    // Seçeneklere Görsel Ata lists the same pictures in the strip's order, chart last, numbered in that order.
    const openPicker = Array.from(container.querySelectorAll('button')).find(item => item.textContent?.includes('Seçeneklere Görsel Ata'))
    expect(openPicker).toBeDefined()
    await click(openPicker)
    const options = Array.from(document.querySelectorAll<HTMLElement>('.variant-media-picker-grid .variant-media-option'))
    expect(options.map(option => option.querySelector('img')?.getAttribute('src'))).toEqual([pictures[1], pictures[2], pictures[0]])
    expect(options.map(option => option.getAttribute('aria-label'))).toEqual(['Görsel 01', 'Görsel 02', 'Görsel 03'])
    await click(Array.from(document.querySelectorAll('.variant-media-picker-modal footer button')).find(item => item.textContent === 'Vazgeç'))

    await click(button('İşareti Kaldır'))
    expect(container.querySelector('[data-size-chart]')).toBeNull()
    expect(container.querySelector('.size-chart-badge')).toBeNull()
    expect(button('İşareti Kaldır')).toBeUndefined()
  })
})
