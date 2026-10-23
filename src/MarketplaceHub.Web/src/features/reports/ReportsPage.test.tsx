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
  topProducts: [{ variantId: 'v1', productId: 'p1', title: 'Koltuk Kılıfı', sku: 'KLF-1', brand: 'Ravencia', category: 'Kılıf', soldQuantity: 2, returnedQuantity: 0, returnRate: 0, salesAmount: 200, estimatedCommission: 43, commissionCoveredAmount: 200, modelCode: 'KLF', imageUrl: '/api/v1/files/product-media/a1/content', options: [{ name: 'Beden', value: 'M' }, { name: 'Renk', value: 'Siyah' }], netAmount: 200, previousSoldQuantity: 1, available: 7 }],
  topReturned: [],
  rising: [{ variantId: 'v1', productId: 'p1', title: 'Koltuk Kılıfı', sku: 'KLF-1', brand: 'Ravencia', category: 'Kılıf', soldQuantity: 2, returnedQuantity: 0, returnRate: 0, salesAmount: 200, estimatedCommission: 43, commissionCoveredAmount: 200, modelCode: 'KLF', netAmount: 200, previousSoldQuantity: 1 }],
  falling: [{ variantId: 'v2', productId: 'p2', title: 'Masa Örtüsü', sku: 'MSA-1', brand: 'Ravencia', category: 'Örtü', soldQuantity: 0, returnedQuantity: 0, returnRate: null, salesAmount: 0, estimatedCommission: 0, commissionCoveredAmount: 0, netAmount: 0, previousSoldQuantity: 3 }],
  brands: [group('Ravencia', 'Ravencia'), group('Bilinmiyor', 'Bilinmiyor')],
  categories: [group('Kılıf', 'Kılıf')],
  returnReasons: [{ reason: 'Beden uymadı', known: true, count: 1 }],
  cancellations: { cancelledOrders: 1, cancelledQuantity: 1, cancelRate: 0.2, warningRate: 0.02, platforms: [{ key: 'TRENDYOL', label: 'TRENDYOL', orders: 4, cancelledOrders: 1, cancelledQuantity: 1, cancelRate: 0.25 }], reasons: [{ reason: 'Bilinmiyor', known: false, count: 1 }], cancelledAmount: 120 },
  cargo: [{ label: 'Aras Kargo', known: true, packages: 3, share: 1, averageHoursToShip: 24, measuredShip: 1, averageHoursToDeliver: 48, measuredDeliver: 1, lateShipments: 1, measuredLate: 1, lateRate: 1 }],
  heatmap: Array.from({ length: 7 }, (_, day) => Array.from({ length: 24 }, (_, hour) => (day === 3 && hour === 12 ? 3 : 0))),
  platformHeatmaps: [
    { platformCode: 'TRENDYOL', heatmap: Array.from({ length: 7 }, (_, day) => Array.from({ length: 24 }, (_, hour) => (day === 3 && hour === 12 ? 2 : 0))) },
    { platformCode: 'SHOPIFY', heatmap: Array.from({ length: 7 }, (_, day) => Array.from({ length: 24 }, (_, hour) => (day === 3 && hour === 12 ? 1 : 0))) }
  ],
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
// Opens a step of "Satıştan Kâra" and returns the text of the window its breakdown opens in.
const openStep = async (page: HTMLElement, label: string) => {
  await act(async () => { page.querySelector<HTMLButtonElement>(`.reports-journey button[aria-label^="${label}"]`)!.click() })
  return page.querySelector('.reports-journey-modal')?.textContent ?? ''
}

afterEach(() => { act(() => root?.unmount()); client?.clear(); container?.remove(); requests.length = 0 })

