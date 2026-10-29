import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, render, waitFor } from '../../../test/render'

const api = vi.hoisted(() => ({ paths: [] as string[] }))
const claim = (id: string, extra: Record<string, unknown> = {}) => ({
  id, externalClaimId: `CL-${id}`, orderNumber: `#${id}`, status: 'ACTION_REQUIRED', rawStatus: 'WaitingInAction', reasonText: 'Beden küçük', reasonCode: null, actionDueAt: null, version: 1,
  customerName: `Alıcı ${id}`, orderedAt: '2026-10-10T08:00:00Z', orderAmount: 100, currency: 'TRY', cargoProviderName: 'Aras Kargo', cargoTrackingNumber: null, primaryImageUrl: null,
  productCount: 1, primaryBarcode: null, lines: [], packageNumber: null, invoiceStatus: 'FATURA_BILINMIYOR', grossAmount: 100, discountAmount: 0, isMicroExport: false,
  connectionId: 'c', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', lineStatuses: {}, returnAmounts: null, rowKey: null, ...extra
})
// Trendyol return 11738971444 as Trendyol's own panel shows it: 2 × 799,00 sold, 80,98 off, 1.517,02 invoiced.
const trendyolAmounts = { saleAmount: 1598, discountAmount: 80.98, invoicedAmount: 1517.02, refundAmount: null }
const returnedLine = {
  id: 'l1', sku: 'MZ035DYD25', barcode: 'MZ035DYD25', title: 'Penye Bluz', quantity: 2, orderedQuantity: 2, cancelledQuantity: 0, shippedQuantity: 2, deliveredQuantity: 2, returnedQuantity: 2,
  unitPrice: 758.51, vatRate: 10, rawStatus: 'Delivered', variantId: null, modelCode: null, optionSignature: null, imageUrl: null, returnAmounts: trendyolAmounts
}
const summary = {
  tabCounts: { ALL: 41, REQUESTED: 3, SHIPPING: 5, ACTION_REQUIRED: 7, APPROVED: 22, REJECTED: 4, REVIEW: 0, DISPUTED: 0, SUSPENDED: 0 },
  platforms: [{ platformCode: 'TRENDYOL', displayName: 'Trendyol' }], cargoProviders: ['Yurtiçi', 'Aras Kargo'],
  reasons: [{ value: 'SIZE', reasonCode: 'SIZE', reasonText: null }], invoiceStatuses: ['FATURA_BILINMIYOR'], unfilteredCount: 41
}

vi.mock('../../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../../shared/api')>()),
  hubApi: vi.fn(async (path: string) => {
    api.paths.push(path)
    const params = new URLSearchParams(path.split('?')[1])
    if (path.startsWith('/returns/summary')) return summary
    if (path.startsWith('/returns?')) {
      if (params.get('after') === 'next-1') return { items: [claim('21')], nextCursor: null, hasMore: false, totalCount: 41 }
      if (params.get('page') === '3') return { items: [claim('41')], nextCursor: null, hasMore: false, totalCount: 41 }
      return { items: [claim('1', { returnAmounts: trendyolAmounts, lines: [returnedLine] }), claim('2', { rowKey: '2:waiting' })], nextCursor: 'next-1', hasMore: true, totalCount: 41 }
    }
    return []
  }),
  loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false }))
}))

import { ReturnsPage } from './ReturnPages'

beforeEach(() => { api.paths = [] })
afterEach(cleanup)

function Address() { const location = useLocation(); return <output data-testid="address">{location.search}</output> }
function renderPage(initialEntry = '/returns') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 60_000 } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[initialEntry]}><ReturnsPage /><Address /></MemoryRouter></QueryClientProvider>)
}

