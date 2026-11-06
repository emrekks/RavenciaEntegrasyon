import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// W3 / K3 (audit 2026-10-24): a new product whose Maliyet was never typed is created without a cost (null), not with a
// cost of 0. A 0 cost counts as entered in the cost summary and the "missing cost" template, but the profit report
// leaves it out, so a default 0 silently hid the variant from both.
const fixture = vi.hoisted(() => {
  const variant = { id: 'v7', version: 1, sku: 'NEW-7', barcode: '', modelCode: '', optionSignature: '-', status: 'DRAFT', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 0, salePrice: 0, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: {}, attributes: [] }
  const product = { id: 'p7', version: 1, title: 'Maliyetsiz Ürün', description: '', brandId: null, categoryId: null, status: 'DRAFT', updatedAt: '2026-11-06T10:00:00Z', variants: [variant], primaryImageUrl: null, totalStock: 0, startingPrice: 0, currency: 'TRY', modelCode: '', activePlatforms: [], mediaUrls: [], attributes: [], familyProductIds: ['p7'] }
  return { product, created: [] as unknown[] }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    if (path === '/products' && init?.method === 'POST') { fixture.created.push(JSON.parse(String(init.body))); return fixture.product }
    if (path === '/products/p7') return fixture.product
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

beforeEach(() => {
  fixture.created.length = 0
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

describe('new product cost', () => {
  it('starts with an empty Maliyet and creates the variant without a cost when none was typed', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => {
      root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products/new']}><main><Routes>
        <Route path="/products/new" element={<NewProductPage />} />
        <Route path="/products/:id" element={<ProductDetailPage />} />
      </Routes></main></MemoryRouter></QueryClientProvider>)
    })
    await settle()
    const cost = [...container.querySelectorAll('label')].find(label => label.firstChild?.textContent === 'Maliyet')?.querySelector('input')
    expect(cost?.value).toBe('')
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Maliyetsiz Ürün')
    setValue(container.querySelector<HTMLInputElement>('input.sku-value')!, 'NEW-7')

    const draft = container.querySelector<HTMLButtonElement>('button[data-submit-intent="draft"]')!
    await act(async () => { draft.form!.requestSubmit(draft) })
    await settle(20)

    expect(fixture.created).toHaveLength(1)
    const body = fixture.created[0] as { variants: Array<{ costPrice: number | null }> }
    expect(body.variants.map(variant => variant.costPrice)).toEqual([null])
  })
})
