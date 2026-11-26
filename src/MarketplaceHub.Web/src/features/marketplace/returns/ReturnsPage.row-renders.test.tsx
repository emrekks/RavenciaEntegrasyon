import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../../test/render'

// İadeler (26 Nov 2026): a pick, a row menu or a poll that brought the same returns redrew every row. A row now draws
// again only when its own inputs change. Counted, not timed: each return row render calls useProductLines once.
const counter = vi.hoisted(() => ({ rows: 0, paths: [] as string[] }))
vi.mock('../product-lines', async importOriginal => {
  const original = await importOriginal<typeof import('../product-lines')>()
  return { ...original, useProductLines: (total: number) => { counter.rows++; return original.useProductLines(total) } }
})
const claim = (index: number) => ({
  id: `r${index}`, externalClaimId: `CL-${index}`, orderNumber: `#${index}`, status: 'ACTION_REQUIRED', rawStatus: 'WaitingInAction', reasonText: 'Beden küçük', reasonCode: null, actionDueAt: null, version: 1,
  customerName: `Alıcı ${index}`, orderedAt: '2026-10-10T08:00:00Z', orderAmount: 100, currency: 'TRY', cargoProviderName: 'Aras Kargo', cargoTrackingNumber: null, primaryImageUrl: null,
  productCount: 1, primaryBarcode: null, lines: [], packageNumber: null, invoiceStatus: 'FATURA_BILINMIYOR', grossAmount: 100, discountAmount: 0, isMicroExport: false,
  connectionId: 'c', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', lineStatuses: {}, returnAmounts: null, rowKey: null, allowedActions: ['APPROVE', 'REJECT']
})
const summary = () => ({
  tabCounts: { ALL: 20, REQUESTED: 0, SHIPPING: 0, ACTION_REQUIRED: 20, APPROVED: 0, REJECTED: 0, REVIEW: 0, DISPUTED: 0, SUSPENDED: 0 },
  platforms: [{ platformCode: 'TRENDYOL', displayName: 'Trendyol' }], cargoProviders: ['Aras Kargo'], reasons: [], invoiceStatuses: [], unfilteredCount: 20,
  metrics: { rejectedAwaitingCargo: 0, rejectedCargoUnknown: 0, todayApproved: 0, todayRejected: 0 }
})

vi.mock('../../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../../shared/api')>()),
  get hubPoll() { return (this as unknown as { hubApi: unknown }).hubApi },
  // A new answer object on every call with the same content, as a poll brings it.
  hubApi: vi.fn(async (path: string) => {
    counter.paths.push(path)
    if (path.startsWith('/returns/summary')) return summary()
    if (path.startsWith('/returns?')) return { items: Array.from({ length: 20 }, (_, index) => claim(index + 1)), nextCursor: null, hasMore: false, totalCount: 20 }
    if (/^\/returns\/r\d+$/.test(path)) return { ...claim(Number(path.slice(10))), lines: [], allowedActions: [], wholeClaimDecision: false, customerRequest: null }
    return []
  }),
  loadAllPages: vi.fn(async (path: string) => ({ items: path.startsWith('/connections') ? [{ id: 'c', platformCode: 'TRENDYOL', status: 'ACTIVE' }] : [], nextCursor: null, hasMore: false }))
}))

import { ReturnsPage } from './ReturnPages'

let host: HTMLElement
let client: QueryClient
beforeEach(async () => {
  counter.paths = []
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  host = render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/returns?status=ACTION_REQUIRED']}><ReturnsPage /></MemoryRouter></QueryClientProvider>).host
  await waitFor(() => box(20))
  await settle()
  counter.rows = 0
})
afterEach(cleanup)

async function settle() { for (let index = 0; index < 8; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) }) }
const box = (index: number) => host.querySelector<HTMLInputElement>(`input[aria-label="#${index} iadesini toplu onaya dahil et"]`)
const barText = () => host.querySelector('.rr-bulk:not(.is-leaving)')?.textContent ?? ''

describe('ReturnsPage row renders', () => {
  it('redraws only the picked row, keeps the bulk count, and the head box still picks every row', async () => {
    act(() => box(4)!.click())
    expect(counter.rows).toBeLessThanOrEqual(2)
    expect(box(4)!.checked).toBe(true)
    expect(box(4)!.closest('tr')!.classList.contains('rr-sel')).toBe(true)
    expect(barText()).toContain('1 iade seçildi')
    act(() => host.querySelector<HTMLInputElement>('input[aria-label="Tüm iadeleri seç"]')!.click())
    expect(host.querySelectorAll('tbody input.rr-cb:checked')).toHaveLength(20)
    expect(barText()).toContain('20 iade seçildi')
  })

  it('redraws no row when a poll brings the same returns and counts', async () => {
    const before = counter.paths.length
    await act(async () => { await client.refetchQueries() })
    await settle()
    expect(counter.paths.length).toBeGreaterThan(before)
    expect(counter.rows).toBe(0)
  })

  it('opens a row menu without redrawing the other rows and its detail reads the row\'s own return', async () => {
    const trigger = box(5)!.closest('tr')!.querySelector<HTMLButtonElement>('.rr-actcell .rr-trigger')!
    act(() => trigger.click())
    expect(counter.rows).toBe(0)
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')).find(button => button.textContent === 'Detaylı Bilgi')!.click())
    await settle()
    // Only the row that opened it draws again, a few times while its detail loads (dialog, loading, loaded); 20 before.
    expect(counter.rows).toBeLessThanOrEqual(5)
    expect(counter.paths).toContain('/returns/r5')
  })

  // The phone's return cards are these same rows: one list at every width, never a table beside a card list.
  it('draws every return once, as one list', () => {
    expect(host.querySelectorAll('table.rr-tbl')).toHaveLength(1)
    expect(host.querySelectorAll('input[aria-label="#1 iadesini toplu onaya dahil et"]')).toHaveLength(1)
  })
})
