import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ReportGroup, ReportSales, ReportSummary } from './report-format'

const group = (key: string, label: string, values: Partial<ReportGroup> = {}): ReportGroup => ({
  key, label, known: label !== 'Bilinmiyor', isTest: false, orders: 1, soldQuantity: 1, returnedQuantity: 0, returnClaims: 0, returnRate: 0, ordersWithAmount: 1,
  grossSales: 100, netRevenue: 100, estimatedCommission: 0, commissionCoveredRevenue: 0, children: [], split: [], ...values
})
const summary = (values: Partial<ReportSummary> = {}): ReportSummary => ({
  orders: 4, cancelledOrders: 1, cancelRate: 0.2, soldQuantity: 5, returnedQuantity: 1, keptQuantity: 4, returnClaims: 1, returnRate: 0.2, ordersWithAmount: 3,
  grossSales: 350, returnedAmount: 100, netRevenue: 250, averageOrderValue: 116.67, ordersWithoutCity: 1, ordersWithoutAmount: 1, otherCurrencyOrders: 0, ...values
})
const report: ReportSales = {
  from: '2026-10-01', to: '2026-10-22', previousFrom: '2026-09-09', previousTo: '2026-09-30', granularity: 'day', includesTest: false, generatedAt: '2026-10-22T10:00:00Z',
  summary: summary(), previousSummary: summary({ orders: 2, netRevenue: 200, cancelRate: 0.1 }),
  commission: { coveredRevenue: 200, coverage: 0.8, estimatedCommission: 43, estimatedPayout: 157, effectiveRate: 0.215 },
  cost: { keptQuantity: 4, costedQuantity: 2, coverage: 0.5, costTotal: 80, costedRevenue: 200, grossProfit: 120, grossMargin: 0.6, profitQuantity: 2, profitCoverage: 0.5, profitRevenue: 200, profitCost: 80, profitCommission: 43, netProfit: 77, netMargin: 0.385, costedVariants: 1 },
  platforms: [
    group('TRENDYOL', 'TRENDYOL', { orders: 3, soldQuantity: 4, netRevenue: 250, grossSales: 350, ordersWithAmount: 3, estimatedCommission: 43, commissionCoveredRevenue: 200, children: [group('c1', 'Ravencia Trendyol')] }),
    group('SHOPIFY', 'SHOPIFY', { ordersWithAmount: 0, grossSales: 0, netRevenue: 0 })
  ],
  cities: [
    group('İstanbul', 'İstanbul', { soldQuantity: 2, returnedQuantity: 1, children: [group('KADIKÖY', 'Kadıköy')], split: [group('TRENDYOL', 'TRENDYOL')] }),
    group('Bilinmiyor', 'Bilinmiyor', { soldQuantity: 3 })
  ],
  timeSeries: [{ start: '2026-10-01', orders: 2, soldQuantity: 3, returnedQuantity: 1, netRevenue: 150, cancelledOrders: 1 }, { start: '2026-10-02', orders: 2, soldQuantity: 2, returnedQuantity: 0, netRevenue: 100, cancelledOrders: 0 }],
  previousTimeSeries: [{ start: '2026-09-09', orders: 1, soldQuantity: 1, returnedQuantity: 0, netRevenue: 100, cancelledOrders: 0 }],
  topProducts: [{ variantId: 'v1', productId: 'p1', title: 'Koltuk Kılıfı', sku: 'KLF-1', brand: 'Ravencia', category: 'Kılıf', soldQuantity: 2, returnedQuantity: 0, returnRate: 0, salesAmount: 200, estimatedCommission: 43, commissionCoveredAmount: 200 }],
  topReturned: [],
  brands: [group('Ravencia', 'Ravencia'), group('Bilinmiyor', 'Bilinmiyor')],
  categories: [group('Kılıf', 'Kılıf')],
  returnReasons: [{ reason: 'Beden uymadı', known: true, count: 1 }],
  cancellations: { cancelledOrders: 1, cancelledQuantity: 1, cancelRate: 0.2, warningRate: 0.02, platforms: [{ key: 'TRENDYOL', label: 'TRENDYOL', orders: 4, cancelledOrders: 1, cancelledQuantity: 1, cancelRate: 0.25 }], reasons: [{ reason: 'Bilinmiyor', known: false, count: 1 }] },
  cargo: [{ label: 'Aras Kargo', known: true, packages: 3, share: 1, averageHoursToShip: 24, measuredShip: 1, averageHoursToDeliver: 48, measuredDeliver: 1, lateShipments: 1, measuredLate: 1, lateRate: 1 }],
  heatmap: Array.from({ length: 7 }, (_, day) => Array.from({ length: 24 }, (_, hour) => (day === 3 && hour === 12 ? 3 : 0))),
  customers: { identifiedOrders: 3, unidentifiedOrders: 1, uniqueCustomers: 2, repeatCustomers: 1, repeatRate: 0.5, ordersPerCustomer: 1.5 },
  invoices: { buckets: [{ key: 'INVOICED', label: 'Faturalı', packages: 2, amount: 200 }, { key: 'NOT_INVOICED', label: 'Faturasız', packages: 1, amount: 50 }, { key: 'UNKNOWN', label: 'Bilinmiyor', packages: 0, amount: 0 }], platforms: [{ platformCode: 'TRENDYOL', invoiced: 2, notInvoiced: 1, unknown: 0 }] },
  stock: { periodDays: 22, bestSellers: [{ variantId: 'v1', title: 'Koltuk Kılıfı', sku: 'KLF-1', soldQuantity: 2, available: 1, dailyAverage: 0.09, daysLeft: 11.1 }], soldOut: [] },
  channels: [{ id: 'c1', platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol', isTest: false }, { id: 'c2', platformCode: 'SHOPIFY', displayName: 'Shopify', isTest: false }],
  costs: { source: 'estimate', lastSyncedAt: null, coveredFrom: null, coveredThrough: null, financeOrders: 3, settledOrders: 0, coverage: null, realCostTotal: null, commission: null, cargo: null, lines: [], channels: [], products: [] },
  geography: {
    countries: [group('TR', 'Türkiye', { orders: 3 }), group('SA', 'Suudi Arabistan', { orders: 1, netRevenue: 300, grossSales: 300, split: [group('TRENDYOL', 'TRENDYOL', { netRevenue: 300 })] })],
    previousCities: [group('İstanbul', 'İstanbul', { netRevenue: 50 })], previousCountries: [], abroadOrders: 1
  },
  dataNotes: ['1 siparişte tutar yok (SHOPIFY 1).']
}

// The same period with Trendyol's account statement read: one settled order, one order's cargo billed, two not yet.
const withFinance: ReportSales = {
  ...report,
  costs: {
    source: 'trendyol-finance', lastSyncedAt: '2026-10-22T23:40:00Z', coveredFrom: '2026-07-24T23:39:00Z', coveredThrough: '2026-10-22T23:40:00Z', financeOrders: 3, settledOrders: 1, coverage: 0.3333, realCostTotal: 63.5,
    commission: { real: 20, realOrders: 1, estimatedForSettled: 21.5, estimatedForUnsettled: 21.5, combined: 41.5 },
    cargo: { real: 30, billedOrders: 1, unbilledOrders: 2, averagePerOrder: 98.34, sampleOrders: 40, estimated: 196.68, combined: 226.68 },
    lines: [
      { category: 'SALE', label: 'Satış', isCost: false, amount: -100, records: 1, orderLinked: -100, unlinked: 0 },
      { category: 'COMMISSION', label: 'Komisyon', isCost: true, amount: 20, records: 1, orderLinked: 20, unlinked: 0 },
      { category: 'CARGO', label: 'Kargo', isCost: true, amount: 30, records: 1, orderLinked: 30, unlinked: 0 },
      { category: 'SERVICE_FEE', label: 'Platform hizmet bedeli', isCost: true, amount: 12.5, records: 1, orderLinked: 0, unlinked: 12.5 },
      { category: 'STOPPAGE', label: 'Stopaj', isCost: true, amount: 1, records: 1, orderLinked: 0, unlinked: 1 }
    ],
    channels: [], products: []
  }
}

const requests = vi.hoisted(() => [] as string[])
const fixture = vi.hoisted(() => ({ current: null as ReportSales | null }))
vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async (path: string) => {
    requests.push(path)
    if (path.startsWith('/reports/sales?')) return fixture.current
    throw new Error(`Test API fixture does not handle ${path}`)
  })
}))

