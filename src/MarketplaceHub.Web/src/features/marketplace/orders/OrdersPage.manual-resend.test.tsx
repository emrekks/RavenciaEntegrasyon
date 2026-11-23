import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { queryKeys } from '../../../shared/api/query-keys'

// Owner, 23 Nov 2026: an order added to Yeniden Gönderimler by hand reads "Elle Eklendi" and its state, and İşlemler offers
// "Kargoya Verildi Olarak İşaretle" (or "İşareti Kaldır" once marked), panel only.
const order = (id: string, orderNumber: string, manualResend: unknown) => ({ id, orderNumber, derivedStatus: 'DELIVERED', currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-09T10:00:00Z', lineCount: 1, packageCount: 1, version: 1, connectionId: 'c1', platformCode: 'HEPSIBURADA', platformDisplayName: 'Hepsiburada', customerName: 'Ayşe Yılmaz', customerTaxOrIdentityNumber: null, orderType: 'STANDARD', isMicroExport: false, shipmentAddress: null, shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'NONE', invoiceId: null, invoiceDocumentUrl: null, cargoProviderName: 'Aras Kargo', cargoTrackingNumber: 'TRK-1', primaryImageUrl: null, productQuantity: 1, lines: null,
  packages: [{ id: `p-${id}`, orderId: id, orderNumber, externalPackageId: '3000000001', status: 'DELIVERED', rawStatus: 'Delivered', cargoTrackingNumber: 'TRK-1', cargoProviderName: 'Aras Kargo' }], manualResend })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/orders?')) return { items: [
      order('o1', '1000000201', { id: 'm1', version: 3, shipped: false, shippedAt: null }),
      order('o2', '1000000202', { id: 'm2', version: 5, shipped: true, shippedAt: '2026-11-23T12:00:00Z' })
    ], nextCursor: null, hasMore: false, totalCount: 2, supportedFilters: ['labelPrinted'] }
    if (path.startsWith('/orders/facets')) return { platforms: {}, cargos: {} }
    if (path.startsWith('/orders/summary')) return {}
    if (path.startsWith('/dashboard/cancellations')) return { stores: [] }
    if (path.startsWith('/dashboard/bootstrap')) return { channels: [] }
    if (path.startsWith('/settings/shipping-label')) return { settings: null, version: 0 }
    if (path.startsWith('/settings/resend-reminder')) return { count: 0, version: 0 }
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

describe('OrdersPage hand-added re-shipment', () => {
  it('tags the row Elle Eklendi with its state and marks it handed to the carrier', async () => {
    const { hubApi } = await import('../../../shared/api')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(queryKeys.me(), { role: 'OWNER' })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders?status=RESENT']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    for (let index = 0; index < 20 && !container.textContent?.includes('1000000202'); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    const rows = [...container.querySelectorAll<HTMLTableRowElement>('tbody tr')]
    const waiting = rows.find(row => row.textContent?.includes('1000000201'))!
    const shipped = rows.find(row => row.textContent?.includes('1000000202'))!
    expect(waiting.querySelector('.rr-ordercell')!.textContent).toContain('Elle Eklendi')
    expect(waiting.querySelector('.rr-ordercell')!.textContent).toContain('Yeniden Gönderim: Kargoya Verilmesi Bekleniyor')
    expect(shipped.querySelector('.rr-ordercell')!.textContent).toContain('Yeniden Gönderim: Kargoya Verildi (Elle)')

    const menuItems = async (row: HTMLTableRowElement) => {
      await act(async () => { row.querySelector<HTMLButtonElement>('[data-l="İşlemler"] .rr-trigger')!.click() })
      return [...document.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')]
    }
    expect((await menuItems(shipped)).find(item => item.textContent === 'İşareti Kaldır')).toBeDefined()
    await act(async () => { shipped.querySelector<HTMLButtonElement>('[data-l="İşlemler"] .rr-trigger')!.click() })

    const mark = (await menuItems(waiting)).find(item => item.textContent === 'Kargoya Verildi Olarak İşaretle')!
    await act(async () => { mark.click() })
    const confirm = [...document.querySelectorAll<HTMLButtonElement>('.rr-primary')].find(button => button.textContent === 'Kargoya Verildi Olarak İşaretle')!
    await act(async () => { confirm.click() })
    const call = vi.mocked(hubApi).mock.calls.find(([path]) => path === '/manual-resends/m1/shipped')
    expect(call).toBeDefined()
    const init = call![1] as RequestInit & { headers: Record<string, string> }
    expect(init.method).toBe('POST')
    expect(init.headers['If-Match']).toBe('"v3"')
    expect(JSON.parse(init.body as string)).toEqual({ marked: true })
  })
})
