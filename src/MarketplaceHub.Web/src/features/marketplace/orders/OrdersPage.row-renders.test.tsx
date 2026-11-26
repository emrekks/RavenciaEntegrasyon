import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Siparişler with 50 rows (26 Nov 2026): every pick, window or poll redrew all 50 order rows (0.55 s per pick, 1.5 s per
// tab on a 4x slowed phone). A row now draws again only when its own inputs change. Counted, not timed: each order row
// render calls useProductLines once.
const counter = vi.hoisted(() => ({ rows: 0 }))
vi.mock('../product-lines', async importOriginal => {
  const original = await importOriginal<typeof import('../product-lines')>()
  return { ...original, useProductLines: (total: number) => { counter.rows++; return original.useProductLines(total) } }
})

const order = (index: number) => ({ id: `o${index}`, orderNumber: `TY-${index}`, derivedStatus: 'NEW', currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-14T10:00:00Z', lineCount: 1, packageCount: 1, version: 1, connectionId: 'c1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: 'Ayşe Yılmaz', customerTaxOrIdentityNumber: null, orderType: 'STANDARD', isMicroExport: false, shipmentAddress: null, shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'NONE', invoiceId: null, invoiceDocumentUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: `TRK-${index}`, primaryImageUrl: null, productQuantity: 1, lines: null, packages: [{ id: `p${index}`, orderId: `o${index}`, externalPackageId: `PK${index}`, status: 'NEW', statusOccurredAt: '2026-11-14T10:00:00Z', cargoProviderName: 'Yurtiçi', cargoTrackingNumber: `TRK-${index}` }] })
const requested: Array<{ path: string; init?: RequestInit }> = []

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  // A new answer object on every call with the same content, as a poll brings it.
  const answer = async (path: string, init?: RequestInit) => {
    requested.push({ path, init })
    if (path.startsWith('/orders?')) return { items: Array.from({ length: 50 }, (_, index) => order(index + 1)), nextCursor: null, hasMore: false, totalCount: 50 }
    if (path.startsWith('/orders/facets')) return { platforms: {}, cargos: {} }
    if (path.startsWith('/orders/summary')) return { new: 50, processing: 0, tabCounts: {} }
    if (path.startsWith('/dashboard/cancellations')) return { stores: [] }
    if (path.startsWith('/dashboard/bootstrap')) return { channels: [] }
    if (path.startsWith('/settings/shipping-label')) return { settings: null, version: 0 }
    if (path.startsWith('/connections')) return { items: [{ id: 'c1', platformCode: 'TRENDYOL', status: 'ACTIVE', invoiceCreationEnabled: true }], nextCursor: null, hasMore: false }
    if (/^\/orders\/o\d+\/instant-process$/.test(path)) return { id: 'p3', orderId: 'o3', status: 'PROCESSING', statusOccurredAt: '2026-11-14T11:00:00Z' }
    return null
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { OrdersPage } from './OrdersPage'

let container: HTMLDivElement
let root: Root
let client: QueryClient
beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
})
afterEach(() => { act(() => root.unmount()); container.remove(); document.body.innerHTML = '' })

async function settle(rounds = 12) { for (let index = 0; index < rounds; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) }) }
async function openPage() {
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
  expect(container.querySelectorAll('tbody tr input[aria-label$=" seç"]')).toHaveLength(50)
  counter.rows = 0
}
const row = (number: number) => container.querySelector<HTMLInputElement>(`input[aria-label="Sipariş TY-${number} seç"]`)!.closest('tr')!
const checkedCount = () => container.querySelectorAll<HTMLInputElement>('tbody input.rr-cb:checked').length
const menuItem = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')).find(button => button.textContent === label)!

describe('OrdersPage row renders', () => {
  it('redraws only the picked row, keeps the bulk count, and a select-all still picks the page', async () => {
    await openPage()
    act(() => row(2).querySelector<HTMLInputElement>('input.rr-cb')!.click())
    expect(counter.rows).toBeLessThanOrEqual(2)
    expect(checkedCount()).toBe(1)
    expect(row(2).classList.contains('rr-sel')).toBe(true)
    expect(container.querySelector('.rr-bulk')?.textContent).toContain('1 sipariş seçildi')

    counter.rows = 0
    act(() => row(2).querySelector<HTMLInputElement>('input.rr-cb')!.click())
    expect(counter.rows).toBeLessThanOrEqual(2)
    expect(checkedCount()).toBe(0)

    act(() => container.querySelector<HTMLInputElement>('input[aria-label="Sayfadaki siparişleri seç"]')!.click())
    expect(checkedCount()).toBe(50)
    expect(container.querySelector('.rr-bulk')?.textContent).toContain('50 sipariş seçildi')
    expect(container.querySelector('button[aria-label^="Etiketleri Yazdır"]')?.getAttribute('aria-label')).toBe('Etiketleri Yazdır (50)')
  })

  it('redraws no row when a poll brings the same orders and counts', async () => {
    await openPage()
    const before = requested.length
    await act(async () => { await client.refetchQueries() })
    await settle(4)
    expect(requested.length).toBeGreaterThan(before)
    expect(counter.rows).toBe(0)
  })

  it('opens a row menu and its windows without redrawing the other rows, with the row\'s own order', async () => {
    await openPage()
    act(() => row(3).querySelector<HTMLButtonElement>('.rr-inv-trigger')!.click())
    expect(counter.rows).toBe(0)
    act(() => menuItem('Fatura Bilgileri').click())
    await settle(4)
    expect(counter.rows).toBeLessThanOrEqual(1)
    // The window reads the order it was opened from.
    expect(requested.some(call => call.path === '/orders/o3')).toBe(true)
    expect(document.body.textContent).toContain('Fatura ve Adres Bilgileri')
  })

  it('takes the menu\'s order into work with the same request as before', async () => {
    await openPage()
    act(() => row(3).querySelector<HTMLButtonElement>('.rr-actcell .rr-trigger')!.click())
    await act(async () => { menuItem('İşleme Al').click() })
    await settle(4)
    const call = requested.find(entry => entry.path === '/orders/o3/instant-process')
    expect(call?.init?.method).toBe('POST')
    expect(new Headers(call?.init?.headers).get('Idempotency-Key')).toBeTruthy()
    expect(requested.filter(entry => entry.path.endsWith('/instant-process'))).toHaveLength(1)
  })

  // The phone's order cards are these same rows (orders-sade.css lays them out as cards up to 760 px): one list in the
  // page at every width, never a table beside a card list.
  it('draws every order once, as one list', async () => {
    await openPage()
    expect(container.querySelectorAll('table.rr-tbl')).toHaveLength(1)
    expect(container.querySelectorAll('input[aria-label="Sipariş TY-1 seç"]')).toHaveLength(1)
  })
})
