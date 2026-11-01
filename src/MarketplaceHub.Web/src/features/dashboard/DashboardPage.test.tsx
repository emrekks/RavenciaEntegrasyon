import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ failing: new Set<string>() }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const respond = async (path: string) => {
    const route = path.split('?')[0]
    if (apiState.failing.has(route)) throw new Error(`${route} failed`)
    if (route === '/dashboard/bootstrap') return { metrics: { pendingOrders: 4, lateOrders: 1, pendingReturns: 2, pendingUnitsByConnection: { ty: 34, hb: 1 } }, channels: [{ connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol', status: 'ACTIVE' }, { connectionId: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada', status: 'ACTIVE' }] }
    if (route === '/dashboard/cancellations') return { stores: [
      { connectionId: 'ty', cancelledOrders: 2, partialOrders: 1, cancelledUnits: 4, unknownTimeOrders: 0, unknownTimeUnits: 0 },
      { connectionId: 'hb', cancelledOrders: 0, partialOrders: 0, cancelledUnits: 0, unknownTimeOrders: 1, unknownTimeUnits: 3 },
    ] }
    if (route === '/invoice-workspace/summary') return { dueSoonCount: 3, uninvoicedCount: 7 }
    if (route === '/products/summary') return { totalCount: 10, activeCount: 8, outOfStockCount: 1, lowStockCount: 2 }
    if (route === '/dashboard/integration-matrix') return [{ connectionId: 'c1', platformCode: 'TRENDYOL', displayName: 'Trendyol', externalStoreId: '9999', status: 'ACTIVE', cells: {}, issue: 'Son sipariş çekimi hata verdi' }]
    if (route === '/dashboard/performance') return { from: '2026-10-01', to: '2026-10-31', previousFrom: '2026-09-01', previousTo: '2026-09-30', current: [], previous: [], channels: [], partialDay: false, currentEnd: '2026-10-31T20:59:59Z' }
    if (route.endsWith('/instant-notifications')) return { state: 'UNSUPPORTED', endpoint: null, registeredAt: null, lastReceivedAt: null, receivedLast24Hours: 0, message: null }
    throw new Error(`unhandled ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond), loadAllPages: vi.fn(async () => ({ items: [], totalCount: 0 })) }
})

import { DashboardPage, knownCount, matrixJobsLink } from './DashboardPage'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function render() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><DashboardPage /></MemoryRouter></QueryClientProvider>) })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 30)) })
}

const card = (label: string) => Array.from(container.querySelectorAll('.rr-opcard')).find(item => item.querySelector('small')?.textContent === label)?.querySelector('strong')?.textContent

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.failing = new Set()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root.unmount())
  client.clear()
  container.remove()
})

describe('DashboardPage', () => {
  it('shows the counts it read', async () => {
    await render()
    expect(card('Bekleyen Siparişler')).toBe('4')
    expect(card('Bekleyen Faturalar')).toBe('7')
    expect(container.querySelector('.rr-callout.rr-warn')).toBeNull()
  })

  it('places the pending products under the pending orders', async () => {
    await render()
    expect(Array.from(container.querySelectorAll('.rr-opcard small')).map(label => label.textContent)).toEqual([
      'Bekleyen Siparişler', 'Geciken Siparişler', 'Bekleyen İadeler', 'Bekleyen Faturalar',
      'Bekleyen Ürün Adedi', 'Düşük Stok', 'Yaklaşan Faturalar', 'Günlük İptal',
    ])
  })

  it("counts today's whole cancellations, adds products and partial ones, and lists unknown times apart", async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-11-01T09:00:00Z'))
    try { await render() } finally { vi.useRealTimers() }
    const cancel = Array.from(container.querySelectorAll<HTMLAnchorElement>('a.rr-opcard')).find(item => item.querySelector('small')?.textContent === 'Günlük İptal')!
    expect(cancel.querySelector('strong')?.textContent).toBe('2sipariş · 4 ürün · 1 kısmi')
    expect(cancel.getAttribute('href')).toBe('/orders?status=CANCELLED&cancelFrom=2026-11-01&cancelTo=2026-11-01')
    expect(Array.from(cancel.querySelectorAll('.rr-bd-r')).map(row => row.textContent)).toEqual(['Trendyol2 sipariş · 1 kısmi · 4 adet', 'Hepsiburada · Zamanı Bilinmiyor1 sipariş · 3 adet'])
  })

  it('shows unread cancellations as unknown, not 0', async () => {
    apiState.failing = new Set(['/dashboard/cancellations'])
    await render()
    expect(card('Günlük İptal')).toBe('—')
  })

  it('opens the performance panel on today', async () => {
    await render()
    expect(container.querySelector('.rr-seg [aria-checked="true"]')?.textContent).toBe('Bugün')
    expect(container.querySelector('.rr-pf-h p')?.textContent).toBe('Bugün · Tüm Platformlar')
  })

  it('counts the products of the pending orders, splits them by store and opens the pending orders', async () => {
    await render()
    expect(card('Bekleyen Ürün Adedi')).toBe('35')
    const units = Array.from(container.querySelectorAll<HTMLAnchorElement>('a.rr-opcard')).find(item => item.querySelector('small')?.textContent === 'Bekleyen Ürün Adedi')!
    expect(units.getAttribute('href')).toBe('/orders?status=PENDING')
    expect(Array.from(units.querySelectorAll('.rr-bd-r')).map(row => row.textContent)).toEqual(['Trendyol34 adet', 'Hepsiburada1 adet'])
  })

  it('shows an unread invoice summary or bootstrap as unknown, not 0, and says data is missing', async () => {
    apiState.failing = new Set(['/invoice-workspace/summary', '/dashboard/bootstrap'])
    await render()
    expect(card('Bekleyen Faturalar')).toBe('—')
    expect(card('Yaklaşan Faturalar')).toBe('—')
    expect(card('Bekleyen Siparişler')).toBe('—')
    expect(card('Geciken Siparişler')).toBe('—')
    expect(card('Bekleyen Ürün Adedi')).toBe('—')
    expect(card('Düşük Stok')).toBe('2')
    expect(container.querySelector('.rr-callout.rr-warn')?.textContent).toContain('Bazı rapor verileri alınamadı')
  })

  it('opens the failed jobs of the matrix row\'s 7 days and platform', async () => {
    await render()
    const link = Array.from(container.querySelectorAll<HTMLAnchorElement>('.rr-mx-act a')).find(item => item.textContent === 'İncele')!
    expect(link.getAttribute('href')).toBe('/jobs?durum=hata&aralik=7g&platform=trendyol')
  })

  it('keeps a missing count unknown and builds the jobs link with the names İşlem Takibi reads', () => {
    expect(knownCount(false, undefined)).toBeNull()
    expect(knownCount(true, 5)).toBeNull()
    expect(knownCount(false, 0)).toBe(0)
    expect(matrixJobsLink('HEPSIBURADA')).toBe('/jobs?durum=hata&aralik=7g&platform=hepsiburada')
  })
})
