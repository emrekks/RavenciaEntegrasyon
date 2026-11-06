import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Owner, 6 Nov 2026: "Tek Ürün var ... silinmiyor". The product MZ071 held its old option-less "Tek Ürün" variant beside
// the generated ones, and the row's trash button only took it off the table: Kaydet never deleted it, so it came back.
// A saved row now asks first, the save sends its id in variantIdsToDelete, and a refusal puts the row back with the
// server's reason.
const fixture = vi.hoisted(() => {
  const base = { productId: 'p1', version: 1, barcode: '', modelCode: 'MZ071', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 0, available: 0, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 100, defaultListPrice: 100, defaultSalePrice: 100, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], attributes: [], platformStatuses: [] }
  const placeholder = { ...base, id: 'v1', sku: 'MZ070', modelCode: 'MZ070', optionSignature: '-', options: {} }
  const small = { ...base, id: 'v2', sku: 'MZ071-01', optionSignature: 'Beden: S', options: { Beden: 'S' } }
  const product = { id: 'p1', version: 1, title: 'Kaşkorse Anne Takım', description: '', brandId: null, categoryId: 'c1', status: 'DRAFT', updatedAt: '2026-11-06T10:00:00Z', variants: [placeholder, small], primaryImageUrl: null, totalStock: 0, startingPrice: 100, currency: 'TRY', modelCode: 'MZ071', activePlatforms: [], mediaUrls: [], attributes: [], familyProductIds: ['p1'], defaultListPrice: 100, defaultSalePrice: 100, vatRate: 20, vatInclusion: 'INCLUDED', deliveryDays: 2 }
  const beden = { attributeId: 'a-beden', isRequired: false, allowsCustomValue: false, displayOrder: 1, role: 'OPTION', attribute: { id: 'a-beden', version: 1, code: 'option-beden', name: 'Beden', dataType: 'SINGLE_SELECT', values: [{ id: 'b-s', value: 'S' }], roles: ['OPTION'] } }
  const calls: Array<{ path: string; method: string; body: unknown }> = []
  const state = { refuse: false }
  return { product, small, beden, calls, state }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    let body: unknown = init?.body
    if (typeof body === 'string') { try { body = JSON.parse(body) } catch { /* keep */ } }
    fixture.calls.push({ path, method, body })
    if (method === 'PATCH' && path === '/products/p1') {
      if (fixture.state.refuse) throw new original.ApiRequestError('Varyant sipariş stok hareketine bağlı olduğu için silinemiyor. Stok geçmişini korumak için ürünü arşivleyin.', 409, 'VARIANT_DELETE_BLOCKED')
      return { ...fixture.product, version: 2, variants: [fixture.small] }
    }
    if (path === '/products/p1') return fixture.product
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return [fixture.beden]
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/mappings/categories/')) return null
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => ({
    items: path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Takım', isLeaf: true, isActive: true, path: 'Takım', depth: 0, version: 1 }] : [],
    nextCursor: null,
    hasMore: false
  })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { ToastStackProvider } from '../../../shared/components/ToastStack'
import { NewProductPage } from './ProductEditorPages'
import { restoreRemovedVariantRows, variantRemovalConfirmText, variantRemovalTies } from '../variant-row-removal'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  fixture.calls.length = 0
  fixture.state.refuse = false
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function waitFor(check: () => boolean) {
  for (let index = 0; index < 250 && !check(); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

const rowKeys = () => [...container.querySelectorAll<HTMLElement>('[data-variant-row-key]')].map(row => row.dataset.variantRowKey!)
const dialog = () => document.querySelector<HTMLElement>('.variant-delete-confirm-modal')

async function openEditorAndTrashPlaceholder() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><ToastStackProvider><MemoryRouter><main><NewProductPage editProductId="p1" /></main></MemoryRouter></ToastStackProvider></QueryClientProvider>) })
  await waitFor(() => rowKeys().includes('v1') && rowKeys().includes('v2'))
  const trash = container.querySelector<HTMLButtonElement>('[data-variant-row-key="v1"] .variant-row-action-delete')!
  await act(async () => { trash.click() })
  // A saved row asks first and stays until confirmed.
  expect(dialog()?.textContent).toContain('Bu varyant Kaydet’te silinir.')
  expect(rowKeys()).toContain('v1')
  const confirm = [...dialog()!.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.trim() === 'Varyantı Sil')!
  await act(async () => { confirm.click() })
  expect(dialog()).toBeNull()
  expect(rowKeys()).toEqual(['v2'])
}

async function saveDraft() {
  const draft = document.querySelector<HTMLButtonElement>('button[data-submit-intent="draft"]')
  expect(draft, 'Taslak Olarak Kaydet').toBeTruthy()
  await act(async () => { draft!.click() })
  await waitFor(() => fixture.calls.some(call => call.method === 'PATCH' && call.path === '/products/p1'))
  for (let index = 0; index < 20; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  return fixture.calls.find(call => call.method === 'PATCH' && call.path === '/products/p1')!.body as { variantIdsToDelete: string[]; variantUpdates: Array<{ id: string }> }
}

describe('the variant row trash button', () => {
  it('deletes a saved row on Kaydet after asking', async () => {
    await openEditorAndTrashPlaceholder()
    const patch = await saveDraft()
    expect(patch.variantIdsToDelete).toEqual(['v1'])
    expect(patch.variantUpdates.map(update => update.id)).toEqual(['v2'])
  }, 30000)

  it('keeps the row and shows the server reason when the delete is refused', async () => {
    fixture.state.refuse = true
    await openEditorAndTrashPlaceholder()
    await saveDraft()
    await waitFor(() => rowKeys().includes('v1'))
    expect(rowKeys()).toEqual(['v1', 'v2'])
    expect(document.body.textContent).toContain('sipariş stok hareketine bağlı olduğu için silinemiyor')
    expect(document.body.textContent).toContain('Varyant satırı geri getirildi')
  }, 30000)

})

describe('variant row removal helpers', () => {
  it('names what the panel knows is tied to the variant', () => {
    expect(variantRemovalConfirmText({ key: 'v1' })).toMatch(/^Bu varyant Kaydet’te silinir\./)
    expect(variantRemovalTies({ key: 'v1', loadedStock: 3, platformStatuses: [{ platform: 'Trendyol', isLinked: true }, { platform: 'Hepsiburada', isLinked: false, offerId: 'o1' }] }))
      .toEqual(['Trendyol pazaryeri bağlantısı panelden kaldırılır; pazaryerindeki ilana dokunulmaz.', 'Hepsiburada platform fiyatı silinir.', 'Paneldeki 3 adet stoğu da silinir.'])
    expect(variantRemovalTies({ key: 'v1', stock: 0, platformStatuses: [] })).toEqual([])
  })

  it('puts refused rows back where they stood, once', () => {
    const rows = [{ key: 'b' }, { key: 'd' }]
    expect(restoreRemovedVariantRows(rows, [{ row: { key: 'c' }, index: 2 }, { row: { key: 'a' }, index: 0 }]).map(row => row.key)).toEqual(['a', 'b', 'c', 'd'])
    expect(restoreRemovedVariantRows(rows, [{ row: { key: 'b' }, index: 0 }]).map(row => row.key)).toEqual(['b', 'd'])
  })
})
