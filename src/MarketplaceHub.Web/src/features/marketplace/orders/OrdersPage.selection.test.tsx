import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// W6 (scan 2026-11-15) pins today's behaviour: Siparişler clears its selection on every list change (sort included),
// unlike Faturalar (kept when only the sort changes on page 1) and Ürünler (kept across pages on purpose). Whether they
// should agree waits for the owner.
const requested: string[] = []
const order = (id: string, orderNumber: string) => ({ id, orderNumber, derivedStatus: 'NEW', currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-14T10:00:00Z', lineCount: 1, packageCount: 1, version: 1, connectionId: 'c1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: 'Ayşe Yılmaz', customerTaxOrIdentityNumber: null, orderType: 'STANDARD', isMicroExport: false, shipmentAddress: null, shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'NONE', invoiceId: null, invoiceDocumentUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: `TRK-${id}`, primaryImageUrl: null, productQuantity: 1, lines: null, packages: null })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    requested.push(path)
    if (path.startsWith('/orders?')) return { items: [order('o1', 'TY-1'), order('o2', 'TY-2')], nextCursor: 'next', hasMore: true, totalCount: 120 }
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

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function settle() { for (let index = 0; index < 8; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
const checkbox = (orderNumber: string) => container.querySelector<HTMLInputElement>(`input[aria-label="Sipariş ${orderNumber} seç"]`)!
const checked = () => Array.from(container.querySelectorAll<HTMLInputElement>('input[aria-label^="Sipariş "][aria-label$=" seç"]')).filter(input => input.checked).length

describe('OrdersPage selection', () => {
  it('clears on a sort change and on a page change, and the bulk bar counts only the selected rows of the page', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    await settle()
    act(() => checkbox('TY-1').click())
    expect(checked()).toBe(1)
    expect(container.querySelector('.rr-bulk')?.textContent).toContain('1 sipariş seçildi')
    expect(container.querySelector('button[aria-label^="Etiketleri Yazdır"]')?.getAttribute('aria-label')).toBe('Etiketleri Yazdır (1)')

    const sortField = Array.from(container.querySelectorAll('.rr-fld')).find(field => field.querySelector('small')?.textContent === 'Sıralama')!
    act(() => sortField.querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(option => option.textContent === 'En Eski Siparişler')!.click())
    const apply = Array.from(container.querySelectorAll<HTMLButtonElement>('.rr-fbtns button')).find(button => button.textContent === 'Filtrele')
    if (apply && !apply.disabled) act(() => apply.click())
    await settle()
    expect(requested.filter(path => path.startsWith('/orders?')).at(-1)).toContain('sort=DATE_ASC')
    expect(checked()).toBe(0)

    act(() => checkbox('TY-2').click())
    expect(checked()).toBe(1)
    act(() => container.querySelector<HTMLButtonElement>('button[aria-label="Sonraki"]')!.click())
    await settle()
    expect(requested.filter(path => path.startsWith('/orders?')).at(-1)).toContain('after=next')
    expect(checked()).toBe(0)
  })
})
