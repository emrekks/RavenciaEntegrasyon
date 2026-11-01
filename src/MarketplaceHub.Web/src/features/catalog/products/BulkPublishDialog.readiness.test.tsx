import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Two selected records: RYP006 has no description (a required gap everywhere), RYP007 is complete. The category has a
// Renk option (its Web Color) and maps on the store with no marketplace attributes, so only the product checks decide.
const api = vi.hoisted(() => {
  const requirements = [{ attributeId: 'a-renk', isRequired: false, allowsCustomValue: false, displayOrder: 1, role: 'OPTION', isWebColor: true, attribute: { id: 'a-renk', version: 1, code: 'option-renk', name: 'Renk', dataType: 'TEXT', values: [{ id: 'red', value: 'Kırmızı' }], roles: ['OPTION'] } }]
  const variant = (id: string, sku: string) => ({ id, version: 1, sku, barcode: `${sku}-B`, modelCode: sku, optionSignature: 'Renk:Kırmızı', status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1, offerId: null, listPrice: 100, salePrice: 90, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: ['https://cdn.example/a.jpg'], options: { Renk: 'Kırmızı' }, attributes: [] })
  const product = (id: string, sku: string, description: string) => ({ id, version: 1, title: `Elbise ${sku}`, description, brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T10:00:00Z', variants: [variant(`${id}-v`, sku)], primaryImageUrl: null, totalStock: 3, startingPrice: 90, currency: 'TRY', modelCode: sku, activePlatforms: [], mediaUrls: [], attributes: [], vatRate: 20 })
  const products: Record<string, unknown> = { p6: product('p6', 'RYP006', ''), p7: product('p7', 'RYP007', 'Açıklama') }
  return {
    requirements,
    products,
    gate: null as null | Promise<void>,
    posts: [] as Array<{ productIds: string[]; connectionIds: string[] }>,
    inFlight: 0,
    maxInFlight: 0,
    // Hepsiburada has no mapping of the panel category until the card's "Kategori Eşle" saves one.
    hbMapped: false,
    categoryPuts: [] as Array<{ path: string; body: Record<string, unknown> }>
  }
})

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    if (path === '/products/bulk-publication-jobs') {
      const body = JSON.parse(String(init?.body))
      api.posts.push(body)
      return { queued: body.productIds.length * body.connectionIds.length, skipped: 0, failed: [] }
    }
    const product = /^\/products\/(p\d+)$/.exec(path)?.[1]
    if (product) {
      api.inFlight++; api.maxInFlight = Math.max(api.maxInFlight, api.inFlight)
      try { if (api.gate) await api.gate; return api.products[product] } finally { api.inFlight-- }
    }
    if (path.includes('/attribute-requirements')) return api.requirements
    if (path.includes('/publication-status/')) return { actualStatus: 'UNKNOWN', lastJobStatus: null }
    if (path === '/mappings/categories/c1' && init?.method === 'PUT') {
      api.categoryPuts.push({ path, body: JSON.parse(String(init.body)) })
      api.hbMapped = true
      return { externalId: '411', snapshotId: 's1', status: 'VERIFIED', version: 1 }
    }
    if (path.startsWith('/mappings/categories/') && path.includes('connectionId=hb')) return api.hbMapped ? { externalId: '411', snapshotId: 's1', status: 'VERIFIED', version: 1 } : null
    if (path.startsWith('/mappings/categories/')) return { externalId: '411', snapshotId: 's1', status: 'VERIFIED', version: 1 }
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', items: [{ externalId: '411', name: 'Elbise', path: 'Giyim > Elbise', isActive: true, isLeaf: true }] }
    if (path.includes('/attributes?')) return path.startsWith('/reference-data') ? { snapshotId: 'a1', items: [] } : []
    if (path.includes('/attribute-defaults')) return []
    return null
  }
  const pages = async (path: string) => ({ items: path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Kazak', isLeaf: true, isActive: true, path: 'Kazak' }] : [], nextCursor: null, hasMore: false })
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { BulkPublishDialog } from './BulkPublishDialog'
import { runLimited } from './bulk-publish-readiness'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLElement
let root: Root
const connection = { id: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağaza', externalStoreId: '930112', status: 'ACTIVE', externalWritesEnabled: true }

beforeEach(() => {
  container = document.createElement('main')
  document.body.append(container)
  root = createRoot(container)
  api.gate = null; api.posts = []; api.inFlight = 0; api.maxInFlight = 0; api.hbMapped = false; api.categoryPuts = []
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function settle(rounds = 8) {
  for (let index = 0; index < rounds; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
}

async function render(productIds: string[], connections = [connection]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><BulkPublishDialog productIds={productIds} cardCount={productIds.length} connections={connections} products={[]} onClose={vi.fn()} onDone={vi.fn()} /></QueryClientProvider>) })
}

const card = () => document.querySelector('article.publish-platform-card')!
const cardButton = () => card().querySelector<HTMLButtonElement>('.publish-platform-card-head')!
const button = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent === label)!

describe('BulkPublishDialog readiness', () => {
  it('masks the cards and counts the checked records until every record is checked', async () => {
    let open = () => {}
    api.gate = new Promise(resolve => { open = resolve })
    await render(['p6', 'p7'])
    expect(card().classList.contains('is-settling')).toBe(true)
    expect(cardButton().disabled).toBe(true)
    expect(document.body.textContent).toContain('0 / 2 ürün kontrol edildi')
    expect(button('Yayınla').disabled).toBe(true)
    await act(async () => { open() })
    await settle()
    expect(card().classList.contains('is-settling')).toBe(false)
  })

  it('says how many records have gaps, lists them with a link and sends only the ready ones', async () => {
    await render(['p6', 'p7'])
    await settle()
    expect(card().textContent).toContain('1/2 üründe zorunlu eksik')
    expect(card().textContent).toContain('1 hazır · 1 eksik')
    expect(card().textContent).toContain('Yalnız hazır olanlar gönderilir')
    // The gaps sit in the red badge's popover: pressing the badge pins it, Escape closes it.
    const badge = card().querySelector<HTMLButtonElement>('.publish-issue-trigger')!
    act(() => badge.click())
    expect(card().querySelector('.publish-issue-tooltip.is-open')).not.toBeNull()
    expect(badge.getAttribute('aria-expanded')).toBe('true')
    const link = card().querySelector<HTMLAnchorElement>('.publish-issue-tooltip li a')!
    expect(link.textContent).toBe('Elbise RYP006')
    expect(link.getAttribute('href')).toBe('/products/p6')
    expect(card().querySelector('.publish-issue-tooltip li')?.textContent).toContain('Temel Ürün Verileri')
    act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(card().querySelector('.publish-issue-tooltip.is-open')).toBeNull()
    act(() => cardButton().click())
    expect(document.body.textContent).toContain('yalnız hazır olanlar gönderilir')
    await act(async () => { button('Yayınla').click() })
    await settle(3)
    expect(api.posts).toEqual([{ productIds: ['p7'], connectionIds: ['ty'] }])
  })

  it('locks a store where every record has gaps', async () => {
    await render(['p6'])
    await settle()
    expect(card().textContent).toContain('1/1 üründe zorunlu eksik')
    expect(card().textContent).toContain('Zorunlu eksikler giderilmeden seçilemez')
    expect(cardButton().disabled).toBe(true)
  })

  it('checks at most six records at once', async () => {
    let running = 0
    let most = 0
    await runLimited(Array.from({ length: 40 }, (_, index) => index), 6, async () => {
      running++; most = Math.max(most, running)
      await new Promise(resolve => setTimeout(resolve, 1))
      running--
    }, () => false)
    expect(most).toBe(6)
  })

  it('says the category is not mapped on Hepsiburada and maps it from the card', async () => {
    const hepsiburada = { id: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada', externalStoreId: 'hb-1', status: 'ACTIVE', externalWritesEnabled: true }
    await render(['p7'], [hepsiburada])
    await settle()
    expect(card().textContent).toContain('Kazak kategorisinin Hepsiburada karşılığı seçilmemiş; kategoriyi eşleyin.')
    expect(card().textContent).not.toContain('alınamadı')
    // One record and one fix: pressing the badge opens Kategori Eşle directly; the card has no fix rows of its own.
    expect(card().querySelector('.publish-platform-fix-mappings')).toBeNull()
    await act(async () => { card().querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click() })
    await settle(2)
    expect(document.body.textContent).toContain('Kazak kategorisinin Hepsiburada karşılığını seçin')
    await act(async () => { document.querySelector<HTMLButtonElement>('.category-mapping-dialog .vp-trigger')!.click() })
    await act(async () => { [...document.querySelectorAll<HTMLButtonElement>('.vp-list [role="option"]')].find(option => option.textContent === 'Giyim > Elbise')!.click() })
    await act(async () => { button('Eşlemeyi Kaydet').click() })
    await settle()
    expect(api.categoryPuts).toEqual([{ path: '/mappings/categories/c1', body: { connectionId: 'hb', snapshotId: 's1', externalId: '411', status: 'VERIFIED' } }])
    // The check runs again: the record is ready on Hepsiburada now.
    expect(card().querySelector('.publish-issue-trigger')).toBeNull()
    expect(cardButton().disabled).toBe(false)
  })
})
