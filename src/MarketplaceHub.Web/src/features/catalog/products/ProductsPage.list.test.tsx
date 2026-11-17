import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// GET /products as the server sends it since 9 Nov 2026: the slim list card, its null fields left out (no description
// text, no attributes, options or picture strip on the card, no channel offers on the variants, one variant picture).
const listPage = (after: string | null) => ({
  items: after ? [] : [{
    id: 'p1', title: 'Triko Kazak', description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 3,
    variants: [
      { id: 'v1', sku: 'RYP-1-S', barcode: '869001', modelCode: 'RYP001', optionSignature: 'Renk:Bej|Beden:S', status: 'ACTIVE', version: 1, onHand: 4, available: 4, salePrice: 450, listPrice: 500, currency: 'TRY', mediaUrls: ['/api/v1/files/product-media/a/content'], options: { Renk: 'Bej', Beden: 'S' }, productId: 'p1' },
      { id: 'v2', sku: 'RYP-1-M', modelCode: 'RYP001', optionSignature: 'Renk:Bej|Beden:M', status: 'ACTIVE', version: 1, onHand: 0, available: 0, options: { Renk: 'Bej', Beden: 'M' }, productId: 'p1' }
    ],
    primaryImageUrl: '/api/v1/files/product-media/a/content', totalStock: 4, startingPrice: 450, currency: 'TRY', modelCode: 'RYP001', activePlatforms: ['Trendyol'],
    platformStatuses: [{ platform: 'Trendyol', status: 'ACTIVE', matchedVariantCount: 2, variantCount: 2, isChecking: false, platformCode: 'TRENDYOL', missingVariantCount: 0, onSaleVariantCount: 2, offSaleVariantCount: 0, offSaleStockZeroVariantCount: 0 }],
    categoryPath: 'Giyim > Kazak', hasCustomMediaOrder: false
  }],
  nextCursor: after ? undefined : 'cursor-2', hasMore: !after, totalCount: fixture.totalCount
})
const requested: string[] = []
// The list's total; a test that walks to page 2 raises it so the page is not clamped back to the first.
const fixture = { totalCount: 2 }

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    requested.push(path)
    if (path.startsWith('/products/summary')) return { totalCount: 2, activeCount: 2, outOfStockCount: 0, lowStockCount: 0, platforms: ['Trendyol'] }
    if (path.startsWith('/products?')) return listPage(new URLSearchParams(path.split('?')[1]).get('after'))
    if (path.startsWith('/jobs')) return []
    throw new Error(`Test API fixture does not handle ${path}`)
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { ProductsPage } from './ProductsPage'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  fixture.totalCount = 2
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  container.remove()
})

async function until(test: () => boolean, timeoutMs = 3000) {
  const started = Date.now()
  while (!test()) {
    if (Date.now() - started > timeoutMs) throw new Error('Timed out waiting for the products list.')
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
  }
}

async function render(role?: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  if (role) client.setQueryData(['me'], { id: 'u1', email: 'a@example.test', displayName: 'A', role, state: 'ACTIVE', tenantId: 't1' })
  root = createRoot(container)
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products']}><ProductsPage /></MemoryRouter></QueryClientProvider>)
  })
  await until(() => Boolean(container.textContent?.includes('Triko Kazak')))
}

const nextPageRequests = () => requested.filter(path => path.startsWith('/products?') && path.includes('after='))

