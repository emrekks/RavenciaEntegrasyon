import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { settleUntilQuiet } from '../../../test/render'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Pins what the editor does around its seeding and saving (WEB-SAYFALAR-10, written before the page was split): the
// Web Color and option seeding after the form is filled, which refetch refills the form, what Kaydet sends after a
// variant edit and in which order, and a saved picture's removal on a form with other edits.
const fixture = vi.hoisted(() => {
  const state = {
    version: 1,
    title: 'Yonca Desen Elbise',
    mediaUrls: ['https://cdn.example.com/a.jpg', 'https://cdn.example.com/b.jpg'],
    onHand: { v1: 3, v2: 5 } as Record<string, number>,
    cost: { v1: 25, v2: null } as Record<string, number | null>,
    requests: 0,
    writes: [] as Array<{ method: string; path: string; ifMatch: string | null; body: unknown }>
  }
  const variant = (id: string, colour: string, sku: string) => ({ id, version: 1, productId: 'p1', sku, barcode: `${sku}-B`, modelCode: 'RYP006', optionSignature: `Renk:${colour}`, status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: state.cost[id], onHand: state.onHand[id], available: state.onHand[id], inventoryVersion: state.onHand[id], offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: { Renk: colour }, attributes: [] })
  const product = () => ({ id: 'p1', version: state.version, title: state.title, description: 'x', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant('v1', 'Siyah', 'RYP006-SY'), variant('v2', 'Bordo', 'RYP006-BD')], primaryImageUrl: state.mediaUrls[0] ?? null, totalStock: 8, startingPrice: 90, currency: 'TRY', modelCode: 'RYP006', activePlatforms: [], mediaUrls: state.mediaUrls, attributes: [], familyProductIds: ['p1'], trendyolWebColorValueId: 'wc-siyah', warrantyMonths: 0, deliveryDays: 1, vatRate: 20, vatInclusion: 'INCLUDED' })
  const requirement = (attributeId: string, name: string, role: 'OPTION' | 'ATTRIBUTE', values: Array<{ id: string; value: string }>, isWebColor = false) => ({ attributeId, isRequired: false, allowsCustomValue: false, displayOrder: role === 'OPTION' ? 1 : 2, role, isWebColor, attribute: { id: attributeId, version: 1, code: name.toUpperCase(), name, dataType: 'SINGLE_SELECT', values } })
  const requirements = [
    requirement('a-renk', 'Renk', 'OPTION', [{ id: 'r-siyah', value: 'Siyah' }, { id: 'r-bordo', value: 'Bordo' }]),
    requirement('a-wc', 'Web Color', 'ATTRIBUTE', [{ id: 'wc-siyah', value: 'Siyah' }, { id: 'wc-bordo', value: 'Bordo' }], true)
  ]
  return { product, requirements, state }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    fixture.state.requests++
    const method = init?.method ?? 'GET'
    // The taken-codes check is a read sent as POST (debounced while typing): not a write of the editor.
    if (method !== 'GET' && !path.startsWith('/products/p1/publication-status') && !path.startsWith('/catalog/variant-codes/taken')) {
      fixture.state.writes.push({ method, path, ifMatch: new Headers(init?.headers).get('If-Match'), body: init?.body ? JSON.parse(String(init.body)) : null })
    }
    if (path === '/products/p1' && method === 'PATCH') {
      const ifMatch = new Headers(init?.headers).get('If-Match')
      if (ifMatch !== `"v${fixture.state.version}"`) throw new original.ApiRequestError('Sürüm değişti.', 412)
      const body = JSON.parse(String(init?.body)) as { title: string; variantUpdates: Array<{ id: string; costPrice: number | null }> }
      fixture.state.title = body.title
      for (const update of body.variantUpdates) if (update.costPrice != null) fixture.state.cost[update.id] = update.costPrice
      fixture.state.version++
      return fixture.product()
    }
    if (path.startsWith('/inventory/') && method === 'PUT') {
      const variantId = path.split('/')[2]
      fixture.state.onHand[variantId] = (JSON.parse(String(init?.body)) as { onHand: number }).onHand
      return {}
    }
    if (path === '/files/product-media-item' && method === 'DELETE') {
      const { url } = JSON.parse(String(init?.body)) as { url: string }
      fixture.state.mediaUrls = fixture.state.mediaUrls.filter(item => item !== url)
      fixture.state.version++
      return { deletedCount: 1, version: fixture.state.version, productVersions: { p1: fixture.state.version } }
    }
    if (path === '/products/p1') return fixture.product()
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return fixture.requirements
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/catalog/variant-codes/taken')) return { items: [] }
    return null
  }
  const pages = async (path: string) => {
    fixture.state.requests++
    return {
      items: path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise' }]
        : path.startsWith('/catalog/brands') ? [{ id: 'b1', name: 'Ravencia', isActive: true }]
          : [],
      nextCursor: null,
      hasMore: false
    }
  }
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { NewProductPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(() => {
  Object.assign(fixture.state, { version: 1, title: 'Yonca Desen Elbise', mediaUrls: ['https://cdn.example.com/a.jpg', 'https://cdn.example.com/b.jpg'], onHand: { v1: 3, v2: 5 }, cost: { v1: 25, v2: null }, writes: [] })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function settle(rounds?: number) {
  if (rounds === undefined) return settleUntilQuiet(() => fixture.state.requests)
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

async function openEditor() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" /></main></MemoryRouter></QueryClientProvider>) })
  await settle()
}

async function save() {
  const form = container.querySelector<HTMLFormElement>('#product-creation-form')!
  await act(async () => { form.requestSubmit(container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!) })
  await settle()
}

const titleInput = () => container.querySelector<HTMLInputElement>('.product-title-field input')!
const writes = () => fixture.state.writes.map(write => `${write.method} ${write.path}`)

describe('editor seeding and saving (characterization)', () => {
  it('opens clean with the saved manual Web Color and the variant options inferred from the rows', async () => {
    await openEditor()
    expect(unloadBlocked()).toBe(false)
    // The saved manual Web Color turns the automatic switch off and shows its value.
    expect(container.querySelector<HTMLInputElement>('.attribute-builder-web-color-toggle input')!.checked).toBe(false)
    // The Renk values the rows hold are selected (option inference after the seed).
    expect(container.querySelector('.attribute-builder-disclosure-meta small')!.textContent).toBe('2 / 2 Seçili')
    expect([...container.querySelectorAll('.variant-option-text')].map(item => item.textContent)).toEqual(['Bordo', 'Siyah'])
    expect(container.querySelector('.product-stale-banner')).toBeNull()
  })

  it('refills a clean form from a newer version without a banner', async () => {
    await openEditor()
    fixture.state.title = 'Başka Sekmede Değişti'
    fixture.state.version = 2
    await act(async () => { await client.invalidateQueries({ queryKey: ['product', 'p1'] }) })
    await settle()
    expect(titleInput().value).toBe('Başka Sekmede Değişti')
    expect(container.querySelector('.product-stale-banner')).toBeNull()
    expect(unloadBlocked()).toBe(false)
  })

  it('Kaydet after a variant cost and stock edit sends the row against what it was loaded with, then the stock, and ends clean', async () => {
    await openEditor()
    setValue(container.querySelector<HTMLInputElement>('input[aria-label="Renk:Siyah maliyeti"]')!, '40')
    setValue(container.querySelector<HTMLInputElement>('input[aria-label="Renk:Siyah stok"]')!, '7')
    await settle(2)
    expect(unloadBlocked()).toBe(true)
    await save()
    expect(writes()).toEqual(['PATCH /products/p1', 'PUT /inventory/v1/on-hand'])
    const patch = fixture.state.writes[0]
    expect(patch.ifMatch).toBe('"v1"')
    const body = patch.body as { trendyolWebColorValueId: string; loadedFamilyVersions: unknown; variantIdsToDelete: string[]; variantsToCreate: unknown[]; variantUpdates: Array<Record<string, unknown>> }
    expect(body.trendyolWebColorValueId).toBe('wc-siyah')
    expect(body.variantIdsToDelete).toEqual([])
    expect(body.variantsToCreate).toEqual([])
    const siyah = body.variantUpdates.find(update => update.id === 'v1')!
    const bordo = body.variantUpdates.find(update => update.id === 'v2')!
    expect(siyah).toMatchObject({ costPrice: 40, loadedCostPrice: 25, loadedVersion: 1, defaultListPrice: 100, defaultSalePrice: 90, options: { Renk: 'Siyah' } })
    expect(bordo).toMatchObject({ costPrice: null, loadedVersion: 1 })
    expect(bordo).not.toHaveProperty('loadedCostPrice')
    expect(body.variantUpdates.map(update => update.sortOrder)).toEqual([0, 1])
    expect(fixture.state.writes[1].body).toMatchObject({ onHand: 7, expectedOnHand: 3 })
    // The refill after the editor's own save: the saved values show and the form is clean, with no banner.
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Renk:Siyah stok"]')!.value).toBe('7')
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Renk:Siyah maliyeti"]')!.value).toBe('40')
    expect(unloadBlocked()).toBe(false)
    expect(container.querySelector('.product-stale-banner')).toBeNull()
  })

  it('a saved picture removed on a form with other edits keeps them, and the next Kaydet names the version the removal left', async () => {
    await openEditor()
    setValue(titleInput(), 'Yeni Ad')
    await settle(2)
    expect(unloadBlocked()).toBe(true)
    const remove =container.querySelector<HTMLButtonElement>('button.image-remove-btn[aria-label="Görseli modelin tüm renklerinden hemen kaldır"]')!
    await act(async () => { remove.click() })
    await settle()
    expect(writes()).toEqual(['DELETE /files/product-media-item'])
    expect(fixture.state.writes[0].ifMatch).toBe('"v1"')
    expect(titleInput().value).toBe('Yeni Ad')
    expect(container.querySelector('.product-stale-banner')).toBeNull()
    // Found while writing this test (kept as it is by the split, reported): the new version re-runs the option
    // inference of the seed, which retakes the snapshot, so the form reads clean although the title edit is unsaved.
    expect(unloadBlocked()).toBe(false)
    await save()
    const patch = fixture.state.writes.find(write => write.method === 'PATCH')!
    expect(patch.ifMatch).toBe('"v2"')
    expect((patch.body as { title: string }).title).toBe('Yeni Ad')
  })
})
