import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Owner, 6 Nov 2026: a card saved as a draft before its variants were generated holds one option-less "Tek Ürün"
// variant. Generating Beden S and M replaces that row (it does not stay at the bottom of the table) and the next save
// asks the server to remove the saved placeholder.
const fixture = vi.hoisted(() => {
  const placeholder = { id: 'v1', productId: 'p1', version: 1, sku: 'MZ070', barcode: '', modelCode: 'MZ070', optionSignature: '-', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 100, defaultListPrice: 100, defaultSalePrice: 100, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: {}, attributes: [], platformStatuses: [] }
  const product = { id: 'p1', version: 1, title: 'Taslak Elbise', description: '', brandId: null, categoryId: 'c1', status: 'DRAFT', updatedAt: '2026-11-06T10:00:00Z', variants: [placeholder], primaryImageUrl: null, totalStock: 0, startingPrice: 100, currency: 'TRY', modelCode: 'MZ070', activePlatforms: [], mediaUrls: [], attributes: [], familyProductIds: ['p1'], defaultListPrice: 100, defaultSalePrice: 100, vatRate: 20, vatInclusion: 'INCLUDED', deliveryDays: 2 }
  const beden = { attributeId: 'a-beden', isRequired: false, allowsCustomValue: false, displayOrder: 1, role: 'OPTION', attribute: { id: 'a-beden', version: 1, code: 'option-beden', name: 'Beden', dataType: 'SINGLE_SELECT', values: [{ id: 'b-s', value: 'S' }, { id: 'b-m', value: 'M' }], roles: ['OPTION'] } }
  const calls: Array<{ path: string; method: string; body: unknown }> = []
  const state = { keepPlaceholder: false }
  return { product, placeholder, beden, calls, state }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    let body: unknown = init?.body
    if (typeof body === 'string') { try { body = JSON.parse(body) } catch { /* keep */ } }
    fixture.calls.push({ path, method, body })
    if (method === 'PATCH' && path === '/products/p1') {
      const created = ((body as { variantsToCreate?: Array<{ sku: string; options: Record<string, string> }> }).variantsToCreate ?? [])
        .map((item, index) => ({ ...fixture.placeholder, id: `new-${index}`, sku: item.sku, optionSignature: `Beden:${item.options.Beden}`, options: item.options }))
      return { ...fixture.product, version: 2, variants: [...(fixture.state.keepPlaceholder ? [fixture.placeholder] : []), ...created] }
    }
    if (path === '/products/p1') return fixture.product
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return [fixture.beden]
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/mappings/categories/')) return null
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => ({
    items: path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise', depth: 0, version: 1 }] : [],
    nextCursor: null,
    hasMore: false
  })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { ToastStackProvider } from '../../../shared/components/ToastStack'
import { NewProductPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  fixture.calls.length = 0
  fixture.state.keepPlaceholder = false
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function waitFor(check: () => boolean) {
  for (let index = 0; index < 250 && !check(); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

const button = (text: string) => [...container.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.trim() === text)
const rowLabels = () => [...container.querySelectorAll<HTMLElement>('[data-variant-row-key]')].map(row => row.dataset.variantRowKey!)

async function generateSizes() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><ToastStackProvider><MemoryRouter><main><NewProductPage editProductId="p1" /></main></MemoryRouter></ToastStackProvider></QueryClientProvider>) })
  await waitFor(() => rowLabels().includes('v1') && Boolean(container.querySelector('.attribute-builder-disclosure')))
  expect(rowLabels()).toEqual(['v1'])
  await act(async () => { container.querySelector<HTMLButtonElement>('.attribute-builder-disclosure')!.click() })
  for (const size of ['S', 'M']) await act(async () => { button(size)!.click() })
  await waitFor(() => Boolean(button('Ürünleri Ekle')) && !button('Ürünleri Ekle')!.disabled)
  await act(async () => { button('Ürünleri Ekle')!.click() })
  await waitFor(() => rowLabels().length === 2)
  // Generated rows start without a stock code; the owner types them before saving.
  const skuInputs = [...container.querySelectorAll<HTMLInputElement>('input[aria-label$=" stok kodu"]')]
  expect(skuInputs).toHaveLength(2)
  skuInputs.forEach((input, index) => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
    act(() => { setter.call(input, `MZ070-${index + 1}`); input.dispatchEvent(new Event('input', { bubbles: true })) })
  })
}

async function saveDraft() {
  const draft = container.ownerDocument.querySelector<HTMLButtonElement>('button[data-submit-intent="draft"]')
  expect(draft, 'Taslak Olarak Kaydet').toBeTruthy()
  await act(async () => { draft!.click() })
  await waitFor(() => fixture.calls.some(call => call.method === 'PATCH' && call.path === '/products/p1'))
  for (let index = 0; index < 20; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  return fixture.calls.find(call => call.method === 'PATCH' && call.path === '/products/p1')!.body as { placeholderVariantIdsToDelete: string[]; variantIdsToDelete: string[]; variantsToCreate: Array<{ options: Record<string, string> }>; variantUpdates: Array<{ id: string }> }
}

describe('generating variants on a draft that holds the Tek Ürün placeholder', () => {
  it('replaces the Tek Ürün row and asks the save to remove the saved placeholder', async () => {
    await generateSizes()
    expect(rowLabels()).not.toContain('v1')
    expect(container.textContent).not.toMatch(/Tek Ürün varyantını sil/)

    const patch = await saveDraft()
    expect(patch.placeholderVariantIdsToDelete).toEqual(['v1'])
    expect(patch.variantIdsToDelete).toEqual([])
    expect(patch.variantUpdates.map(update => update.id)).not.toContain('v1')
    expect(patch.variantsToCreate.map(item => item.options.Beden).sort()).toEqual(['M', 'S'])
    expect(document.body.textContent).not.toContain('“Tek Ürün” varyantı silinmedi')
  }, 30000)

  it('says so in Turkish when the server keeps a placeholder that something is tied to', async () => {
    fixture.state.keepPlaceholder = true
    await generateSizes()
    await saveDraft()
    await waitFor(() => Boolean(document.body.textContent?.includes('“Tek Ürün” varyantı silinmedi')))
    expect(document.body.textContent).toContain('“Tek Ürün” varyantı silinmedi')
  }, 30000)
})
