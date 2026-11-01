import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// A saved record on Hepsiburada whose category is mapped: Kumaş Tipi and Kesim Kalıbı are required and not mapped, the
// product's Boy has no mapped field, and its Kalınlık value "Orta" has no value mapping (İnce has one).
const fixture = vi.hoisted(() => {
  const variant = { id: 'v1', version: 1, sku: 'RYP010-S', barcode: 'RYP0100001', modelCode: 'RYP010', optionSignature: '-', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: [], options: {}, attributes: [] }
  const product = { id: 'p1', version: 1, title: 'Yonca Desen Elbise', description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant], primaryImageUrl: null, totalStock: 3, startingPrice: 90, currency: 'TRY', modelCode: 'RYP010', activePlatforms: [], mediaUrls: [], attributes: [{ attributeId: 'boy', valueId: 'midi' }, { attributeId: 'kal', valueId: 'orta' }], familyProductIds: ['p1'] }
  const connection = { id: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada Mağaza', externalStoreId: 'hb-1', status: 'ACTIVE', externalWritesEnabled: true }
  const attribute = (id: string, name: string, values: Array<[string, string]>) => ({ id, code: id, name, dataType: 'SELECT', isActive: true, version: 1, values: values.map(([valueId, value], index) => ({ id: valueId, value, sortOrder: index, isActive: true })) })
  const attributes = [attribute('boy', 'Boy', [['midi', 'Midi']]), attribute('kal', 'Kalınlık', [['orta', 'Orta'], ['ince', 'İnce']]), attribute('kumas', 'Kumaş', [['pamuk', 'Pamuk']]), attribute('renk', 'Renk', [['kirmizi', 'Kırmızı']])]
  const requirements = [
    { attributeId: 'boy', isRequired: false, allowsCustomValue: false, displayOrder: 1, role: 'ATTRIBUTE', attribute: attributes[0] },
    { attributeId: 'kal', isRequired: false, allowsCustomValue: false, displayOrder: 2, role: 'ATTRIBUTE', attribute: attributes[1] }
  ]
  const remote = (externalId: string, name: string, isRequired: boolean) => ({ externalId, parentExternalId: null, name, path: name, depth: 0, isLeaf: true, isActive: true, isRequired, allowsCustomValue: false, allowsMultipleValues: false })
  const categoryMapping = { id: 'cm1', connectionId: 'hb', snapshotId: 's1', localId: 'c1', scopeExternalId: '', externalId: 'hb-cat', status: 'VERIFIED', verifiedAt: null, version: 1 }
  const state = {
    attributeMappings: [{ id: 'am-kal', connectionId: 'hb', snapshotId: 'a1', localId: 'kal', scopeExternalId: 'hb-cat', externalId: 'k4', status: 'VERIFIED', verifiedAt: null, version: 1 }] as Array<Record<string, unknown>>,
    readinessReads: 0,
    puts: [] as Array<{ path: string; body: unknown }>
  }
  return { product, connection, attributes, requirements, remote, categoryMapping, state }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const { state } = fixture
    if (init?.method === 'PUT' && path.startsWith('/mappings/attributes/')) {
      const body = JSON.parse(String(init.body)) as { externalId: string; snapshotId: string; scopeExternalId: string }
      const saved = { id: `am-${state.puts.length}`, connectionId: 'hb', localId: path.split('/').pop(), version: 1, status: 'VERIFIED', verifiedAt: null, ...body }
      state.puts.push({ path, body })
      state.attributeMappings.push(saved)
      return saved
    }
    if (path === '/products/p1') return fixture.product
    if (path.startsWith('/catalog/categories/c1/attribute-requirements')) return fixture.requirements
    if (path.startsWith('/products/p1/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/mappings/categories?')) return [fixture.categoryMapping]
    if (path.startsWith('/mappings/categories/c1')) return fixture.categoryMapping
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', resourceType: 'CATEGORIES', fetchedAt: '2026-11-01T10:00:00Z', items: [{ ...fixture.remote('hb-cat', 'Elbise', false), path: 'Giyim > Elbise' }] }
    if (path.startsWith('/reference-data/categories/hb-cat/attributes/k4/values')) return { snapshotId: 'v1', resourceType: 'ATTRIBUTE_VALUES', fetchedAt: '2026-11-01T10:00:00Z', items: [fixture.remote('x-orta', 'Orta', false), fixture.remote('x-ince', 'İnce', false)] }
    if (path.startsWith('/reference-data/categories/hb-cat/attributes?')) {
      state.readinessReads++
      return { snapshotId: 'a1', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-11-01T10:00:00Z', items: [fixture.remote('k1', 'Kumaş Tipi', true), fixture.remote('k2', 'Kesim Kalıbı', true), fixture.remote('k3', 'Boy', false), fixture.remote('k4', 'Kalınlık', false), ...(fixture.connection.platformCode === 'TRENDYOL' ? [fixture.remote('k5', 'Web Color', false)] : [])] }
    }
    if (path.startsWith('/mappings/attributes?')) return state.attributeMappings
    if (path.startsWith('/mappings/attribute-values?')) return path.includes(encodeURIComponent('hb-cat/k4')) ? [{ id: 'vm-ince', connectionId: 'hb', snapshotId: 'v1', localId: 'ince', scopeExternalId: 'hb-cat/k4', externalId: 'x-ince', status: 'VERIFIED', verifiedAt: null, version: 1 }] : []
    if (path.includes('/attribute-defaults')) return []
    if (path.startsWith('/mappings/conflicts')) return {}
    if (path.startsWith('/mappings/attribute-name-hints')) return {}
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => ({
    items: path.startsWith('/connections') ? [fixture.connection]
      : path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise', depth: 0, version: 1 }]
        : path.startsWith('/catalog/brands') ? [{ id: 'b1', name: 'Ravencia', isActive: true }]
          : path.startsWith('/catalog/attributes') ? fixture.attributes
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

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  fixture.connection.platformCode = 'HEPSIBURADA'
})

async function settle(rounds = 10) {
  for (let index = 0; index < rounds; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}

const chips = (section: 'required' | 'optional') => [...document.querySelectorAll<HTMLElement>(`.publish-issue-${section} .publish-issue-chip`)]
const chip = (section: 'required' | 'optional', title: string) => chips(section).find(item => item.textContent === title)!
const selectedField = () => document.querySelector('.mapping-advanced-modal .cm-item.is-selected .cm-name')?.textContent
const marked = (tone: 'required' | 'optional') => [...document.querySelectorAll('.mapping-advanced-modal .cm-item.is-product-' + tone + ' .cm-name')].map(item => item.textContent)
const button = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('.mapping-advanced-modal button')].find(item => item.textContent === label)!

// The window's code loads on demand: wait (up to 5 s under a busy test run) until what is looked for is there.
async function waitFor(check: () => boolean) {
  for (let index = 0; index < 250 && !check(); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

async function openFromChip(section: 'required' | 'optional', title: string) {
  await waitFor(() => Boolean(document.querySelector('.publish-issue-trigger')))
  await act(async () => { document.querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click() })
  await act(async () => { chip(section, title).click() })
  await waitFor(() => Boolean(selectedField()))
  await settle()
}

describe('publish gap chips open the category fields window', () => {
  it('opens the window on the pressed field, marks the product gaps and walks them with Sonraki Eksik', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" publishOnly /></main></MemoryRouter></QueryClientProvider>) })
    await waitFor(() => chips('optional').length === 2)
    expect(chips('required').map(item => item.textContent)).toEqual(expect.arrayContaining(['Kumaş Tipi', 'Kesim Kalıbı']))
    expect(chips('optional').map(item => item.textContent)).toEqual(['Boy', 'Kalınlık'])

    await openFromChip('required', 'Kesim Kalıbı')
    expect(document.querySelector('.mapping-advanced-modal h2')?.textContent).toBe('Kategori Alanları ve Değerleri')
    expect(selectedField()).toBe('Kesim Kalıbı')
    // Red marks for the required gaps, yellow for the optional warnings; a field the product does not need stays plain.
    expect(marked('required')).toEqual(['Kesim Kalıbı', 'Kumaş Tipi'])
    expect(marked('optional')).toEqual(['Boy', 'Kalınlık'])

    // Sonraki Eksik: the required gaps first, then the optional ones, in the publish check's order.
    await act(async () => { button('Sonraki Eksik').click() })
    expect(selectedField()).toBe('Boy')
    await act(async () => { button('Sonraki Eksik').click() })
    await waitFor(() => Boolean(document.querySelector('.mapping-advanced-modal .cm-vtable')))
    expect(selectedField()).toBe('Kalınlık')
    // The value gap: the table opens on "Yalnızca Eksikler" with the product's value marked.
    expect(button('Yalnızca Eksikler').getAttribute('aria-pressed')).toBe('true')
    expect([...document.querySelectorAll('.mapping-advanced-modal .cm-vrow.is-product-issue .cm-pv')].map(item => item.firstChild?.textContent)).toEqual(['Orta'])
    expect([...document.querySelectorAll('.mapping-advanced-modal .cm-vscroll .cm-vrow')]).toHaveLength(1)

    // Any other field can still be chosen; from there Sonraki Eksik goes back to the product's first gap.
    await act(async () => { [...document.querySelectorAll<HTMLButtonElement>('.mapping-advanced-modal .cm-item')].find(item => item.querySelector('.cm-name')?.textContent === 'Kumaş Tipi')!.click() })
    expect(selectedField()).toBe('Kumaş Tipi')
    await act(async () => { button('Sonraki Eksik').click() })
    expect(selectedField()).toBe('Kesim Kalıbı')

    // "Bu Üründeki Eksikler" narrows the list to the product's gaps.
    await act(async () => { [...document.querySelectorAll<HTMLButtonElement>('.mapping-advanced-modal button')].find(item => item.textContent?.startsWith('Bu Üründeki Eksikler'))!.click() })
    expect(document.querySelectorAll('.mapping-advanced-modal .cm-item')).toHaveLength(4)
  })

  it('reads the publish check again after a save and when the window closes', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" publishOnly /></main></MemoryRouter></QueryClientProvider>) })
    await waitFor(() => chips('required').some(item => item.textContent === 'Kumaş Tipi'))
    await openFromChip('required', 'Kumaş Tipi')
    expect(selectedField()).toBe('Kumaş Tipi')
    // Choosing the panel attribute saves the mapping (the panel's own data) and the badge follows it.
    const select = document.querySelector<HTMLSelectElement>('.mapping-advanced-modal select[aria-label="Kumaş Tipi için panel özelliği"]')!
    await act(async () => {
      select.value = 'kumas'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    await waitFor(() => Boolean(chip('required', 'Kumaş Tipi')?.title.includes('değeri seçilmemiş')))
    expect(fixture.state.puts).toEqual([{ path: '/mappings/attributes/kumas', body: expect.objectContaining({ connectionId: 'hb', scopeExternalId: 'hb-cat', externalId: 'k1', status: 'VERIFIED' }) }])
    expect(chip('required', 'Kumaş Tipi').title).toContain('ürün veya varyant değeri seçilmemiş')
    // Closing reads the check once more.
    const before = fixture.state.readinessReads
    await act(async () => { document.querySelector<HTMLButtonElement>('.mapping-advanced-modal .modal-close')!.click() })
    await waitFor(() => fixture.state.readinessReads > before)
    expect(document.querySelector('.mapping-advanced-modal')).toBeNull()
    expect(fixture.state.readinessReads).toBeGreaterThan(before)
  })

  it('gives every chip a fix; a gap of the product’s own data opens the window on its first missing field', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" publishOnly /></main></MemoryRouter></QueryClientProvider>) })
    await waitFor(() => chips('optional').length === 2)
    // No chip is a dead end: each is a button or a link.
    const all = [...chips('required'), ...chips('optional')]
    expect(all.map(item => item.textContent)).toEqual(expect.arrayContaining(['Temel Ürün Verileri', 'Görsel Kalitesi']))
    expect(all.filter(item => !item.matches('button, a[href]')).map(item => item.textContent)).toEqual([])
    await openFromChip('required', 'Temel Ürün Verileri')
    expect(selectedField()).toBe('Kumaş Tipi')
  })

  it('opens Web Color on Trendyol for the "Kategori ve Web Color" gap', async () => {
    fixture.connection.platformCode = 'TRENDYOL'
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><main><NewProductPage editProductId="p1" publishOnly /></main></MemoryRouter></QueryClientProvider>) })
    await waitFor(() => chips('required').some(item => item.textContent === 'Kategori ve Web Color'))
    expect(chip('required', 'Kategori ve Web Color').matches('button')).toBe(true)
    await openFromChip('required', 'Kategori ve Web Color')
    expect(selectedField()).toBe('Web Color')
    // The Renk ↔ Web Color card is the field's detail.
    expect(document.querySelector('.mapping-advanced-modal .cm-detail .cm-legacy-card')).not.toBeNull()
  })
})
