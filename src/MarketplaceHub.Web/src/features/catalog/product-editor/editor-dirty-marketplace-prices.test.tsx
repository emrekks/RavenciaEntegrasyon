import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { settleUntilQuiet } from '../../../test/render'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// A store-specific price changed in "Pazaryerlerine Özel Fiyat" is an unsaved change of the editor.
const fixture = vi.hoisted(() => {
  const variant = { id: 'v1', version: 1, sku: 'RYP006-S', barcode: 'RYP0060001', modelCode: 'RYP006', optionSignature: '-', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: {}, attributes: [] }
  const state = { version: 1, holdWrites: false, description: 'x', requests: 0, patches: [] as Array<{ ifMatch: string | null; body: Record<string, unknown> }> }
  const product = () => ({ id: 'p1', version: state.version, title: 'Yonca Desen Elbise', description: state.description, brandId: 'b1', categoryId: 'c1', warrantyMonths: 0, status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant], primaryImageUrl: null, totalStock: 3, startingPrice: 90, currency: 'TRY', modelCode: 'RYP006', activePlatforms: [], mediaUrls: [], attributes: [], familyProductIds: ['p1'] })
  const connection = { id: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağaza', externalStoreId: '930112', status: 'ACTIVE', externalWritesEnabled: true }
  return { product, connection, state }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    fixture.state.requests++
    // A save that has not answered yet.
    if (fixture.state.holdWrites && init?.method && init.method !== 'GET') return new Promise(() => undefined)
    // A product write is checked against the version it names, as the server does (If-Match).
    if (path === '/products/p1' && init?.method === 'PATCH') {
      const ifMatch = new Headers(init.headers).get('If-Match')
      fixture.state.patches.push({ ifMatch, body: JSON.parse(String(init.body)) as Record<string, unknown> })
      if (ifMatch !== `"v${fixture.state.version}"`) throw new original.ApiRequestError('Sürüm değişti.', 412)
      fixture.state.version++
      return fixture.product()
    }
    if (path === '/products/p1') return fixture.product()
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return []
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/mappings/categories/')) return null
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => {
    fixture.state.requests++
    return {
      items: path.startsWith('/connections') ? [fixture.connection]
        : path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise' }]
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
  fixture.state.version = 1
  fixture.state.holdWrites = false
  fixture.state.description = 'x'
  fixture.state.patches = []
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

// W3: a short fixed wait for an intermediate state; otherwise until the editor and its requests are quiet.
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

async function setTrendyolOwnPrice() {
  const open = container.querySelector<HTMLButtonElement>('button.marketplace-pricing-open')
  expect(open).not.toBeNull()
  act(() => open!.click())
  const toggle = container.querySelector<HTMLInputElement>('input[aria-label="Trendyol Mağaza için özel fiyat"]')
  expect(toggle).not.toBeNull()
  act(() => toggle!.click())
  const fields = container.querySelectorAll<HTMLInputElement>('fieldset[aria-label="Trendyol Mağaza pazaryeri fiyatlandırması"] input[inputmode="decimal"]')
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

  it('after the stale banner, Kaydet names the version the form was filled from and does not overwrite the other change', async () => {
    await openEditor()
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Yeni Ad')
    await settle(2)
    // Another tab saved a new description meanwhile.
    fixture.state.description = 'Yeni kumaş: %100 pamuk'
    await bumpVersion()
    expect(container.querySelector('.product-stale-banner')).not.toBeNull()
    const form = container.querySelector<HTMLFormElement>('#product-creation-form')!
    const save = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
    await act(async () => { form.requestSubmit(save) })
    await settle()
    // The 412 is not retried at the newer version: its content differs from what the form was filled from.
    expect(fixture.state.patches.map(patch => patch.ifMatch)).toEqual(['"v1"'])
    expect(fixture.state.version).toBe(2)
    expect(fixture.product().description).toBe('Yeni kumaş: %100 pamuk')
    // The unsaved edit stays in the form.
    expect(container.querySelector<HTMLInputElement>('.product-title-field input')!.value).toBe('Yeni Ad')
  })

  it('the editor\'s own save moves the form on: the next Kaydet names the version that save made', async () => {
    await openEditor()
    const form = container.querySelector<HTMLFormElement>('#product-creation-form')!
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Yeni Ad')
    await act(async () => { form.requestSubmit(container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!) })
    await settle()
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Daha Yeni Ad')
    await settle(2)
    await act(async () => { form.requestSubmit(container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!) })
    await settle()
    expect(fixture.state.patches.map(patch => patch.ifMatch)).toEqual(['"v1"', '"v2"'])
    expect(container.querySelector('.product-stale-banner')).toBeNull()
  })

  it('locks the form while a save is pending, so nothing typed meanwhile is lost to the refill after the save', async () => {
    await openEditor()
    const form = container.querySelector<HTMLFormElement>('#product-creation-form')!
    expect(form.hasAttribute('inert')).toBe(false)
    setValue(container.querySelector<HTMLInputElement>('.product-title-field input')!, 'Yeni Ad')
    fixture.state.holdWrites = true
    const save = container.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')!
    await act(async () => { form.requestSubmit(save) })
    await settle(3)
    expect(form.hasAttribute('inert')).toBe(true)
    expect(form.getAttribute('aria-busy')).toBe('true')
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
