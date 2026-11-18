import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Owner, 18 Nov 2026: the Yeniden Gönderimler row of an exchange shows the exchange package Trendyol named (package,
// carrier, tracking, its tracking link) and the first package as "İlk Paket"; no invoice actions or label print for it.
// An exchange only the owner marked shows that Trendyol named no package.
const order = (id: string, orderNumber: string, exchangeResend: unknown) => ({ id, orderNumber, derivedStatus: 'DELIVERED', currency: 'TRY', grossAmount: 100, discountAmount: 0, netAmount: 100, orderedAt: '2026-11-09T10:00:00Z', lineCount: 1, packageCount: 1, version: 1, connectionId: 'c1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: 'Ayşe Yılmaz', customerTaxOrIdentityNumber: null, orderType: 'STANDARD', isMicroExport: false, shipmentAddress: null, shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'NONE', invoiceId: null, invoiceDocumentUrl: null, cargoProviderName: 'PTT Kargo', cargoTrackingNumber: 'OLD-TRK', primaryImageUrl: null, productQuantity: 1, lines: null,
  packages: [{ id: `p-${id}`, orderId: id, orderNumber, externalPackageId: '4326271446', status: 'DELIVERED', rawStatus: 'Delivered', cargoTrackingNumber: 'OLD-TRK', cargoProviderName: 'PTT Kargo' }], exchangeResend })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    if (path.startsWith('/orders?')) return { items: [
      order('o1', '11805950639', { packageId: '4360616611', cargoProviderName: 'Trendyol Express Marketplace', cargoTrackingNumber: 'NEW-TRK', cargoTrackingLink: 'https://tracking.example.test/new', shipped: true }),
      order('o2', '11805950640', { packageId: null, cargoProviderName: null, cargoTrackingNumber: null, cargoTrackingLink: null, shipped: false })
    ], nextCursor: null, hasMore: false, totalCount: 2, supportedFilters: ['labelPrinted'] }
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

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('OrdersPage exchange package', () => {
  it('shows the exchange package with the first package beside it, and a note when Trendyol named none', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders?status=RESENT']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    for (let index = 0; index < 20 && !container.textContent?.includes('11805950640'); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    const rows = [...container.querySelectorAll<HTMLTableRowElement>('tbody tr')]
    const exchange = rows.find(row => row.textContent?.includes('11805950639'))!
    const marked = rows.find(row => row.textContent?.includes('11805950640'))!
    const orderCell = exchange.querySelector('.rr-ordercell')!.textContent!
    expect(orderCell).toContain('4360616611')
    expect(orderCell).toContain('İlk Paket:')
    expect(orderCell).toContain('4326271446')
    const cargo = exchange.querySelector('[data-l="Kargo"]')!
    expect(cargo.textContent).toContain('NEW-TRK')
    expect(cargo.querySelector('a[href="https://tracking.example.test/new"]')).not.toBeNull()
    expect(cargo.textContent).toContain('İlk Paket:')
    expect(exchange.querySelector('[data-l="Fatura"] .rr-inv-trigger')).toBeNull()
    expect(exchange.querySelector('[data-l="Fatura"]')!.textContent).toContain('İlk paketin faturası')
    expect(marked.querySelector('.rr-ordercell')!.textContent).toContain('Değişim paketi bilgisi Trendyol\'dan gelmedi')
    expect(marked.querySelector('[data-l="Fatura"] .rr-inv-trigger')).not.toBeNull()
  })
})