describe('returns page', () => {
  it('reads one page of cards and the tab counts from the server instead of every return', async () => {
    const { host } = renderPage()
    await waitFor(() => host.textContent?.includes('#2'))
    await waitFor(() => host.querySelector('.rr-metrics')?.textContent?.includes('22'))

    const returnRequests = api.paths.filter(path => path.startsWith('/returns'))
    expect(returnRequests).toContain('/returns?limit=20')
    expect(returnRequests).toContain('/returns/summary?')
    expect(returnRequests).toHaveLength(2)
    const metrics = host.querySelector('.rr-metrics')!.textContent!
    expect(metrics).toContain('7')
    expect(metrics).toContain('41')
    expect(host.textContent).toContain('Toplam 41 iade bilgisi')
    // The return's amounts as the marketplace wrote them, never ones calculated from the order.
    expect(host.textContent).not.toContain('Siparişten hesaplanan')
    expect(host.textContent).not.toContain('Faturalanacak Tutar')
  })

  it('shows the amounts the marketplace wrote for the return and a dash where it wrote none', async () => {
    const { host } = renderPage()
    await waitFor(() => host.textContent?.includes('#2'))
    const [first, second] = Array.from(host.querySelectorAll<HTMLElement>('.rr-invcell'))
    expect(first.textContent).toContain('Satış Tutarı')
    expect(first.textContent).toContain('1.598,00')
    expect(first.textContent).toContain('İndirim Tutarı')
    expect(first.textContent).toContain('80,98')
    expect(first.textContent).toContain('Faturalanmış Tutar')
    expect(first.textContent).toContain('1.517,02')
    // The unit price column shows the sale price the marketplace sold at, as Trendyol does (799,00, not 758,51).
    const prices = host.querySelector<HTMLElement>('.rr-price-col')!.textContent!
    expect(prices).toContain('799,00')
    expect(prices).not.toContain('758,51')
    // No amount from the marketplace: unknown, not calculated.
    expect(second.textContent).toContain('İade Tutarı')
    expect(second.textContent).toContain('—')
    expect(second.querySelector('[title="Pazaryeri iade tutarını bildirmedi."]')).not.toBeNull()
  })

  it('continues with the cursor and jumps to a page by number', async () => {
    const { host } = renderPage()
    await waitFor(() => host.textContent?.includes('#2'))
    act(() => { byText<HTMLButtonElement>(host, 'button[aria-label="Sonraki"]', '').click() })
    await waitFor(() => host.textContent?.includes('#21'))
    expect(api.paths.at(-1)).toBe('/returns?limit=20&after=next-1')

    act(() => { byText<HTMLButtonElement>(host, '.rr-pages button', '3').click() })
    await waitFor(() => host.textContent?.includes('#41'))
    expect(api.paths.at(-1)).toBe('/returns?limit=20&page=3')
    // Moving between pages never asks for the summary again.
    expect(api.paths.filter(path => path.startsWith('/returns/summary'))).toHaveLength(1)
  })

  it('opens the page, page size and filters the address carries and keeps them there (W-F11)', async () => {
    const { host } = renderPage('/returns?status=APPROVED&page=3&size=50&cargo=Aras%20Kargo&from=2026-10-01&to=2026-10-10')
    await waitFor(() => api.paths.some(path => path.startsWith('/returns?')))
    const listRequest = api.paths.find(path => path.startsWith('/returns?'))!
    const params = new URLSearchParams(listRequest.split('?')[1])
    expect(params.get('page')).toBe('3')
    expect(params.get('limit')).toBe('50')
    expect(params.get('status')).toBe('APPROVED')
    expect(params.get('cargo')).toBe('Aras Kargo')
    expect(params.get('dateFrom')).toBeTruthy()
    await waitFor(() => host.textContent?.includes('#41'))
    const address = new URLSearchParams(host.querySelector('[data-testid="address"]')!.textContent!)
    expect(address.get('page')).toBe('3')
    expect(address.get('size')).toBe('50')
    expect(address.get('cargo')).toBe('Aras Kargo')

    act(() => { byText<HTMLButtonElement>(host, '.rr-pages button', '1').click() })
    await waitFor(() => !new URLSearchParams(host.querySelector('[data-testid="address"]')!.textContent!).has('page'))
  })
})
