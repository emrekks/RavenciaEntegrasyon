import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter, Route, Routes } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Owner, 10-11 Nov 2026: on a card with variants Liste/Satış Fiyatı are open ("Kilidi kaldır") and Stok Kodu/Barkod read-only; the
// editor's required fields are marked and stop a save of a card on sale, and a new card with one empty is kept as a draft.
const fixture = vi.hoisted(() => {
  const state = { calls: [] as Array<{ method: string; path: string; body: unknown }>, brandId: 'b1' as string | null, gate: null as Promise<void> | null }
  const variant = (id: string, sku: string, colour: string) => ({ id, productId: 'p1', version: 1, sku, barcode: `${sku}-B`, modelCode: 'M-1', optionSignature: `Renk: ${colour}`, status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, defaultListPrice: 100, defaultSalePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: { Renk: colour }, attributes: [] })
  const product = () => ({
    id: 'p1', version: 3, title: 'Ürün', description: 'Açıklama', brandId: state.brandId, categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-10T15:00:00Z',
    variants: [variant('v1', 'SKU-1', 'Bej'), variant('v2', 'SKU-2', 'Mor')], primaryImageUrl: null, totalStock: 0, startingPrice: 90, currency: 'TRY', modelCode: 'M-1', activePlatforms: [],
    defaultListPrice: 120, defaultSalePrice: 99.9, warrantyMonths: 0, deliveryDays: 1, vatRate: 20, vatInclusion: 'INCLUDED',
    mediaUrls: [], familyMediaUrls: [], familyMediaItems: [], attributes: [], familyProductIds: ['p1']
  })
  return { state, product }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    fixture.state.calls.push({ method: init?.method ?? 'GET', path, body: init?.body ? JSON.parse(String(init.body)) : null })
    if (path === '/products' && init?.method === 'POST') return { ...fixture.product(), status: 'DRAFT' }
    if (path === '/products/p1' && init?.method === 'PATCH') return fixture.product()
    if (path === '/products/p1') { if (fixture.state.gate) await fixture.state.gate; return fixture.product() }
    if (path.includes('/capabilities') || path.includes('/attribute-requirements')) return []
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
  fixture.state.calls.length = 0
  fixture.state.brandId = 'b1'
  fixture.state.gate = null
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

async function renderAt(path: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><main><Routes>
      <Route path="/products/new" element={<NewProductPage />} />
      <Route path="/products/:id" element={<ProductDetailPage />} />
    </Routes></main></MemoryRouter></QueryClientProvider>)
  })
  await settle()
}

const labelled = (text: string) => Array.from(container.querySelectorAll('label')).find(label => label.textContent?.startsWith(text))!
const inputOf = (text: string) => labelled(text).querySelector('input')!
// The taken-codes check is a POST that only reads; it fires 400 ms after the table settles, which a slow run reaches.
const writes = () => fixture.state.calls.filter(call => call.method !== 'GET' && call.path !== '/catalog/variant-codes/taken')

describe('the editor while the card loads', () => {
  // Owner, 11 Nov 2026: the required fields flashed red on open, before the card's data was in the form.
  it('frames no field red before the data arrives, and a really empty one after', async () => {
    fixture.state.brandId = null
    let release = () => undefined as void
    fixture.state.gate = new Promise<void>(resolve => { release = resolve })
    await renderAt('/products/p1')
    expect(container.querySelector('input')).not.toBeNull()
    expect(container.querySelectorAll('[aria-invalid="true"], .is-invalid')).toHaveLength(0)
    release()
    await settle()
    expect(container.querySelector('#product-editor-brand')!.closest('.pm-select')!.classList.contains('is-invalid')).toBe(true)
    expect(inputOf('Ürün Adı').getAttribute('aria-invalid')).toBeNull()
  })
})

