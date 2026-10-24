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
  grossSales: 350, returnedAmount: 100, netRevenue: 250, averageOrderValue: 116.67, ordersWithoutCity: 1, ordersWithoutAmount: 1, otherCurrencyOrders: 0,
  listAmount: 380, discountAmount: 30, ...values
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
const texts = (page: HTMLElement, selector: string) => [...page.querySelectorAll(selector)].map(item => item.textContent ?? '')
// A metric card's rows as "name value", without the explanation shown under the pointer.
const metricRows = (page: HTMLElement, card: string) => [...page.querySelectorAll(`.reports-card-metrics.is-${card} .reports-metric`)].map(row => row.textContent ?? '')
// The bridge's rows as "name amount", without their explanations.
const bridgeRows = (page: HTMLElement) => [...page.querySelectorAll('.reports-bridge > li')].map(row => `${row.querySelector('.reports-bridge-label')?.textContent ?? ''} ${row.querySelector('b')?.textContent ?? ''}`)

afterEach(() => { act(() => root?.unmount()); client?.clear(); container?.remove(); requests.length = 0 })

describe('ReportsPage', () => {
  it('asks for the last 30 days and shows the overview: the six figures, the costs, the bridge, the days and the metric cards', async () => {
    const page = await open()
    expect(requests[0]).toMatch(/^\/reports\/sales\?from=\d{4}-\d{2}-\d{2}&to=\d{4}-\d{2}-\d{2}$/)
    // One account: 350 of sales, 100 back and an estimated commission of 43 taken off, the product cost of 80, 127 of
    // estimated profit (green); its margin over the sales and over the cost; 4 orders against 2, 4 of 5 units kept.
    const figures = texts(page, '.reports-kpis > div')
    expect(figures).toHaveLength(6)
    expect(figures[0]).toContain('Net Ciro')
    expect(figures[0]).toContain('250,00')
    expect(figures[0]).toContain('+%25')
    expect(figures[1]).toContain('Net Kâr')
    expect(figures[1]).toContain('127,00')
    expect(page.querySelectorAll('.reports-kpis > div')[1].classList.contains('is-good')).toBe(true)
    expect(figures[2]).toContain('%36,3')
    expect(figures[3]).toContain('%158,8')
    expect(figures[4]).toContain('+%100')
    expect(figures[5]).toContain('5 satılan adetten 1 iade düşüldü')
    // Masraf Kalemleri: the account's deductions and the product cost, largest first, 223 in all (63,7 % of the sales);
    // what is not read without Trendyol's statement is "—", never ₺0.
    const card = page.querySelector('.reports-card-costlines')!
    expect(card.querySelector('.reports-donut-centre')?.textContent).toContain('₺223')
    expect(card.querySelector('.reports-donut-centre')?.textContent).toContain('%63,7')
    const lines = [...card.querySelectorAll('.reports-costlines li')].map(line => [...line.children].slice(1, 4).map(cell => cell.textContent).join(' ').trim())
    expect(lines.slice(0, 3)).toEqual(['İade Edilen Tutar ₺100,00 %44,8', 'Ürün Maliyeti ₺80,00 %35,9', 'Tahmini Komisyon ₺43,00 %19,3'])
    expect(lines.slice(3)).toEqual(['Kargo —', 'Hizmet Bedeli —', 'Stopaj —', 'Reklam —', 'Ceza —', 'Erken Ödeme Kesintisi —', 'Net KDV —'])
    expect(card.textContent).not.toContain('₺0,00')
    // Kâr Köprüsü: each deduction taken from where the one before ended, down to the same 127.
    expect(bridgeRows(page)).toEqual(['Satış Tutarı ₺350,00', '− İade Edilen Tutar − ₺100,00', '− Pazaryeri Masrafları − ₺43,00', '− Ürün Maliyeti − ₺80,00', 'Tahmini Net Kâr ₺127,00'])
    // A row's explanation is there for the pointer, not printed under it.
    expect(page.querySelector('.reports-bridge > li.is-end .reports-hover-tip')?.textContent).toContain('Net marj %36,3')
    // The sales open their platforms in a window over the page.
    expect(page.querySelector('.reports-journey-modal')).toBeNull()
    await act(async () => { page.querySelector<HTMLButtonElement>('.reports-bridge button[aria-label^="Satış tutarı"]')!.click() })
    expect(page.querySelector('.reports-journey-modal')?.textContent).toContain('Trendyol')
    // Günler: the day columns, the best one marked, and the best day, the average and the days that beat the previous.
    const days = page.querySelector('.reports-card-period')!
    expect(days.querySelector('h2')?.textContent).toBe('Günler')
    expect(days.querySelectorAll('.reports-columns li')).toHaveLength(2)
    expect(days.querySelector('.reports-columns li.is-peak')).not.toBeNull()
    expect(days.querySelector('.reports-period-facts')?.textContent).toContain('01.10.2026')
    expect(days.querySelector('.reports-period-facts')?.textContent).toContain('1 / 1')
    // The metric cards: four rows on each of the first three, six on the two under them.
    expect(metricRows(page, 'product')).toEqual(['Adet Başına Satış₺70,00', 'Adet Başına Kâr₺31,75', 'Ortalama Komisyon Oranı%21,5', 'Ortalama İndirim Oranı%7,9'])
    expect(metricRows(page, 'order')).toEqual(['Sipariş Başına Satış₺116,67', 'Sipariş Başına Kâr₺42,33', 'Sipariş Başına Kargo—', 'Sipariş Başına Adet1,25'])
    expect(metricRows(page, 'ads')).toEqual(['Toplam Reklam Harcaması—', 'Reklam / Kâr—', 'Reklam / Ciro—', 'Reklam Cirosu ve ROAS—'])
    expect(metricRows(page, 'returns')).toHaveLength(6)
    expect(metricRows(page, 'returns')[4]).toContain('İptal Oranı%20,0')
    expect(page.querySelector('.reports-card-metrics.is-returns li.is-warn')?.textContent).toContain('İptal Oranı')
    expect(metricRows(page, 'ops')).toEqual(['Kargoya Verme Süresi24 sa', 'Geciken Kargo1', 'Kargo FirmasıAras Kargo', 'Faturasız Paket1', 'Stok Riski0 ürün', 'Tekrar Alan Müşteri%50,0'])
    // The old cards are gone, and no card repeats another.
    expect(page.querySelector('.reports-journey, .reports-card-summary, .reports-operations, .reports-card-revenue')).toBeNull()
    // One tab per area, each with its headline figure.
    const tabs = [...page.querySelectorAll('.reports-tabs [role="tab"]')].map(tab => tab.textContent)
    expect(tabs.some(tab => tab?.includes('Genel Bakış'))).toBe(true)
    expect(tabs.some(tab => tab?.includes('Operasyon') && tab.includes('%'))).toBe(true)
    expect(page.querySelector('.reports-map-panel')).toBeNull()
  })

  it('takes commission, cargo and fees from Trendyol\'s statement, real plus estimated, and names what is not read', async () => {
    const page = await open('', withFinance)
    // The statement's lines: commission 41,50, cargo 226,68, the service fee and the withholding tax; the categories it
    // has no record of are at zero, the VAT is not worked out.
    const lines = [...page.querySelectorAll('.reports-costlines li')].map(line => [...line.children].slice(1, 3).map(cell => cell.textContent).join(' '))
    expect(lines).toEqual(['Kargo ₺226,68', 'İade Edilen Tutar ₺100,00', 'Ürün Maliyeti ₺80,00', 'Komisyon ₺41,50', 'Hizmet Bedeli ₺12,50', 'Stopaj ₺1,00',
      'Reklam ₺0,00', 'Ceza ₺0,00', 'Erken Ödeme Kesintisi ₺0,00', 'Net KDV —'])
    expect(page.querySelector('.reports-costlines li:first-child .reports-hover-tip')?.textContent).toContain('faturası gelmeyen 2 sipariş tahmini')
    // 350 − 100 − 226,68 − 54 (commission and service fee) − 1 − 80 = −111,68: a loss, in red, on the bridge and the strip.
    expect(bridgeRows(page)).toEqual(['Satış Tutarı ₺350,00', '− İade Edilen Tutar − ₺100,00', '− Kargo − ₺226,68', '− Pazaryeri Masrafları − ₺54,00', '− Stopaj − ₺1,00', '− Ürün Maliyeti − ₺80,00', 'Tahmini Zarar − ₺111,68'])
    expect(page.querySelector('.reports-bridge > li.is-end.is-negative')).not.toBeNull()
    expect(page.querySelector('.reports-bridge > li.is-end .reports-hover-tip')?.textContent).toContain('Shopify kargo ve hizmet bedeli')
    // What the estimate leaves out is written under the bridge as well, for a phone that has no pointer.
    expect(page.querySelector('.reports-card-bridge .reports-note')?.textContent).toContain('düşülmeyenler: Shopify kargo ve hizmet bedeli')
    expect(page.querySelector('.reports-card-bridge .reports-note')?.textContent).toContain('%50,0 kadarı için girili')
    const loss = page.querySelectorAll('.reports-kpis > div')[1]
    expect(loss.classList.contains('is-bad')).toBe(true)
    expect(loss.textContent).toContain('Tahmini Zarar')
    expect(loss.textContent).toContain('− ₺111,68')
    expect(metricRows(page, 'ads')[0]).toBe('Toplam Reklam Harcaması₺0,00')
    expect(metricRows(page, 'order')[2]).toBe('Sipariş Başına Kargo₺75,56')

    await act(async () => { page.querySelector<HTMLButtonElement>('[aria-label="Kâr Köprüsü: Tümünü Gör"]')!.click() })
    expect(selectedTab(page)).toContain('Kârlılık')
    expect(body(page)).toContain('Trendyol Cari Hesap Kalemleri')
    expect(body(page)).toContain('Masraf Değil')
    // Another marketplace is picked above the statement; its statement is not read yet, so the card says so.
    await act(async () => { [...page.querySelectorAll<HTMLButtonElement>('.reports-segmented button')].find(button => button.textContent === 'Shopify')!.click() })
    expect(body(page)).toContain('Shopify Cari Hesap Kalemleri')
    expect(body(page)).toContain('Shopify cari hesabı henüz okunmuyor')
  })

  it('counts a deduction invoice of an unnamed kind among the Kesintiler', async () => {
    const costs = withFinance.costs!
    const page = await open('', { ...withFinance, costs: { ...costs, realCostTotal: 68, lines: [...costs.lines,
      { category: 'OTHER_DEDUCTION', label: 'Diğer kesinti', isCost: true, amount: 4.5, records: 1, orderLinked: 0, unlinked: 4.5 }] } })
    // It has its own line among the costs, and only when there is one (the other tests list none).
    const lines = [...page.querySelectorAll('.reports-costlines li')].map(line => [...line.children].slice(1, 3).map(cell => cell.textContent).join(' '))
    expect(lines).toContain('Diğer Kesinti ₺4,50')
    expect(bridgeRows(page).some(row => row.includes('Pazaryeri Masrafları') && row.includes('58,50'))).toBe(true)
  })

  it('takes the net VAT off the profit after the deductions, and adds a carried-forward one back', async () => {
    const page = await open('', { ...withFinance, costs: { ...withFinance.costs!, netVat: { salesVat: 22.73, productCostVat: 7.27, expenseVat: 68.63, net: -53.17 } } })
    // −111,68 before the VAT; a VAT carried forward of 53,17 brings it to −58,51.
    expect(bridgeRows(page)).toContain('+ Net KDV + ₺53,17')
    expect(bridgeRows(page).at(-1)).toContain('58,51')
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
    const more = page.querySelector<HTMLButtonElement>('button[aria-label="Kâr Köprüsü: Tümünü Gör"]')!
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
    const figures = texts(page, '.reports-kpis > div')
    expect(figures[1]).toContain('Maliyet girilmemiş')
    expect(figures[2]).toContain('—')
    // The product cost line says it is missing; the bridge ends at what the deductions leave, 207.
    const cost = [...page.querySelectorAll('.reports-costlines li')].find(line => line.textContent?.includes('Ürün Maliyeti'))!
    expect(cost.textContent).toContain('—')
    expect(cost.textContent).toContain('Ürün kartında varyant maliyeti girildiğinde görünür')
    expect(bridgeRows(page).at(-1)).toBe('Masraflar Sonrası ₺207,00')
    expect(bridgeRows(page).some(row => row.includes('Ürün Maliyeti'))).toBe(false)
    expect(metricRows(page, 'product')[1]).toBe('Adet Başına Kâr—')
    // The profit tab's account says the same instead of a row of zeros.
    await act(async () => { page.querySelector<HTMLButtonElement>('[aria-label="Kâr Köprüsü: Tümünü Gör"]')!.click() })
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
    await act(async () => { [...page.querySelectorAll<HTMLButtonElement>('.reports-card-metrics.is-ops button')].find(button => button.textContent?.includes('Stok Riski'))!.click() })
    expect(selectedTab(page)).toContain('Operasyon')
    expect(body(page)).toContain('En Çok Satanların Stoğu')
  })

  it('shows the commission tariffs as the second view', async () => {
    const page = await open('?view=tariffs')
    expect(page.querySelector('.reports-kpis')).toBeNull()
    expect(page.querySelector('.reports-body')?.textContent?.length).toBeGreaterThan(0)
  })
})
