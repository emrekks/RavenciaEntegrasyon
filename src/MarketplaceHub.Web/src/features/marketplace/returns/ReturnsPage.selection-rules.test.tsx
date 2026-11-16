import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, type, waitFor } from '../../../test/render'
import { listSelectionRules, selectionEvents, type SelectionEvent } from '../../../test/list-selection-rules'

// WEB-SAYFALAR-11: İadeler's bulk selection (Aksiyon Bekleyen tab) against the shared table (src/test/list-selection-rules.ts).
const api = vi.hoisted(() => ({ paths: [] as string[], withSecond: true }))
const claim = (id: string) => ({
  id, externalClaimId: `CL-${id}`, orderNumber: `#${id}`, status: 'ACTION_REQUIRED', rawStatus: 'WaitingInAction', reasonText: 'Beden küçük', reasonCode: null, actionDueAt: null, version: 1,
  customerName: `Alıcı ${id}`, orderedAt: '2026-10-10T08:00:00Z', orderAmount: 100, currency: 'TRY', cargoProviderName: 'Aras Kargo', cargoTrackingNumber: null, primaryImageUrl: null,
  productCount: 1, primaryBarcode: null, lines: [], packageNumber: null, invoiceStatus: 'FATURA_BILINMIYOR', grossAmount: 100, discountAmount: 0, isMicroExport: false,
  connectionId: 'c', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', lineStatuses: {}, returnAmounts: null, rowKey: null
})
const summary = {
  tabCounts: { ALL: 41, REQUESTED: 3, SHIPPING: 5, ACTION_REQUIRED: 7, APPROVED: 22, REJECTED: 4, REVIEW: 0, DISPUTED: 0, SUSPENDED: 0 },
  platforms: [{ platformCode: 'TRENDYOL', displayName: 'Trendyol' }], cargoProviders: ['Yurtiçi', 'Aras Kargo'],
  reasons: [{ value: 'SIZE', reasonCode: 'SIZE', reasonText: null }], invoiceStatuses: ['FATURA_BILINMIYOR'], unfilteredCount: 41,
  metrics: { rejectedAwaitingCargo: 0, rejectedCargoUnknown: 0, todayApproved: 0, todayRejected: 0 }
}

vi.mock('../../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../../shared/api')>()),
  get hubPoll() { return (this as unknown as { hubApi: unknown }).hubApi },
  hubApi: vi.fn(async (path: string) => {
    api.paths.push(path)
    const params = new URLSearchParams(path.split('?')[1])
    if (path.startsWith('/returns/summary')) return summary
    if (path.startsWith('/returns?')) {
      if (params.get('after') === 'next-1') return { items: [claim('21')], nextCursor: null, hasMore: false, totalCount: 41 }
      return { items: [claim('1'), ...(api.withSecond ? [claim('2')] : [])], nextCursor: 'next-1', hasMore: true, totalCount: 41 }
    }
    return []
  }),
  loadAllPages: vi.fn(async (path: string) => ({ items: path.startsWith('/connections') ? [{ id: 'c', platformCode: 'TRENDYOL', status: 'ACTIVE' }] : [], nextCursor: null, hasMore: false }))
}))

import { ReturnsPage } from './ReturnPages'

let host: HTMLElement
let client: QueryClient

beforeEach(async () => {
  api.paths = []
  api.withSecond = true
  client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 60_000 } } })
  host = render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/returns?status=ACTION_REQUIRED']}><ReturnsPage /></MemoryRouter></QueryClientProvider>).host
  await waitFor(() => box('2'))
})
afterEach(cleanup)

async function settle() { for (let index = 0; index < 4; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
const box = (id: string) => host.querySelector<HTMLInputElement>(`input[aria-label$="${id} iadesini toplu onaya dahil et"]`)
// The bar shows what it showed last while it slides away; a leaving bar counts nothing.
const barText = () => host.querySelector('.rr-bulk:not(.is-leaving)')?.textContent ?? ''
const lastList = () => api.paths.filter(path => path.startsWith('/returns?')).at(-1) ?? ''
const field = (label: string) => Array.from(host.querySelectorAll('.rr-filters .rr-fld')).find(element => element.querySelector('small')?.textContent === label)!
const option = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"], [role="option"], .rr-opt')).find(element => element.textContent?.trim() === label)!
const tab = (label: string) => act(() => Array.from(host.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(element => element.textContent?.includes(label))!.click())
const apply = () => act(() => Array.from(host.querySelectorAll<HTMLButtonElement>('.rr-fbtns button')).find(button => button.textContent === 'Filtrele')!.click())

const events: Record<SelectionEvent, () => Promise<void>> = {
  async filter() {
    act(() => field('Kargo Firması').querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(element => element.textContent?.includes('Yurtiçi'))!.click())
    await settle()
    expect(lastList()).toContain('cargo')
  },
  async search() {
    type(host.querySelector<HTMLInputElement>('input[aria-label="İadelerde ara"]')!, 'Alıcı')
    apply()
    await settle()
    expect(lastList()).toContain('search=')
  },
  async tab() {
    // Bulk decisions exist on Aksiyon Bekleyen only: away and back, the pick is gone if the tab change cleared it.
    tab('Onaylanan')
    await settle()
    expect(lastList()).toContain('status=APPROVED')
    tab('Aksiyon Bekleyen')
    await settle()
  },
  async sort() {
    act(() => host.querySelector<HTMLButtonElement>('.rr-return-sort button.rr-trigger')!.click())
    act(() => option('En Eski Sipariş').click())
    await settle()
    expect(lastList()).toContain('sort=ORDERED_ASC')
  },
  async pageSize() {
    act(() => host.querySelector<HTMLButtonElement>('button.rr-psize-btn')!.click())
    act(() => option('50').click())
    await settle()
    expect(lastList()).toContain('limit=50')
  },
  async page() {
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Sonraki"]')!.click())
    await waitFor(() => box('21'))
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Önceki"]')!.click())
    await waitFor(() => box('2'))
  },
  async refetch() {
    await act(async () => { await client.invalidateQueries({ queryKey: ['returns'] }) })
    await settle()
  },
  async rowGone() {
    api.withSecond = false
    await act(async () => { await client.invalidateQueries({ queryKey: ['returns'] }) })
    await settle()
    expect(box('2')).toBeNull()
  }
}

describe('İadeler bulk selection (WEB-SAYFALAR-11 table)', () => {
  it.each(selectionEvents)('%s', async event => {
    act(() => box('2')!.click())
    expect(barText()).toContain('1 iade seçildi')
    await events[event]()
    const outcome = listSelectionRules.returns[event]
    if (outcome === 'clears') {
      expect(barText()).toBe('')
      expect(box('2')?.checked ?? false).toBe(false)
    }
    if (outcome === 'keeps') {
      expect(barText()).toContain('1 iade seçildi')
      expect(box('2')!.checked).toBe(true)
    }
    if (outcome === 'counted') expect(barText()).toContain('1 iade seçildi')
    // The row comes back on a later read: it is ticked again, and counted once.
    if (outcome === 'counted') {
      api.withSecond = true
      await act(async () => { await client.invalidateQueries({ queryKey: ['returns'] }) })
      await settle()
      expect(box('2')!.checked).toBe(true)
      expect(barText()).toContain('1 iade seçildi')
    }
  })
})