describe('ReportsPage', () => {
  it('asks for the last 30 days and shows the summary on one screen', async () => {
    const page = await open()
    expect(requests[0]).toMatch(/^\/reports\/sales\?from=\d{4}-\d{2}-\d{2}&to=\d{4}-\d{2}-\d{2}$/)
    // Net revenue with its columns (the best day marked) and the period's other figures beside it.
    const revenue = page.querySelector('.reports-card-revenue')!
    expect(revenue.textContent).toContain('Net Ciro')
    expect(revenue.textContent).toContain('+%25')
    expect(revenue.querySelectorAll('.reports-columns li')).toHaveLength(2)
    expect(revenue.querySelector('.reports-columns li.is-peak')).not.toBeNull()
    expect(page.querySelector('.reports-figures')?.textContent).toContain('Ortalama Sepet')
    // From the sales to the profit: 350 of sales, 223 of costs (returns 100, the estimated commission 43, the cost 80),
    // 127 left (its note names the lines that were not taken off), the ratios, then the estimated net profit.
    const steps = [...page.querySelectorAll('.reports-journey > li')]
    expect(steps).toHaveLength(5)
    expect(steps[0].textContent).toContain('350,00')
    expect(steps[1].textContent).toContain('223,00')
    // The costs fill 223 / 350 of the sales bar in red; what is left fills the rest in green from there.
    expect(steps[1].querySelector('.reports-sales-share.is-bad')?.getAttribute('aria-label')).toContain('%63,7')
    expect([...steps[1].querySelectorAll('.reports-journey-parts li')].map(item => item.textContent)).toEqual(['İade Edilen Tutar₺100%28,6', 'Ürün Maliyeti₺80%22,9', 'Tahmini Komisyon₺43%12,3'])
    expect(steps[2].querySelector('.reports-sales-share.is-good')?.getAttribute('aria-label')).toContain('%36,3')
    expect(steps[2].textContent).toContain('127,00')
    expect(steps[3].textContent).toContain('%38,5')
    expect(steps[4].textContent).toContain('Tahmini Net Kâr')
    expect(steps[4].textContent).toContain('77,00')
    // Each step opens its breakdown in a window over the page, and a second click closes it; no separate cards repeat them.
    expect(page.querySelector('.reports-journey-modal')).toBeNull()
    expect(await openStep(page, 'Satış tutarı')).toContain('Trendyol')
    expect(await openStep(page, 'Masraflar:')).toContain('Tahmini Komisyon')
    expect(await openStep(page, 'Masraflar sonrası')).toContain('kargo faturası')
    expect(page.querySelector('.reports-journey > li.is-equals')?.classList.contains('is-open')).toBe(true)
    expect(await openStep(page, 'Kâr oranları')).toContain('%38,5')
    expect(await openStep(page, 'Tahmini Net Kâr')).toContain('Kârlılık sekmesinde aç')
    await openStep(page, 'Tahmini Net Kâr')
    expect(page.querySelector('.reports-journey-modal')).toBeNull()
    expect(page.querySelector('.reports-card-costs, .reports-card-margin, .reports-card-platforms, .reports-notes')).toBeNull()
    // Satış Özeti: 6 units ordered (one cancelled for 120, one returned for 100), 4 kept for 250; the averages under them.
    expect(page.querySelector('.reports-period-card')).toBeNull()
    const sales = [...page.querySelectorAll('.reports-summary-rows > div')].map(row => row.textContent)
    expect(sales).toEqual(['Brüt Satış6 adet₺470,00', 'İptaller−1 adet− ₺120,00', 'İadeler−1 adet− ₺100,00', 'Net Satış4 adet₺250,00'])
    expect(page.querySelector('.reports-summary-metrics')?.textContent).toBe('Ort. satış fiyatı₺70,00Ort. komisyon oranı%21,5')
    expect(page.querySelectorAll('.reports-operations button')).toHaveLength(6)
    expect(page.querySelector('.reports-operations button.is-warn')?.textContent).toContain('İptal Oranı')
    // One tab per area, each with its headline figure.
    const tabs = [...page.querySelectorAll('.reports-tabs [role="tab"]')].map(tab => tab.textContent)
    expect(tabs.some(tab => tab?.includes('Genel Bakış'))).toBe(true)
    expect(tabs.some(tab => tab?.includes('Operasyon') && tab.includes('%'))).toBe(true)
    expect(page.querySelector('.reports-map-panel')).toBeNull()
  })

  it('takes commission, cargo and fees from Trendyol\'s statement, real plus estimated, and names what is not read', async () => {
    const page = await open('', withFinance)
    const costs = await openStep(page, 'Masraflar:')
    expect(costs).toContain('Komisyon')
    expect(costs).toContain('41,50')
    expect(costs).toContain('Gerçek 1 sipariş')
    expect(costs).toContain('226,68')
    expect(costs).toContain('faturası gelmeyen 2 sipariş tahmini')
    expect(costs).toContain('Kesintiler')
    expect(costs).toContain('13,50')
    expect(costs).toContain('Hizmet Bedeli, Stopaj')
    // 250 net − 41.50 − 226.68 − 13.50 − 80 cost: the deductions go 111.68 over the sales, shown as a gap.
    const gap = page.querySelector('.reports-journey > li.is-negative')!
    expect(gap.textContent).toContain('Açık')
    expect(gap.textContent).toContain('111,68')
    const left = await openStep(page, 'Masraflar sonrası')
    expect(left).toContain('Shopify kargo ve hizmet bedeli')
    expect(left).not.toContain('kargo faturası')

    await act(async () => { page.querySelector<HTMLButtonElement>('[aria-label="Satıştan Kâra: Tümünü Gör"]')!.click() })
    expect(selectedTab(page)).toContain('Kârlılık')
    expect(body(page)).toContain('Trendyol Cari Hesap Kalemleri')
    expect(body(page)).toContain('Masraf Değil')
    // Another marketplace is picked above the statement; its statement is not read yet, so the card says so.
    await act(async () => { [...page.querySelectorAll<HTMLButtonElement>('.reports-segmented button')].find(button => button.textContent === 'Shopify')!.click() })
    expect(body(page)).toContain('Shopify Cari Hesap Kalemleri')
    expect(body(page)).toContain('Shopify cari hesabı henüz okunmuyor')
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

  it('shows the day under the pointer with the previous period beside it and hides it when the pointer leaves', async () => {
    const page = await open()
    const chart = page.querySelector<HTMLElement>('.reports-columns')!
    const first = chart.querySelector<HTMLElement>('li')!
    await act(async () => { first.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, relatedTarget: document.body })) })
    const tip = page.querySelector('.reports-columns-tip')!
    expect(tip.textContent).toContain('01.10.2026')
    expect(tip.textContent).toContain('Bu dönem')
    expect(tip.textContent).toContain('150,00')
    expect(tip.textContent).toContain('100,00')
    expect(first.classList.contains('is-hover')).toBe(true)
    await act(async () => { chart.dispatchEvent(new MouseEvent('mouseout', { bubbles: true, relatedTarget: document.body })) })
    expect(page.querySelector('.reports-columns-tip')).toBeNull()
  })

  it('keeps the unknown bucket last and muted in the returns breakdown', async () => {
    const page = await open('?view=ops&sub=returns')
    const list = Array.from(page.querySelectorAll('.reports-list')).find(panel => panel.textContent?.includes('İade Nedenleri'))!
    const rows = Array.from(list.querySelectorAll('.reports-bars li'))
    expect(rows.at(-1)?.classList.contains('is-unknown')).toBe(rows.some(row => row.textContent?.includes('Bilinmiyor')))
  })

  it('moves to the area\'s tab from a card, with its tables and CSV export', async () => {
    const page = await open()
    const more = page.querySelector<HTMLButtonElement>('button[aria-label="Satıştan Kâra: Tümünü Gör"]')!
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
    const profit = page.querySelector('.reports-journey > li.is-result')!
    expect(profit.textContent).toContain('Maliyet girilmemiş')
    expect(profit.textContent).not.toContain('Tahmini Net Kâr')
    expect(page.querySelector('.reports-gauge-value')).toBeNull()
    expect(await openStep(page, 'Masraflar:')).not.toContain('Ürün Maliyeti')
    expect(await openStep(page, 'Masraflar sonrası')).toContain('ürün maliyeti girilmemiş')
    // The profit tab's account says the same instead of a row of zeros.
    await act(async () => { page.querySelector<HTMLButtonElement>('[aria-label="Satıştan Kâra: Tümünü Gör"]')!.click() })
    expect(body(page)).toContain('Ürün kartında varyant maliyeti girildiğinde kâr burada görünür.')
  })

  it('lists a product with its picture, model code, stock code and the variant\'s size and colour', async () => {
    const page = await open('?view=products')
    const row = page.querySelector('.reports-table tbody tr')!
    expect(row.querySelector('img.reports-product-image')?.getAttribute('src')).toBe('/api/v1/files/product-media/a1/content')
    expect(row.textContent).toContain('KLF')
    expect(row.textContent).toContain('KLF-1')
    expect(row.textContent).toContain('Beden: M · Renk: Siyah')
    // As on the marketplace's own product report: net units and revenue, the average price, the commission rate, the
    // change against the previous period and the stock now.
    expect(row.textContent).toContain('₺100,00')
    expect(row.textContent).toContain('%21,5')
    expect(row.textContent).toContain('1 → 2')
    expect(row.textContent).toContain('+%100')
    // The products that rose and fell most, before and now.
    const rising = page.querySelector('.reports-movers.is-up li')!
    expect(rising.textContent).toContain('Koltuk Kılıfı')
    expect(rising.textContent).toContain('+1')
    expect(rising.textContent).toContain('1 → 2 adet')
    expect(page.querySelector('.reports-movers.is-down li')?.textContent).toContain('−33 → 0 adet')
  })

  it('narrows the hours to a weekday and to the picked platforms', async () => {
    const page = await open('?view=customers')
    const panel = page.querySelector('.reports-time-panel')!
    const summary = () => panel.querySelector('.reports-time-summary')?.textContent ?? ''
    const press = async (group: string, label: string) => {
      await act(async () => { [...panel.querySelectorAll<HTMLButtonElement>(`[aria-label="${group}"] button`)].find(button => button.textContent?.startsWith(label))!.click() })
    }
    expect(summary()).toContain('Tüm platformlar · tüm hafta')
    expect(summary()).toContain('3 sipariş')
    // Monday has no orders; Thursday has all three, at noon.
    await press('Gün', 'Pazartesi')
    expect(summary()).toContain('Pazartesi')
    expect(panel.textContent).toContain('Bu seçimde sipariş yok.')
    await press('Gün', 'Perşembe')
    expect(summary()).toContain('12:00–13:00')
    expect(panel.querySelector('.reports-columns li.is-peak small, .reports-columns li.is-peak')).not.toBeNull()
    // Shopify alone brought one of them; with Trendyol picked too, the two count together.
    await press('Platform', 'Shopify')
    expect(summary()).toContain('Shopify · Perşembe')
    expect(summary()).toContain('1 sipariş')
    await press('Platform', 'Trendyol')
    expect(summary()).toContain('Trendyol + Shopify')
    expect(summary()).toContain('3 sipariş')
  })

  it('asks for a ready period in one click and marks it', async () => {
    const page = await open()
    const preset = (label: string) => [...page.querySelectorAll<HTMLButtonElement>('[aria-label="Hazır dönemler"] button')].find(button => button.textContent === label)!
    expect(preset('Son 30 Gün').getAttribute('aria-pressed')).toBe('true')
    await act(async () => { preset('Bu Ay').click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
    const now = new Date()
    const first = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-01`
    expect(requests.at(-1)).toContain(`from=${first}`)
    expect(preset('Bu Ay').getAttribute('aria-pressed')).toBe('true')
    expect(preset('Son 30 Gün').getAttribute('aria-pressed')).toBe('false')
  })

  it('opens an operation\'s tab', async () => {
    const page = await open()
    await act(async () => { page.querySelector<HTMLButtonElement>('.reports-operations button[aria-label^="Stok Riski"]')!.click() })
    expect(selectedTab(page)).toContain('Operasyon')
    expect(body(page)).toContain('En Çok Satanların Stoğu')
  })

  it('shows the commission tariffs as the second view', async () => {
    const page = await open('?view=tariffs')
    expect(page.querySelector('.reports-kpis')).toBeNull()
    expect(page.querySelector('.reports-body')?.textContent?.length).toBeGreaterThan(0)
  })
})
