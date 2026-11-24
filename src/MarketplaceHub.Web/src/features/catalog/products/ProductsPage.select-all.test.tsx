import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PhoneDock, taskBarButtons } from '../../../test/phone-dock'

// The phone's "Tümünü Seç (N)" is the desktop bulk bar's link: same handler (every product of the filter, all pages).
const card = (id: string, title: string, modelCode: string) => ({
  id, title, description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 3,
  variants: [{ id: `${id}-v1`, sku: `${modelCode}-S`, modelCode, optionSignature: 'Beden:S', status: 'ACTIVE', version: 1, onHand: 4, available: 4, salePrice: 450, listPrice: 500, currency: 'TRY', options: { Beden: 'S' }, productId: id }],
  primaryImageUrl: null, totalStock: 4, startingPrice: 450, currency: 'TRY', modelCode, activePlatforms: [], platformStatuses: [], categoryPath: 'Giyim', hasCustomMediaOrder: false
})
const everything = [card('p1', 'Triko Kazak', 'RYP001'), card('p2', 'Keten Gömlek', 'RYP002'), card('p9', 'Yün Atkı', 'RYP009')]
const fixture = { hasMore: true }
const loadAll = vi.hoisted(() => vi.fn<(path: string) => Promise<{ items: unknown[]; nextCursor: null; hasMore: false }>>())

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/products/summary')) return { totalCount: 3, activeCount: 3, outOfStockCount: 0, lowStockCount: 0, platforms: [] }
    if (path.startsWith('/products?')) {
      const params = new URLSearchParams(path.split('?')[1])
      if (params.get('limit') === '1') return { items: [], nextCursor: null, hasMore: false, totalCount: 0 }
      return fixture.hasMore ? { items: everything.slice(0, 2), nextCursor: 'cursor-2', hasMore: true, totalCount: 3 } : { items: everything, nextCursor: null, hasMore: false, totalCount: 3 }
    }
    if (path.startsWith('/jobs')) return []
    if (path.startsWith('/connections')) return { items: [], nextCursor: null, hasMore: false }
    throw new Error(`Test API fixture does not handle ${path}`)
  }
  loadAll.mockImplementation(async (path: string) => ({ items: path.startsWith('/products?') ? everything : [], nextCursor: null, hasMore: false }))
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: loadAll }
})

import { ProductsPage } from './ProductsPage'

let container: HTMLDivElement
let root: Root
beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  loadAll.mockClear()
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(() => { act(() => root.unmount()); container.remove() })

async function until(test: () => boolean, timeoutMs = 3000) {
  const started = Date.now()
  while (!test()) {
    if (Date.now() - started > timeoutMs) throw new Error('Timed out waiting for the products list.')
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
  }
}
async function mount(hasMore: boolean) {
  fixture.hasMore = hasMore
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><PhoneDock><MemoryRouter initialEntries={['/products']}><ProductsPage /></MemoryRouter></PhoneDock></QueryClientProvider>) })
  await until(() => Boolean(container.textContent?.includes('Keten Gömlek')))
}
const box = (title: string) => container.querySelector<HTMLInputElement>(`input[aria-label="${title} ürün grubunu seç"]`)!
const selectAll = () => Array.from(container.querySelectorAll<HTMLButtonElement>('.rv-taskbar-actions button')).find(button => button.textContent?.includes('Tümünü Seç'))

describe('ProductsPage Tümünü Seç on a phone', () => {
  it('is in the task bar with the filter total, loads every page with the desktop handler and goes away when all are selected', async () => {
    await mount(true)
    expect(selectAll()).toBeUndefined()
    act(() => box('Triko Kazak').click())
    expect(taskBarButtons(container)).toContain('Tümünü Seç (3)')
    await act(async () => { selectAll()!.click() })
    await until(() => !selectAll())
    expect(loadAll.mock.calls.filter(([path]) => path.startsWith('/products?'))).toHaveLength(1)
    expect(container.querySelector('.rv-taskbar-actions [data-dock-clear]')?.getAttribute('data-dock-count')).toBe('3')
  })

  it('selects the page when it holds every product', async () => {
    await mount(false)
    act(() => box('Triko Kazak').click())
    expect(taskBarButtons(container)).toContain('Tümünü Seç (3)')
    act(() => selectAll()!.click())
    expect(loadAll.mock.calls.filter(([path]) => path.startsWith('/products?'))).toHaveLength(0)
    expect(selectAll()).toBeUndefined()
    expect(container.querySelector('.rv-taskbar-actions [data-dock-clear]')?.getAttribute('data-dock-count')).toBe('3')
  })
})
