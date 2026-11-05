import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// A store-specific price changed in "Pazaryerlerine Özel Fiyat" is an unsaved change of the editor.
const fixture = vi.hoisted(() => {
  const variant = { id: 'v1', version: 1, sku: 'RYP006-S', barcode: 'RYP0060001', modelCode: 'RYP006', optionSignature: '-', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: {}, attributes: [] }
  const state = { version: 1 }
  const product = () => ({ id: 'p1', version: state.version, title: 'Yonca Desen Elbise', description: 'x', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant], primaryImageUrl: null, totalStock: 3, startingPrice: 90, currency: 'TRY', modelCode: 'RYP006', activePlatforms: [], mediaUrls: [], attributes: [], familyProductIds: ['p1'] })
  const connection = { id: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağaza', externalStoreId: '930112', status: 'ACTIVE', externalWritesEnabled: true }
  return { product, connection, state }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string) => {
    if (path === '/products/p1') return fixture.product()
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return []
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/mappings/categories/')) return null
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => ({
    items: path.startsWith('/connections') ? [fixture.connection]
      : path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise' }]
        : path.startsWith('/catalog/brands') ? [{ id: 'b1', name: 'Ravencia', isActive: true }]
          : [],
    nextCursor: null,
    hasMore: false
  })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { NewProductPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(() => {
  fixture.state.version = 1
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

async function openEditor() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" /></main></MemoryRouter></QueryClientProvider>) })
  await settle()
}

async function setTrendyolOwnPrice() {
  const open = container.querySelector<HTMLButtonElement>('button.marketplace-pricing-open')
  expect(open).not.toBeNull()
  act(() => open!.click())
  const toggle = container.querySelector<HTMLInputElement>('input[aria-label="Trendyol Mağaza için özel fiyat"]')
  expect(toggle).not.toBeNull()
  act(() => toggle!.click())
  const fields = container.querySelectorAll<HTMLInputElement>('fieldset[aria-label="Trendyol Mağaza pazaryeri fiyatlandırması"] input[type="number"]')
  setValue(fields[0], '150')
  setValue(fields[1], '120')
  await settle(2)
  expect(container.querySelector<HTMLInputElement>('input[aria-label="Trendyol Mağaza için özel fiyat"]')!.checked).toBe(true)
  expect(fields[1].value).toBe('120')
}

async function bumpVersion() {
  fixture.state.version = 2
  await act(async () => { await client.invalidateQueries({ queryKey: ['product', 'p1'] }) })
  await settle()
}

describe('editor dirty state with marketplace prices', () => {
  it('a freshly opened editor is clean; a title edit warns on unload and survives a newer version (stale banner)', async () => {
    await openEditor()
    expect(unloadBlocked()).toBe(false)
    const title = container.querySelector<HTMLInputElement>('.product-title-field input')!
    setValue(title, 'Yeni Ad')
    await settle(2)
    expect(unloadBlocked()).toBe(true)
    await bumpVersion()
    expect(container.querySelector('.product-stale-banner')).not.toBeNull()
    expect(container.querySelector<HTMLInputElement>('.product-title-field input')!.value).toBe('Yeni Ad')
  })

  it('a store-specific price change alone warns on unload and survives a newer version', async () => {
    await openEditor()
    expect(unloadBlocked()).toBe(false)
    await setTrendyolOwnPrice()
    expect(unloadBlocked()).toBe(true)
    // A newer product version shows the stale banner instead of reseeding the form: the own price stays.
    await bumpVersion()
    expect(container.querySelector('.product-stale-banner')).not.toBeNull()
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Trendyol Mağaza için özel fiyat"]')!.checked).toBe(true)
  })
})
