import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { orderMediaUrlsByVariants } from './publication-readiness'
import type { Variant } from '../shared'
import { thumbnailUrl } from '../../../shared/product-thumbnail'

// RYP007 (owner, 6 Nov 2026): "beden tablosu ekledim, eşleştirdim, kaydet dedim, yine kaydı sıralaması". One card with
// 6 colours × 3 sizes and 3 pictures per colour; a size chart added at the end of the strip, assigned to every variant
// with "Seçeneklere Görsel Ata" and saved. The strip came back green ×3, chart, brown ×3…: the card's own list was built
// colour by colour from the variants' pictures. The strip is the product-level order the API sends (mediaUrls:
// CatalogFamilyMedia.StripUrls), with or without a custom order.
const colours = ['Haki', 'Kahverengi', 'Kiremit', 'Lacivert', 'Siyah', 'Vizon']
const picture = (colour: number, index: number) => `https://cdn.dsmcdn.com/ryp007/${colours[colour].toLocaleLowerCase('tr-TR')}-${index + 1}.jpg`
const chart = 'https://cdn.dsmcdn.com/ryp007/beden-tablosu.jpg'
const stripOrder = [...colours.flatMap((_, colour) => [0, 1, 2].map(index => picture(colour, index))), chart]

const fixture = vi.hoisted(() => ({ hasCustomMediaOrder: false }))

function ryp007Variants(): Variant[] {
  return colours.flatMap((colour, colourIndex) => ['L', 'XL', '2XL'].map((size, sizeIndex) => ({
    id: `v${colourIndex * 3 + sizeIndex + 1}`, productId: 'p1', version: 1, sku: `RYP007-${String(colourIndex * 3 + sizeIndex + 1).padStart(2, '0')}`, barcode: '', modelCode: 'RYP007',
    optionSignature: `Renk: ${colour} | Beden: ${size}`, status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0,
    inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 10, vatInclusion: 'INCLUDED',
    roundingMode: null, safetyStock: 0,
    // After "Seçeneklere Görsel Ata": the colour's 3 pictures, then the chart (each variant's rows numbered 0-3).
    mediaUrls: [picture(colourIndex, 0), picture(colourIndex, 1), picture(colourIndex, 2), chart],
    options: { Renk: colour, Beden: size }, attributes: []
  }) as unknown as Variant))
}

describe('orderMediaUrlsByVariants', () => {
  it('keeps the API order, so a size chart every colour shares stays where the strip has it', () => {
    expect(orderMediaUrlsByVariants(ryp007Variants(), stripOrder, stripOrder[0], false)).toEqual(stripOrder)
    expect(orderMediaUrlsByVariants(ryp007Variants(), stripOrder, stripOrder[0], true)).toEqual(stripOrder)
  })

  it('puts a variant picture the API list lacks after it', () => {
    const variants = ryp007Variants()
    variants[3] = { ...variants[3], mediaUrls: [...(variants[3].mediaUrls ?? []), 'https://cdn.dsmcdn.com/ryp007/extra.jpg'] }
    expect(orderMediaUrlsByVariants(variants, stripOrder, null)).toEqual([...stripOrder, 'https://cdn.dsmcdn.com/ryp007/extra.jpg'])
  })
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string) => {
    if (path === '/products/p1') return {
      id: 'p1', version: 7, title: 'Kadın Triko Kazak', description: '', brandId: null, categoryId: null, status: 'ACTIVE', updatedAt: '2026-11-06T15:42:30Z',
      variants: ryp007Variants(), primaryImageUrl: stripOrder[0], totalStock: 0, startingPrice: 90, currency: 'TRY', modelCode: 'RYP007', activePlatforms: [],
      mediaUrls: [...stripOrder], familyMediaUrls: [...stripOrder],
      familyMediaItems: stripOrder.map((url, index) => ({ url, mediaIds: [`m${index + 1}`], sourceProductTitles: ['Kadın Triko Kazak'] })),
      familyOrderedMediaUrls: fixture.hasCustomMediaOrder ? [...stripOrder] : undefined,
      hasCustomMediaOrder: fixture.hasCustomMediaOrder,
      attributes: [], familyProductIds: ['p1']
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

const tileSources = () => Array.from(container.querySelectorAll<HTMLElement>('.media-preview-strip figure')).map(tile => tile.querySelector('img')?.getAttribute('src') ?? '')

describe('the edit page strip of a card with colour variants', () => {
  it.each([false, true])('shows the size chart last after it was assigned to every variant (custom order: %s)', async hasCustomMediaOrder => {
    fixture.hasCustomMediaOrder = hasCustomMediaOrder
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => {
      root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products/p1']}><main><Routes>
        <Route path="/products/:id" element={<ProductDetailPage />} />
      </Routes></main></MemoryRouter></QueryClientProvider>)
    })
    await settle()

    // The tiles show the small copies of the same photos.
    expect(tileSources()).toEqual(stripOrder.map(url => thumbnailUrl(url)))
  })
})