describe('a card with variants in the editor', () => {
  it('opens Liste/Satış Fiyatı with the card values, shows the first variant codes read-only and marks the required fields', async () => {
    await renderAt('/products/p1')
    for (const name of ['Liste Fiyatı', 'Satış Fiyatı']) {
      expect(inputOf(name).disabled).toBe(false)
      expect(inputOf(name).readOnly).toBe(false)
      expect(labelled(name).querySelector('.required-marker')).not.toBeNull()
    }
    expect(inputOf('Liste Fiyatı').value).toBe('120')
    expect(inputOf('Satış Fiyatı').value).toBe('99.9')
    // Owner, 11 Nov 2026 ("Salt okunur kalsın"): on a card with variants the codes are read-only (selectable) and not required.
    for (const [name, value] of [['Stok Kodu', 'SKU-1'], ['Barkod', 'SKU-1-B']]) {
      expect(inputOf(name)).toMatchObject({ readOnly: true, disabled: false, value, title: 'Varyantlı üründe kodlar varyant tablosunda düzenlenir.' })
      expect(labelled(name).querySelector('.required-marker')).toBeNull()
    }
    for (const name of ['Ürün Adı', 'Marka', 'Panel Kategorisi', 'Model Kodu', 'Desi', 'Garanti Süresi', 'Teslimat Süresi']) expect(labelled(name).querySelector('.required-marker')).not.toBeNull()

    // A typed decimal comma is the card's price on Kaydet; no variant row price changes.
    setValue(inputOf('Satış Fiyatı'), '89,90')
    const save = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
    await act(async () => { save.form!.requestSubmit(save) })
    await settle(20)
    const patch = writes().find(call => call.method === 'PATCH')!.body as { defaultSalePrice: number; defaultListPrice: number; status: string; variantUpdates: Array<{ defaultSalePrice: number }> }
    expect(patch).toMatchObject({ defaultSalePrice: 89.9, defaultListPrice: 120, status: 'ACTIVE' })
    expect(patch.variantUpdates.map(row => row.defaultSalePrice)).toEqual([90, 90])
    expect(writes().filter(call => call.path.includes('offer') || call.path.includes('publication'))).toEqual([])
  })

  it('stops Kaydet of a card on sale while a required field is empty or a price is 0, naming the field', async () => {
    fixture.state.brandId = null
    await renderAt('/products/p1')
    expect(container.querySelector('#product-editor-brand')!.closest('.pm-select')!.classList.contains('is-invalid')).toBe(true)
    setValue(inputOf('Liste Fiyatı'), '0')
    const save = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
    await act(async () => { save.form!.requestSubmit(save) })
    await settle(5)
    expect(writes()).toEqual([])
    expect(labelled('Marka').querySelector('.product-field-error')?.textContent).toBe('Marka zorunludur.')
    expect(labelled('Liste Fiyatı').querySelector('.product-field-error')?.textContent).toBe("Liste Fiyatı 0'dan büyük olmalı.")
    expect(inputOf('Liste Fiyatı').getAttribute('aria-invalid')).toBe('true')

    // Yayınlamaya Devam Et stays on the first step too.
    act(() => { container.querySelector<HTMLButtonElement>('button[aria-label="Yayınlamaya Devam Et"]')!.click() })
    expect(container.querySelector('[data-wizard-step]')!.getAttribute('data-wizard-step')).toBe('1')
  })
})

describe('a new card with required fields empty', () => {
  it('names what is missing, keeps Ürünü Oluştur off and saves a draft by Taslak, never sent', async () => {
    await renderAt('/products/new')
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Yeni Ürün')
    setValue(inputOf('Stok Kodu'), 'NEW-1')
    const create = container.querySelector<HTMLButtonElement>('button[data-submit-intent="create"]')!
    expect(create.disabled).toBe(true)
    expect(container.querySelector('.product-submit-sticky strong')!.textContent).toMatch(/zorunlu alan eksik/)
    const draft = container.querySelector<HTMLButtonElement>('button[data-submit-intent="draft"]')!
    expect(draft.disabled).toBe(false)
    await act(async () => { draft.form!.requestSubmit(draft) })
    await settle(20)
    const post = writes().find(call => call.method === 'POST' && call.path === '/products')!
    expect((post.body as { status: string }).status).toBe('DRAFT')
    expect(writes().filter(call => call.path.includes('publication'))).toEqual([])
  })
})
