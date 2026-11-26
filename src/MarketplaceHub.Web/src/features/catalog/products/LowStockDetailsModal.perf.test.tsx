import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Product } from '../shared'
import { LowStockDetailsModal } from './LowStockDetailsModal'

// Each model card reads its picture once per render: counting the reads counts the cards that rendered.
const pictureReads = vi.hoisted(() => ({ count: 0 }))
vi.mock('./product-group-image', async original => {
  const actual = await original<typeof import('./product-group-image')>()
  return { productGroupImage: (group: Parameters<typeof actual.productGroupImage>[0]) => { pictureReads.count++; return actual.productGroupImage(group) } }
})

// A live-sized list (26 Nov 2026: 114 models, 443 cards, 1808 variants): 130 models, 500 colour cards of 2-5 sizes.
const colours = ['Siyah', 'Beyaz', 'Bej', 'Kahverengi', 'Haki', 'Lacivert', 'Ekru', 'Gri']
const categories = ['Elbise', 'Bluz', 'Pantolon', 'Ceket']
function records(): Product[] {
  const items: Product[] = []
  for (let model = 0; items.length < 500; model++) {
    const code = `MZ${String(100 + model).padStart(3, '0')}`
    for (let colour = 0; colour < 3 + (model % 3) && items.length < 500; colour++) {
      const id = `${code}-${colour}`
      const name = colours[(model + colour) % colours.length]
      const sizes = ['S', 'M', 'L', 'XL', '2XL'].slice(0, 2 + ((model + colour) % 4))
      items.push({
        id, title: `Kadın ${categories[model % 4]} - ${name}`, description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 1,
        variants: sizes.map((size, index) => ({ id: `${id}-${size}`, sku: `${code}-${name}-${size}`, barcode: index % 2 ? `869${model}${colour}${index}` : null, modelCode: code, optionSignature: `Renk:${name}|Beden:${size}`, status: 'ACTIVE', version: 1, onHand: (model + colour + index) % 3 === 0 ? 2 : 0, available: 0, salePrice: 450, currency: 'TRY', mediaUrls: [], options: { Renk: name, Beden: size }, productId: id })),
        primaryImageUrl: `https://cdn.example.test/${id}.jpg`, familyImageUrl: null, totalStock: 0, startingPrice: 450, currency: 'TRY', modelCode: code, activePlatforms: [], platformStatuses: [],
        categoryPath: `Kadın > ${categories[model % 4]}`, hasCustomMediaOrder: false, familyKey: `model:b1:${code.toLowerCase()}`
      } as unknown as Product)
    }
  }
  return items
}

let container: HTMLDivElement
let root: Root
beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  Element.prototype.scrollIntoView = vi.fn()
  container = document.createElement('div')
  document.body.append(container)
})
afterEach(() => { act(() => root?.unmount()); container.remove() })

const cards = () => [...document.querySelectorAll<HTMLElement>('.rr-lowgrid > .rr-lowf')]
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
async function renderModal(products: Product[]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><LowStockDetailsModal products={products} loading={false} error={null} onClose={() => {}} onImageClick={() => {}} /></MemoryRouter></QueryClientProvider>) })
  await settle()
}
async function type(value: string) {
  const input = document.querySelector<HTMLInputElement>('input[aria-label="Stok eksiklerinde ara"]')!
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await settle()
}

describe('Düşük Stok window on a live-sized list', () => {
  it('shows the counts the records give: models, catalog records and sold-out variants, also after a search', async () => {
    const products = records()
    await renderModal(products)
    const models = new Set(products.map(product => product.modelCode)).size
    const soldOut = products.reduce((sum, product) => sum + product.variants.filter(variant => variant.onHand <= 0).length, 0)
    expect(document.querySelector('.rr-lead')!.textContent).toBe(`${models} model · 500 katalog kaydı · ${soldOut} eksik varyant`)
    expect(cards()).toHaveLength(models)
    // A colour name finds the cards of that colour only; a model's count is its matching cards' sold-out variants.
    await type('siyah')
    const black = products.filter(product => product.title.endsWith('Siyah'))
    expect(document.querySelector('.rr-lead')!.textContent).toBe(`${new Set(black.map(product => product.modelCode)).size} model · ${black.length} katalog kaydı · ${black.reduce((sum, product) => sum + product.variants.filter(variant => variant.onHand <= 0).length, 0)} eksik varyant`)
  })

  // Perf guard (counts, not time): the cards stay small, and opening, closing or narrowing renders only the cards it
  // changes, not every card of the list.
  it('keeps a closed card small and renders only the cards an action changes', async () => {
    await renderModal(records())
    const count = cards().length
    expect(document.querySelector('.rr-lowgrid')!.getElementsByTagName('*').length).toBeLessThanOrEqual(count * 14)
    expect(document.querySelectorAll('.rr-lowgrid section, .rr-lowgrid .rr-lowc')).toHaveLength(0)

    pictureReads.count = 0
    act(() => cards()[5].querySelector<HTMLButtonElement>('.rr-lowf-t')!.click())
    await settle()
    expect(cards()[5].classList.contains('rr-lowf-full')).toBe(true)
    expect(pictureReads.count).toBe(1)

    pictureReads.count = 0
    act(() => cards()[5].querySelector<HTMLButtonElement>('.rr-lowc-h')!.click())
    await settle()
    expect(cards()[5].querySelector('.rr-lowc.rr-open .rr-lowvs, .rr-lowc.rr-open .rr-lead')).not.toBeNull()
    expect(pictureReads.count).toBe(1)

    pictureReads.count = 0
    act(() => cards()[9].querySelector<HTMLButtonElement>('.rr-lowf-t')!.click())
    await settle()
    expect(cards().filter(card => card.classList.contains('rr-lowf-full'))).toHaveLength(1)
    expect(pictureReads.count).toBe(2)

    // A search that keeps whole models renders none of them again; the open model closes.
    pictureReads.count = 0
    await type('mz')
    expect(cards()).toHaveLength(count)
    expect(document.querySelector('.rr-lowf-full')).toBeNull()
    expect(pictureReads.count).toBe(1)
  })
})
