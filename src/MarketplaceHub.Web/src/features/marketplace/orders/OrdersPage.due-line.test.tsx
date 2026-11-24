import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Owner, 23 Nov 2026: the phone order card shows the package number and the shipping deadline (the same "Termin:" line the
// desktop row has). The colour classes follow the desktop rule: is-late for under 24 hours or late, is-soon only for
// time still left, a returned order has no deadline line, a long number (PttAVM) is marked for the smaller phone type.
const hour = 3_600_000
const order = (id: string, orderNumber: string, derivedStatus: string, dueInHours: number | null, extra: Record<string, unknown> = {}) => ({
  id, orderNumber, derivedStatus, currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: new Date(Date.now() - 30 * hour).toISOString(), lineCount: 1, packageCount: 1,
  platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', shipmentDueAt: dueInHours === null ? null : new Date(Date.now() + dueInHours * hour).toISOString(),
  packages: [{ id: `p-${id}`, orderId: id, orderNumber, externalPackageId: '3180000008', status: derivedStatus, rawStatus: 'Created', cargoTrackingNumber: null, cargoProviderName: null }], ...extra })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/orders?')) return { items: [
      order('o1', '10000000001', 'NEW', 40),
      order('o2', '10000000002', 'NEW', 8),
      order('o3', '10000000003', 'PROCESSING', -50),
      order('o4', '10000000004', 'RETURNED', -50),
      order('o5', 'PTTEM-UTEST0001-181026', 'NEW', 40, { platformCode: 'PTTAVM', platformDisplayName: 'PttAVM' })
    ], nextCursor: null, hasMore: false, totalCount: 5, supportedFilters: ['labelPrinted'] }
    if (path.startsWith('/orders/facets')) return { platforms: {}, cargos: {} }
    if (path.startsWith('/orders/summary')) return {}
    if (path.startsWith('/dashboard/cancellations')) return { stores: [] }
    if (path.startsWith('/dashboard/bootstrap')) return { channels: [] }
    if (path.startsWith('/returns/')) return {}
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
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('OrdersPage deadline line', () => {
  it('writes the remaining time with the desktop colour rule and keeps a returned order free of it', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    for (let index = 0; index < 20 && !container.textContent?.includes('10000000004'); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    const rows = [...container.querySelectorAll<HTMLTableRowElement>('tbody tr')]
    const due = (number: string) => rows.find(row => row.textContent?.includes(number))!.querySelector('.rr-ordercell small.rr-due')
    expect(due('10000000001')!.textContent).toMatch(/^Termin: 1 gün/u)
    expect(due('10000000001')!.className).toBe('rr-due')
    expect(due('10000000002')!.className).toContain('is-late')
    expect(due('10000000002')!.className).toContain('is-soon')
    expect(due('10000000003')!.textContent).toContain('gün gecikti')
    expect(due('10000000003')!.className).toContain('is-late')
    expect(due('10000000003')!.className).not.toContain('is-soon')
    expect(due('10000000004')).toBeNull()
    expect(rows.find(row => row.textContent?.includes('10000000004'))!.querySelector('.rr-ordercell')!.textContent).not.toContain('gün gecikti')
    expect(rows.find(row => row.textContent?.includes('PTTEM-UTEST0001-181026'))!.querySelector('.rr-on-no')!.className).toContain('rr-on-long')
    expect(rows.find(row => row.textContent?.includes('10000000001'))!.querySelector('.rr-on-no')!.className).not.toContain('rr-on-long')
  })
})