import { ReportsPage } from './ReportsPage'

let container: HTMLDivElement | null = null
let root: Root | null = null
let client: QueryClient | null = null

async function open(search = '', data: ReportSales = report) {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  fixture.current = data
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root!.render(<QueryClientProvider client={client!}><MemoryRouter initialEntries={[`/reports${search}`]}><ReportsPage /></MemoryRouter></QueryClientProvider>) })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
  return container
}

const selectedTab = (page: HTMLElement) => page.querySelector('.reports-tabs [role="tab"][aria-selected="true"]')?.textContent ?? ''
const body = (page: HTMLElement) => page.querySelector('.reports-body')?.textContent ?? ''

afterEach(() => { act(() => root?.unmount()); client?.clear(); container?.remove(); requests.length = 0 })

describe('ReportsPage', () => {
  it('asks for the last 30 days and shows the summary on one screen', async () => {
    const page = await open()
    expect(requests[0]).toMatch(/^\/reports\/sales\?from=\d{4}-\d{2}-\d{2}&to=\d{4}-\d{2}-\d{2}$/)
    const kpis = page.querySelector('.reports-kpis')!
    expect(kpis.textContent).toContain('Net Ciro')
    expect(kpis.textContent).toContain('+%25')
    expect(kpis.querySelectorAll('[role="tooltip"]')).toHaveLength(8)
    expect(page.querySelector('.reports-kpi.is-warn')?.textContent).toContain('İptal Oranı')
    expect(page.querySelector('.reports-chart')).not.toBeNull()
    expect(page.querySelector('.reports-flow')?.textContent).toContain('Tahmini Net Kâr')
    expect(page.querySelector('.reports-legend')?.textContent).toContain('Henüz okunmuyor')
    expect(page.querySelectorAll('.reports-metric')).toHaveLength(6)
    expect(page.querySelectorAll('.reports-kpis .reports-spark').length).toBeGreaterThan(0)
    // One tab per area, each with its headline figure.
    const tabs = [...page.querySelectorAll('.reports-tabs [role="tab"]')].map(tab => tab.textContent)
    expect(tabs.some(tab => tab?.includes('Genel Bakış'))).toBe(true)
    expect(tabs.some(tab => tab?.includes('Operasyon') && tab.includes('%'))).toBe(true)
    expect(page.querySelector('.reports-map-panel')).toBeNull()
  })

  it('takes commission, cargo and fees from Trendyol\'s statement, real plus estimated, and names what is not read', async () => {
    const page = await open('', withFinance)
    const flow = page.querySelector('.reports-flow')!.textContent!
    expect(flow).toContain('Komisyon')
    expect(flow).toContain('41,50')
    expect(flow).toContain('Gerçek 1 sipariş')
    expect(flow).toContain('Kargo')
    expect(flow).toContain('226,68')
    expect(flow).toContain('faturası gelmeyen 2 sipariş tahmini')
    expect(flow).toContain('Kesintiler')
    expect(flow).toContain('13,50')
    // 250 net − 41.50 − 226.68 − 13.50
    expect(flow).toContain('31,68')
    expect(page.querySelector('.reports-flow li.is-negative')?.textContent).toContain('Masraflar Düşülmüş')
    const legend = page.querySelector('.reports-legend')!.textContent!
    expect(legend).toContain('Hizmet Bedeli')
    expect(legend).toContain('Stopaj')
    expect(legend).not.toContain('Henüz okunmuyor')
    expect(legend).toContain('Shopify kargo ve hizmet bedeli')

    await act(async () => { page.querySelector<HTMLButtonElement>('[aria-label="Kâr Akışı: Tümünü Gör"]')!.click() })
    expect(selectedTab(page)).toContain('Kârlılık')
    expect(body(page)).toContain('Trendyol Cari Hesap Kalemleri')
    expect(body(page)).toContain('Masraf Değil')
  })

  it('paints the provinces, ranks them beside the map, opens one and switches to the export countries', async () => {
    const page = await open('?view=geo')
    const map = page.querySelector('.reports-map-panel')!
    // Each scope's outlines load on their own; the world's are large enough to take longer than any fixed wait on CI.
    // React applies the loaded map only when an act scope ends, so the wait is short act steps, not one long act.
    const paths = () => map.querySelectorAll('.reports-map-regions path').length
    const loaded = async (done: (count: number) => boolean) => {
      for (let tries = 0; tries < 150 && !done(paths()); tries++)
        await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
      expect(done(paths())).toBe(true)
    }
    await loaded(count => count === 82)
    expect(map.querySelectorAll('.reports-map-regions path.is-active')).toHaveLength(1)
    const list = map.querySelector('.reports-map-list')!
    // Known provinces only; the unknown bucket is named under the map instead.
    expect(list.querySelectorAll('li')).toHaveLength(1)
    expect(list.textContent).toContain('İstanbul')
    expect(list.textContent).toContain('+%100')
    expect(map.querySelector('.reports-map-foot')?.textContent).toContain('1 siparişte il okunamadı')
    expect(map.querySelector('.reports-map-foot')?.textContent).toContain('1 sipariş yurt dışına gitti')

    await act(async () => { list.querySelector<HTMLButtonElement>('button')!.click() })
    expect(map.querySelector('.reports-map-detail h3')?.textContent).toBe('İstanbul')
    expect(map.querySelector('.reports-map-districts')?.textContent).toContain('Kadıköy')

    // The breakdown tabs: Istanbul's region, then its platforms; a platform repaints the map with its orders only.
    await act(async () => { [...map.querySelectorAll<HTMLButtonElement>('.reports-map-tabs button')].find(button => button.textContent === 'Bölgeler')!.click() })
    expect(map.querySelector('.reports-map-list.is-zones li')?.textContent).toContain('Marmara')
    await act(async () => { [...map.querySelectorAll<HTMLButtonElement>('.reports-map-tabs button')].find(button => button.textContent === 'Sıralama')!.click() })
    const metricSwitch = map.querySelector<HTMLButtonElement>('.reports-map-switch')!
    expect(metricSwitch.textContent).toContain('Ciro')
    await act(async () => { metricSwitch.click() })
    expect(metricSwitch.textContent).toContain('Sipariş')
    expect(map.querySelector('.reports-map-strip')?.textContent).toContain('İstanbul')

    await act(async () => { [...map.querySelectorAll<HTMLButtonElement>('.reports-map-scope button')].find(button => button.textContent?.includes('Dünya'))!.click() })
    // The world's countries (more outlines than Turkey's 82 provinces).
    await loaded(count => count > 82)
    expect(map.querySelector('.reports-map-detail')).toBeNull()
    expect(map.querySelector('.reports-map-list')?.textContent).toContain('Suudi Arabistan')
    expect(map.querySelector('.reports-map-list')?.textContent).not.toContain('Türkiye')
    expect(map.querySelectorAll('.reports-map-regions path.is-active')).toHaveLength(1)
  })

  it('keeps the unknown bucket last and muted in the returns breakdown', async () => {
    const page = await open('?view=ops&sub=returns')
    const list = Array.from(page.querySelectorAll('.reports-list')).find(panel => panel.textContent?.includes('İade Nedenleri'))!
    const rows = Array.from(list.querySelectorAll('.reports-bars li'))
    expect(rows.at(-1)?.classList.contains('is-unknown')).toBe(rows.some(row => row.textContent?.includes('Bilinmiyor')))
  })

  it('moves to the area\'s tab from a card, with its tables and CSV export', async () => {
    const page = await open()
    const more = page.querySelector<HTMLButtonElement>('button[aria-label="Platform Payı: Tümünü Gör"]')!
    await act(async () => { more.click() })
    expect(selectedTab(page)).toContain('Kârlılık')
    const panel = page.querySelector<HTMLElement>('.reports-body')!
    expect(panel.textContent).toContain('Platform Özeti')
    const shopify = Array.from(panel.querySelectorAll('.reports-table tbody tr')).find(row => row.textContent?.includes('Shopify'))!
    expect(shopify.textContent).toContain('Tutar yok')
    expect(shopify.textContent).not.toContain('₺0,00')
    expect(Array.from(panel.querySelectorAll('button')).some(button => button.textContent?.includes('CSV'))).toBe(true)
  })

  it('opens an older shared link on its tab and keeps the unknown city row last', async () => {
    const page = await open('?detail=cities')
    expect(selectedTab(page)).toContain('Coğrafya')
    const table = Array.from(page.querySelectorAll('.reports-body .reports-panel')).find(panel => panel.textContent?.includes('İllere Göre Satış ve İade'))!
    const rows = Array.from(table.querySelectorAll('.reports-table tbody tr'))
    expect(rows.at(-1)?.textContent).toContain('Bilinmiyor')
    expect(rows.at(-1)?.classList.contains('is-unknown')).toBe(true)
  })

  it.each([
    ['products', 'Ürünler', 'En Çok Satan 20 Ürün'], ['brands', 'Ürünler', 'Markalar'], ['returns', 'Operasyon', 'İade Nedenleri'], ['cancellations', 'Operasyon', 'Platformlara Göre İptaller'],
    ['cargo', 'Operasyon', 'Kargo Firmaları'], ['time', 'Müşteriler', 'Gün ve Saate Göre Siparişler'], ['customers', 'Müşteriler', 'Tekil Müşteri'],
    ['stock', 'Operasyon', 'En Çok Satanların Stoğu'], ['cost', 'Kârlılık', 'Kapsam'], ['invoices', 'Operasyon', 'Platformlara Göre Fatura Durumu']
  ])('opens the %s breakdown on its tab', async (key, tab, heading) => {
    const page = await open(`?detail=${key}`)
    expect(selectedTab(page)).toContain(tab)
    expect(body(page)).toContain(heading)
  })

  it('switches the operations sub-tabs', async () => {
    const page = await open('?view=ops')
    const subtab = (label: string) => [...page.querySelectorAll<HTMLButtonElement>('.reports-subtabs [role="tab"]')].find(button => button.textContent?.includes(label))!
    expect(subtab('İadeler').getAttribute('aria-selected')).toBe('true')
    await act(async () => { subtab('Kargo').click() })
    expect(subtab('Kargo').getAttribute('aria-selected')).toBe('true')
    expect(body(page)).toContain('Kargo Firmalarının Payı')
  })

  it('says the cost is not entered instead of showing a profit when no cost is known', async () => {
    const page = await open('', { ...report, cost: { ...report.cost, costedQuantity: 0, coverage: 0, costTotal: 0, grossProfit: 0, profitQuantity: 0, profitCoverage: 0, netProfit: 0 } })
    const flow = page.querySelector('.reports-flow')!
    expect(flow.textContent).toContain('Maliyet girilmemiş')
    expect(flow.textContent).not.toContain('Tahmini Net Kâr')
    expect(page.querySelector('.reports-legend')?.textContent).toContain('Maliyet girilmemiş')
  })

  it('shows the commission tariffs as the second view', async () => {
    const page = await open('?view=tariffs')
    expect(page.querySelector('.reports-kpis')).toBeNull()
    expect(page.querySelector('.reports-body')?.textContent?.length).toBeGreaterThan(0)
  })
})