describe('ProductsPage on the slim list answer', () => {
  it('shows the card, its price, stock and platform marks from the slim card', async () => {
    await render()
    const row = Array.from(container.querySelectorAll('tbody tr')).find(item => item.textContent?.includes('Triko Kazak'))!
    expect(row).toBeTruthy()
    expect(row.textContent).toContain('RYP001')
    expect(row.querySelector('td[data-l="Stok"]')?.textContent).toContain('4')
    expect(row.querySelector('td[data-l="Fiyat"]')?.textContent).toMatch(/450/)
    // The list price shows struck through beside the cheapest variant's sale price.
    expect(row.querySelector('td[data-l="Fiyat"] small')?.textContent).toMatch(/500/)
    const trendyol = row.querySelector('.rr-pm[aria-label^="Trendyol"]')
    expect(trendyol?.className).toContain('rr-on')
    expect(row.querySelector('img')?.getAttribute('src')).toBe('/api/v1/files/product-media/a/content')
  })

  // RoleAuthorizationMiddleware answers products/{id}/bulk-field with 403 to every role but the owner and administrators.
  async function bulkFieldItem(role: string) {
    await render(role)
    act(() => container.querySelector<HTMLInputElement>('input[aria-label="Triko Kazak ürün grubunu seç"]')!.click())
    await until(() => Boolean(container.querySelector('.rr-pbulk button[aria-haspopup="menu"]')))
    act(() => container.querySelector<HTMLButtonElement>('.rr-pbulk button[aria-haspopup="menu"]')!.click())
    return Array.from(container.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')).find(item => item.textContent?.includes('Toplu Değer Ata'))!
  }

  it('offers Toplu Değer Ata to the owner and administrators only', async () => {
    const operations = await bulkFieldItem('OPERATIONS')
    expect(operations.disabled).toBe(true)
    expect(operations.textContent).toContain('yalnız hesap sahibi ve yöneticiler')
    act(() => root.unmount())
    const owner = await bulkFieldItem('OWNER')
    expect(owner.disabled).toBe(false)
  })

  // W6 (scan 2026-11-15) pins today's behaviour: Ürünler keeps its selection across pages on purpose (Siparişler clears
  // it on every list change, Faturalar on every change but a sort on page 1); a filter or sort change clears it.
  it('keeps the selection across pages and clears it when the sort changes', async () => {
    fixture.totalCount = 40
    await render()
    const bar = () => container.querySelector('.rr-pbulk .rr-bulk-count')?.textContent ?? ''
    act(() => container.querySelector<HTMLInputElement>('input[aria-label="Triko Kazak ürün grubunu seç"]')!.click())
    expect(bar()).toContain('1 ürün kartı seçildi')
    act(() => container.querySelector<HTMLButtonElement>('.rr-pager button[aria-label="Sonraki"]')!.click())
    await until(() => nextPageRequests().length > 0 && !container.textContent?.includes('Triko Kazak'))
    expect(bar()).toContain('1 ürün kartı seçildi')
    act(() => container.querySelector<HTMLButtonElement>('.rr-pager button[aria-label="Önceki"]')!.click())
    await until(() => Boolean(container.textContent?.includes('Triko Kazak')))
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Triko Kazak ürün grubunu seç"]')!.checked).toBe(true)

    const sortField = Array.from(container.querySelectorAll('.rr-fld')).find(field => field.querySelector('small')?.textContent === 'Sıralama')!
    act(() => sortField.querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(option => option.getAttribute('aria-checked') === 'false')!.click())
    const apply = Array.from(container.querySelectorAll<HTMLButtonElement>('.rr-fbtns button')).find(button => button.textContent === 'Filtrele')
    if (apply && !apply.disabled) act(() => apply.click())
    await until(() => requested.some(path => path.startsWith('/products?') && path.includes('sort=')))
    await until(() => Boolean(container.textContent?.includes('Triko Kazak')))
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Triko Kazak ürün grubunu seç"]')!.checked).toBe(false)
  })

  it('reads the next page ahead only after this page, not at once', async () => {
    await render()
    // The page is on screen and its answer named a next page, but the next page is not read with it.
    expect(nextPageRequests()).toEqual([])
    // Nothing is loading on screen here, so it is read once the browser is idle.
    await until(() => nextPageRequests().length > 0)
    expect(nextPageRequests()).toEqual(['/products?limit=20&after=cursor-2'])
  })
})
