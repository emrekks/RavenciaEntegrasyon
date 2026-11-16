import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { listSelectionRules, selectionEvents, type SelectionEvent } from '../../../test/list-selection-rules'

// WEB-SAYFALAR-11: Ürünler's row selection against the shared table (src/test/list-selection-rules.ts).
const card = (id: string, title: string, modelCode: string) => ({
  id, title, description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 3,
  variants: [{ id: `${id}-v1`, sku: `${modelCode}-S`, modelCode, optionSignature: 'Beden:S', status: 'ACTIVE', version: 1, onHand: 4, available: 4, salePrice: 450, listPrice: 500, currency: 'TRY', options: { Beden: 'S' }, productId: id }],
  primaryImageUrl: null, totalStock: 4, startingPrice: 450, currency: 'TRY', modelCode, activePlatforms: [], platformStatuses: [], categoryPath: 'Giyim', hasCustomMediaOrder: false
})
const fixture = { withSecond: true }
const requested: string[] = []

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    requested.push(path)
    if (path.startsWith('/products/summary')) return { totalCount: 40, activeCount: 40, outOfStockCount: 0, lowStockCount: 0, platforms: [] }
    if (path.startsWith('/products?')) {
      const params = new URLSearchParams(path.split('?')[1])
      if (params.get('limit') === '1') return { items: [], nextCursor: null, hasMore: false, totalCount: 0 }
      if (params.get('after')) return { items: [card('p9', 'Yün Atkı', 'RYP009')], nextCursor: null, hasMore: false, totalCount: 40 }
      return { items: [card('p1', 'Triko Kazak', 'RYP001'), ...(fixture.withSecond ? [card('p2', 'Keten Gömlek', 'RYP002')] : [])], nextCursor: 'cursor-2', hasMore: true, totalCount: 40 }
    }
    if (path.startsWith('/jobs')) return []
    if (path.startsWith('/connections')) return { items: [], nextCursor: null, hasMore: false }
    throw new Error(`Test API fixture does not handle ${path}`)
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { ProductsPage } from './ProductsPage'

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(async () => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  fixture.withSecond = true
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/products']}><ProductsPage /></MemoryRouter></QueryClientProvider>) })
  await until(() => Boolean(container.textContent?.includes('Keten Gömlek')))
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function until(test: () => boolean, timeoutMs = 3000) {
  const started = Date.now()
  while (!test()) {
    if (Date.now() - started > timeoutMs) throw new Error('Timed out waiting for the products list.')
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
  }
}
async function settle() { for (let index = 0; index < 6; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
const box = (title: string) => container.querySelector<HTMLInputElement>(`input[aria-label="${title} ürün grubunu seç"]`)
// The bar shows what it showed last while it slides away; a leaving bar counts nothing.
const barCount = () => container.querySelector('.rr-pbulk:not(.is-leaving) .rr-bulk-count')?.textContent ?? ''
const listRequests = () => requested.filter(path => path.startsWith('/products?') && !path.includes('limit=1&'))
const radio = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(option => option.textContent?.trim() === label)!
const field = (label: string) => Array.from(container.querySelectorAll('.rr-filters .rr-fld')).find(item => item.querySelector('small')?.textContent === label)!

const events: Record<SelectionEvent, () => Promise<void>> = {
  async filter() {
    act(() => field('Stok Durumu').querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => radio('Düşük Stok').click())
    await until(() => listRequests().some(path => path.includes('stock=LOW')))
  },
  async search() {
    const input = container.querySelector<HTMLInputElement>('input[aria-label="Ürün ara"]')!
    act(() => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(input, 'kazak')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await until(() => listRequests().some(path => path.includes('search=kazak')))
  },
  async tab() {
    act(() => Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Satışa Açık'))!.click())
    await until(() => listRequests().some(path => path.includes('status=ACTIVE')))
  },
  async sort() {
    act(() => field('Sıralama').querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(option => option.getAttribute('aria-checked') === 'false')!.click())
    await until(() => listRequests().some(path => path.includes('sort=')))
  },
  async pageSize() {
    act(() => container.querySelector<HTMLButtonElement>('button.rr-psize-btn')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="option"]')).find(option => option.textContent === '50')!.click())
    await until(() => listRequests().some(path => path.includes('limit=50')))
  },
  async page() {
    act(() => container.querySelector<HTMLButtonElement>('.rr-pager button[aria-label="Sonraki"]')!.click())
    await until(() => Boolean(container.textContent?.includes('Yün Atkı')))
    act(() => container.querySelector<HTMLButtonElement>('.rr-pager button[aria-label="Önceki"]')!.click())
    await until(() => Boolean(container.textContent?.includes('Keten Gömlek')))
  },
  async refetch() {
    await act(async () => { await client.invalidateQueries({ queryKey: ['products'] }) })
    await settle()
  },
  async rowGone() {
    fixture.withSecond = false
    await act(async () => { await client.invalidateQueries({ queryKey: ['products'] }) })
    await until(() => !container.textContent?.includes('Keten Gömlek'))
  }
}

describe('Ürünler row selection (WEB-SAYFALAR-11 table)', () => {
  it.each(selectionEvents)('%s', async event => {
    await settle()
    act(() => box('Keten Gömlek')!.click())
    expect(barCount()).toContain('1 ürün kartı seçildi')
    await events[event]()
    await settle()
    const outcome = listSelectionRules.products[event]
    if (outcome === 'clears') {
      expect(barCount()).toBe('')
      expect(box('Keten Gömlek')!.checked).toBe(false)
    }
    if (outcome === 'keeps') {
      expect(barCount()).toContain('1 ürün kartı seçildi')
      expect(box('Keten Gömlek')!.checked).toBe(true)
    }
    if (outcome === 'counted') expect(barCount()).toContain('1 ürün kartı seçildi')
    if (outcome === 'uncounted') expect(barCount()).toBe('')
  })
})
