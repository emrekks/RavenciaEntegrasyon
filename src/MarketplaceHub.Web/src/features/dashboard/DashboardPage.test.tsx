import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ failing: new Set<string>() }))
const bootstrapOverride = vi.hoisted(() => ({ units: undefined as Record<string, number> | null | undefined, due: 3 as number | null }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const respond = async (path: string) => {
    const route = path.split('?')[0]
    if (apiState.failing.has(route)) throw new Error(`${route} failed`)
    if (route === '/dashboard/bootstrap') return { metrics: { pendingOrders: 4, lateOrders: 1, pendingReturns: 2, pendingByConnection: { ty: 3, hb: 1 }, pendingUnitsByConnection: bootstrapOverride.units === undefined ? { ty: 34, hb: 1 } : bootstrapOverride.units, pendingByCargo: [{ cargo: 'Yurtiçi Kargo Marketplace', orders: 3, units: 34 }, { cargo: '', orders: 1, units: 1 }] }, channels: [{ connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol', status: 'ACTIVE' }, { connectionId: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada', status: 'ACTIVE' }] }
    if (route === '/dashboard/cancellations') return { stores: [
      { connectionId: 'ty', cancelledOrders: 2, partialOrders: 1, cancelledUnits: 4, unknownTimeOrders: 0, unknownTimeUnits: 0 },
      { connectionId: 'hb', cancelledOrders: 0, partialOrders: 0, cancelledUnits: 0, unknownTimeOrders: 1, unknownTimeUnits: 3 },
    ] }
    if (route === '/invoice-workspace/summary') return { dueSoonCount: bootstrapOverride.due ?? undefined, uninvoicedCount: 7 }
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
  bootstrapOverride.units = undefined
  bootstrapOverride.due = 3
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
    expect(card('Bekleyen Siparişler')).toBe('4Paket | 35 Adet Ürün')
    expect(card('Faturalar')).toBe('7Bekleyen | 3 Yaklaşan')
    expect(container.querySelector('.rr-callout.rr-warn')).toBeNull()
  })

  // Geciken Siparişler is bad-toned only with something late; the mock has 1 late and 0 of nothing else bad.
  it('tones the late orders card only when there are late orders', async () => {
    await render()
    const toned = (label: string) => Array.from(container.querySelectorAll('.rr-opcard')).find(item => item.querySelector('small')?.textContent === label)?.classList.contains('rr-bad')
    expect(toned('Geciken Siparişler')).toBe(true)
    expect(toned('Bekleyen İadeler')).toBe(false)
  })

  it('has no separate pending products card', async () => {
    await render()
    expect(Array.from(container.querySelectorAll('.rr-opcard small')).map(label => label.textContent)).toEqual([
      'Bekleyen Siparişler', 'Geciken Siparişler', 'Bekleyen İadeler', 'Faturalar',
      'İptal · 1 Kısmi İptal', 'Düşük Stok',
    ])
  })

  it("counts today's whole cancellations, shows partial ones beside the label, and lists unknown times apart", async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-11-01T09:00:00Z'))
    try { await render() } finally { vi.useRealTimers() }
    const cancel = Array.from(container.querySelectorAll<HTMLAnchorElement>('a.rr-opcard')).find(item => item.querySelector('small')?.textContent?.startsWith('İptal'))!
    expect(cancel.querySelector('strong')?.textContent).toBe('2')
    expect(cancel.getAttribute('href')).toBe('/orders?status=CANCELLED&cancelFrom=2026-11-01&cancelTo=2026-11-01')
    expect(Array.from(cancel.querySelectorAll('.rr-bd-r')).map(row => row.textContent)).toEqual(['Trendyol2 sipariş · 1 kısmi · 4 adet', 'Hepsiburada · Zamanı Bilinmiyor1 sipariş · 3 adet'])
  })

  it('shows unread cancellations as unknown, not 0', async () => {
    apiState.failing = new Set(['/dashboard/cancellations'])
    await render()
    expect(card('İptal')).toBe('—')
  })

  it('opens the performance panel on today', async () => {
    await render()
    expect(container.querySelector('.rr-seg [aria-checked="true"]')?.textContent).toBe('Bugün')
    expect(container.querySelector('.rr-pf-h p')?.textContent).toBe('Bugün · Tüm Platformlar')
  })

  const pendingCard = () => Array.from(container.querySelectorAll<HTMLAnchorElement>('a.rr-opcard')).find(item => item.querySelector('small')?.textContent === 'Bekleyen Siparişler')!

  it('reads "N Paket | M Adet Ürün" in one card, splits both by store and opens the pending orders', async () => {
    await render()
    expect(pendingCard().querySelector('strong')?.textContent).toBe('4Paket | 35 Adet Ürün')
    expect(pendingCard().getAttribute('href')).toBe('/orders?status=PENDING')
    expect(Array.from(pendingCard().querySelectorAll('.rr-bd-r')).map(row => row.textContent)).toEqual(['Trendyol3 paket · 34 adet', 'Hepsiburada1 paket · 1 adet', 'Yurtiçi Kargo3 paket · 34 adet', 'Kargo Belirlenmedi1 paket · 1 adet'])
    expect(pendingCard().querySelector('.rr-bd-first')?.getAttribute('data-head')).toBe('Kargo Firmasına Göre')
  })

  it('leaves the units out while they are unknown, never as 0', async () => {
    bootstrapOverride.units = null
    await render()
    expect(pendingCard().querySelector('strong')?.textContent).toBe('4Paket')
    expect(Array.from(pendingCard().querySelectorAll('.rr-bd-r:not(:has(.rr-bd-cargo))')).map(row => row.textContent)).toEqual(['Trendyol3 paket', 'Hepsiburada1 paket'])
  })

  it('reads "N Bekleyen | M Yaklaşan" in one wide invoice card that opens the pending invoices', async () => {
    await render()
    const invoice = Array.from(container.querySelectorAll<HTMLAnchorElement>('a.rr-opcard')).find(item => item.querySelector('small')?.textContent === 'Faturalar')!
    expect(invoice.classList.contains('rr-opcard-wide')).toBe(false)
    expect(invoice.getAttribute('href')).toBe('/invoices?tab=UNINVOICED')
    expect(invoice.querySelector('a')).toBeNull()
  })

  it('leaves the due count out while it is unknown, never as 0', async () => {
    bootstrapOverride.due = null
    await render()
    expect(card('Faturalar')).toBe('7Bekleyen')
  })

  it('shows an unread invoice summary or bootstrap as unknown, not 0, and says data is missing', async () => {
    apiState.failing = new Set(['/invoice-workspace/summary', '/dashboard/bootstrap'])
    await render()
    expect(card('Faturalar')).toBe('—')
    expect(card('Bekleyen Siparişler')).toBe('—')
    expect(card('Geciken Siparişler')).toBe('—')
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
