import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PhoneDock, taskBarButtons } from '../../../test/phone-dock'

// "Sayfadakileri Seç": picks the visible page's selectable rows (a cancelled order has no box), on a phone in the task bar
// and on a wide screen in the bulk bar.
const order = (id: string, orderNumber: string, derivedStatus = 'NEW') => ({ id, orderNumber, derivedStatus, currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-14T10:00:00Z', lineCount: 1, packageCount: 1, version: 1, connectionId: 'c1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: 'Ayşe Yılmaz', customerTaxOrIdentityNumber: null, orderType: 'STANDARD', isMicroExport: false, shipmentAddress: null, shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'NONE', invoiceId: null, invoiceDocumentUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: `TRK-${id}`, primaryImageUrl: null, productQuantity: 1, lines: null, packages: null })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/orders?')) return { items: [order('o1', 'TY-1'), order('o2', 'TY-2'), order('o3', 'TY-3'), order('o4', 'TY-4', 'CANCELLED')], nextCursor: null, hasMore: false, totalCount: 4 }
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
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(() => { act(() => root.unmount()); container.remove() })

async function settle() { for (let index = 0; index < 8; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
const checkbox = (orderNumber: string) => container.querySelector<HTMLInputElement>(`input[aria-label="Sipariş ${orderNumber} seç"]`)!
const checked = () => Array.from(container.querySelectorAll<HTMLInputElement>('input[aria-label^="Sipariş "][aria-label$=" seç"]')).filter(input => input.checked).length
async function mount(phone: boolean) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const page = <MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter>
  await act(async () => { root.render(<QueryClientProvider client={client}>{phone ? <PhoneDock>{page}</PhoneDock> : page}</QueryClientProvider>) })
  await settle()
}

describe('OrdersPage Sayfadakileri Seç', () => {
  it('on a phone is in the task bar and picks the page rows that have a box, then disappears', async () => {
    await mount(true)
    act(() => checkbox('TY-1').click())
    expect(taskBarButtons(container)).toContain('Sayfadakileri Seç (3)')
    act(() => Array.from(container.querySelectorAll<HTMLButtonElement>('.rv-taskbar-actions button')).find(button => button.textContent?.includes('Sayfadakileri Seç'))!.click())
    expect(checked()).toBe(3)
    expect(taskBarButtons(container).some(label => label.includes('Sayfadakileri Seç'))).toBe(false)
  })

  it('on a wide screen is in the bulk bar and does the same', async () => {
    await mount(false)
    act(() => checkbox('TY-2').click())
    const select = Array.from(container.querySelectorAll<HTMLButtonElement>('.rr-bulk button')).find(button => button.textContent?.includes('Sayfadakileri Seç'))!
    expect(select.textContent).toContain('(3)')
    act(() => select.click())
    expect(checked()).toBe(3)
    expect(Array.from(container.querySelectorAll('.rr-bulk button')).some(button => button.textContent?.includes('Sayfadakileri Seç'))).toBe(false)
  })
})
