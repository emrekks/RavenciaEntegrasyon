import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Scan C-7: a cancelled order hid "Fatura İşlemleri", so its issued invoice could not be seen or cancelled from the row.
// The menu shows again, but nothing on it issues, uploads or hand-sets an invoice of a cancelled order (the manual
// upload and status endpoints do not refuse one); seeing, reading and cancelling the issued invoice stay.
const order = (id: string, orderNumber: string, invoiceStatus: string, invoiceId: string | null, platformCode = 'TRENDYOL') => ({
  id, orderNumber, derivedStatus: 'CANCELLED', currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-20T10:00:00Z', lineCount: 1, packageCount: 1,
  platformCode, platformDisplayName: platformCode === 'SHOPIFY' ? 'Shopify' : 'Trendyol', shipmentDueAt: null, invoiceStatus, invoiceId, invoiceCreationEnabled: true,
  packages: [{ id: `p-${id}`, orderId: id, orderNumber, externalPackageId: '3180000008', status: 'CANCELLED', rawStatus: 'Cancelled', cargoTrackingNumber: null, cargoProviderName: null }] })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/orders?')) return { items: [
      order('o1', '10000000001', 'FATURA_BEKLIYOR', null),
      order('o2', '10000000002', 'FATURA_DUZELTME_GEREKLI', 'inv-2'),
      order('o3', '10000000003', 'FATURA_BEKLIYOR', null, 'SHOPIFY')
    ], nextCursor: null, hasMore: false, totalCount: 3, supportedFilters: [] }
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
import { invoiceInfoUploadHint } from './orders-page/OrderInvoiceModals'

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

async function openInvoiceMenu(orderNumber: string) {
  const row = [...container.querySelectorAll<HTMLTableRowElement>('tbody tr')].find(candidate => candidate.textContent?.includes(orderNumber))!
  const trigger = row.querySelector<HTMLButtonElement>('.rr-inv-trigger')
  expect(trigger, `${orderNumber} Fatura İşlemleri`).not.toBeNull()
  await act(async () => { trigger!.click() })
  const menu = document.querySelector<HTMLElement>('[role="menu"][aria-label$="fatura işlemleri"]')!
  const items = Object.fromEntries([...menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].map(item => [item.textContent!.trim(), item]))
  await act(async () => { trigger!.click() })
  return items
}

describe('OrdersPage cancelled order invoice menu', () => {
  it('shows the menu and offers no invoice write on a cancelled order', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    for (let index = 0; index < 20 && !container.textContent?.includes('10000000003'); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })

    const pending = await openInvoiceMenu('10000000001')
    expect(pending['Fatura Oluştur'].disabled).toBe(true)
    expect(pending['Fatura Oluştur'].title).toContain('İptal edilen siparişe fatura kesilmez')
    expect(pending['Fatura Yükle'].disabled).toBe(true)
    expect(pending['Fatura Bilgileri'].disabled).toBe(false)

    const issued = await openInvoiceMenu('10000000002')
    expect(issued['Fatura İptal Et'].disabled).toBe(false)
    expect(issued['Fatura Bilgileri'].disabled).toBe(false)

    const shopify = await openInvoiceMenu('10000000003')
    expect(shopify['Fatura Durumunu Değiştir'].disabled).toBe(true)
    expect(shopify['Fatura Yükle'].disabled).toBe(true)
  })

  // The phone's detail sheet offers the same reading items and the way to the invoice's cancellation; Fatura Bilgileri
  // drops its "Fatura Yükle ile ekleyin" hint on a cancelled order.
  it('gives the phone detail sheet the reading items and no upload hint', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    for (let index = 0; index < 20 && !container.textContent?.includes('10000000003'); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    const row = [...container.querySelectorAll<HTMLTableRowElement>('tbody tr')].find(candidate => candidate.textContent?.includes('10000000002'))!
    await act(async () => { row.querySelector<HTMLButtonElement>('.rr-detail-open')!.click() })
    const sheet = document.querySelector<HTMLElement>('.rr-sheet')!
    await act(async () => { sheet.querySelector<HTMLButtonElement>('.rr-sheet-trigger')!.click() })
    const items = [...sheet.querySelectorAll<HTMLButtonElement>('.rr-sheet-menu [role="menuitem"]')]
    expect(items.map(item => item.textContent)).toEqual(['Faturayı Gör', 'Fatura Bilgileri', 'Fatura İptal Et'])
    expect(items.every(item => !item.disabled)).toBe(true)
    await act(async () => { items[1].click() })
    expect(document.body.textContent).toContain('Fatura ve Adres Bilgileri')
    expect(document.body.textContent).not.toContain('Fatura Yükle ile siparişe ekleyin')
  })

  // Fatura Yükle stores a PDF only; the hint of an order still to invoice names no picture format (scan D-11).
  it('names PDF as the only file Fatura Yükle takes', () => {
    expect(invoiceInfoUploadHint).toContain('Yalnız PDF')
    expect(invoiceInfoUploadHint).not.toMatch(/JPE?G|PNG/u)
  })
})
