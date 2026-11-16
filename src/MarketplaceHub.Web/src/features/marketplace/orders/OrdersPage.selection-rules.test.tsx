import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { listSelectionRules, selectionEvents, type SelectionEvent } from '../../../test/list-selection-rules'

// WEB-SAYFALAR-11: Siparişler's row selection against the shared table (src/test/list-selection-rules.ts).
const requested: string[] = []
const fixture = { withSecond: true }
const order = (id: string, orderNumber: string) => ({ id, orderNumber, derivedStatus: 'NEW', currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-14T10:00:00Z', lineCount: 1, packageCount: 1, version: 1, connectionId: 'c1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: 'Ayşe Yılmaz', customerTaxOrIdentityNumber: null, orderType: 'STANDARD', isMicroExport: false, shipmentAddress: null, shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'NONE', invoiceId: null, invoiceDocumentUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: `TRK-${id}`, primaryImageUrl: null, productQuantity: 1, lines: null, packages: null })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    requested.push(path)
    if (path.startsWith('/orders?')) {
      if (new URLSearchParams(path.split('?')[1]).get('after')) return { items: [order('o9', 'TY-9')], nextCursor: null, hasMore: false, totalCount: 120 }
      return { items: [order('o1', 'TY-1'), ...(fixture.withSecond ? [order('o2', 'TY-2')] : [])], nextCursor: 'next', hasMore: true, totalCount: 120 }
    }
    if (path.startsWith('/orders/facets')) return { platforms: {}, cargos: {} }
    if (path.startsWith('/orders/summary')) return {}
    if (path.startsWith('/dashboard/cancellations')) return { stores: [] }
    if (path.startsWith('/dashboard/bootstrap')) return { channels: [] }
    if (path.startsWith('/settings/shipping-label')) return { settings: null, version: 0 }
    return null
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { OrdersPage } from './OrdersPage'

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(async () => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  fixture.withSecond = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function settle() { for (let index = 0; index < 8; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
const box = (orderNumber: string) => container.querySelector<HTMLInputElement>(`input[aria-label="Sipariş ${orderNumber} seç"]`)
// The bar shows what it showed last while it slides away; a leaving bar counts nothing.
const barText = () => container.querySelector('.rr-bulk:not(.is-leaving)')?.textContent ?? ''
const lastList = () => requested.filter(path => path.startsWith('/orders?')).at(-1) ?? ''
const field = (label: string) => Array.from(container.querySelectorAll('.rr-filters .rr-fld')).find(item => item.querySelector('small')?.textContent === label)!
const option = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"], [role="option"]')).find(item => item.textContent?.trim() === label)!
const apply = () => act(() => Array.from(container.querySelectorAll<HTMLButtonElement>('.rr-fbtns button')).find(button => button.textContent === 'Filtrele')!.click())

const events: Record<SelectionEvent, () => Promise<void>> = {
  async filter() {
    act(() => field('Etiket Durumu').querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => option('Etiketi Yazdırılanlar').click())
    await settle()
    expect(lastList()).toContain('labelPrinted=true')
  },
  async search() {
    const input = container.querySelector<HTMLInputElement>('input[aria-label="Sipariş ara"]')!
    act(() => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(input, 'TY-2')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    apply()
    await settle()
    expect(lastList()).toContain('search=TY-2')
  },
  async tab() {
    act(() => Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Yeni'))!.click())
    await settle()
    expect(lastList()).toContain('status=NEW')
  },
  async sort() {
    act(() => field('Sıralama').querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => option('En Eski Siparişler').click())
    await settle()
    expect(lastList()).toContain('sort=DATE_ASC')
  },
  async pageSize() {
    act(() => container.querySelector<HTMLButtonElement>('button.rr-psize-btn')!.click())
    act(() => option('100').click())
    await settle()
    expect(lastList()).toContain('limit=100')
  },
  async page() {
    act(() => container.querySelector<HTMLButtonElement>('button[aria-label="Sonraki"]')!.click())
    await settle()
    expect(lastList()).toContain('after=next')
    act(() => container.querySelector<HTMLButtonElement>('button[aria-label="Önceki"]')!.click())
    await settle()
  },
  async refetch() {
    await act(async () => { await client.invalidateQueries({ queryKey: ['orders'] }) })
    await settle()
  },
  async rowGone() {
    fixture.withSecond = false
    await act(async () => { await client.invalidateQueries({ queryKey: ['orders'] }) })
    await settle()
    expect(box('TY-2')).toBeNull()
  }
}

describe('Siparişler row selection (WEB-SAYFALAR-11 table)', () => {
  it.each(selectionEvents)('%s', async event => {
    act(() => box('TY-2')!.click())
    expect(barText()).toContain('1 sipariş seçildi')
    await events[event]()
    const outcome = listSelectionRules.orders[event]
    if (outcome === 'clears') {
      expect(barText()).toBe('')
      expect(box('TY-2')?.checked ?? false).toBe(false)
    }
    if (outcome === 'keeps') {
      expect(barText()).toContain('1 sipariş seçildi')
      expect(box('TY-2')!.checked).toBe(true)
    }
    if (outcome === 'counted') expect(barText()).toContain('1 sipariş seçildi')
    if (outcome === 'uncounted') {
      expect(barText()).toBe('')
      fixture.withSecond = true
      await act(async () => { await client.invalidateQueries({ queryKey: ['orders'] }) })
      await settle()
      expect(box('TY-2')!.checked).toBe(true)
      expect(barText()).toContain('1 sipariş seçildi')
    }
  })
})
