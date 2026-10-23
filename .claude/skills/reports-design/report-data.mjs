// A realistic 30-day sales report for the local preview (serve.mjs): the shape of GET /reports/sales (report-format.ts
// ReportSales). Add the fields a change needs; keep the figures plausible so the cards look as they will live.
const rnd = (seed => () => (seed = (seed * 16807) % 2147483647) / 2147483647)(42)
const day = (start, i) => { const d = new Date(start); d.setDate(d.getDate() + i); return d.toISOString().slice(0, 10) }
const series = start => Array.from({ length: 30 }, (_, i) => { const orders = Math.round(14 + rnd() * 18 + (i > 20 ? 8 : 0)); return { start: day(start, i), orders, soldQuantity: Math.round(orders * 1.3), returnedQuantity: Math.round(rnd() * 3), netRevenue: Math.round(orders * (850 + rnd() * 300)), cancelledOrders: Math.round(rnd() * 2) } })
const now = series('2026-09-24'), before = series('2026-08-25')
const sum = (rows, key) => rows.reduce((s, r) => s + r[key], 0)
const summary = rows => { const net = sum(rows, 'netRevenue'), orders = sum(rows, 'orders'), sold = sum(rows, 'soldQuantity'), ret = sum(rows, 'returnedQuantity'), returned = Math.round(net * 0.08); return { orders, cancelledOrders: sum(rows, 'cancelledOrders'), cancelRate: 0.031, soldQuantity: sold, returnedQuantity: ret, keptQuantity: sold - ret, returnClaims: ret, returnRate: ret / sold, ordersWithAmount: orders, grossSales: net + returned, returnedAmount: returned, netRevenue: net, averageOrderValue: (net + returned) / orders, ordersWithoutCity: 2, ordersWithoutAmount: 0, otherCurrencyOrders: 0, listAmount: (net + returned) * 1.036, discountAmount: (net + returned) * 0.036 } }
const s = summary(now), p = summary(before)
const group = (key, label, v = {}) => ({ key, label, known: label !== 'Bilinmiyor', isTest: false, orders: 10, soldQuantity: 12, returnedQuantity: 1, returnClaims: 1, returnRate: 0.08, ordersWithAmount: 10, grossSales: 10000, netRevenue: 9000, estimatedCommission: 1900, commissionCoveredRevenue: 9000, children: [], split: [], ...v })
const commission = Math.round(s.netRevenue * 0.21), cargo = Math.round(s.orders * 72), fees = Math.round(s.netRevenue * 0.025)
const report = {
  from: '2026-09-24', to: '2026-10-23', previousFrom: '2026-08-25', previousTo: '2026-09-23', granularity: 'day', includesTest: false, generatedAt: '2026-10-23T10:00:00Z',
  summary: s, previousSummary: p,
  commission: { coveredRevenue: s.netRevenue, coverage: 0.97, estimatedCommission: commission, estimatedPayout: s.netRevenue - commission, effectiveRate: 0.21 },
  cost: { keptQuantity: s.keptQuantity, costedQuantity: s.keptQuantity - 40, coverage: 0.9, costTotal: Math.round(s.netRevenue * 0.42), costedRevenue: s.netRevenue * 0.9, grossProfit: 0, grossMargin: 0.5, profitQuantity: s.keptQuantity - 40, profitCoverage: 0.9, profitRevenue: s.netRevenue * 0.9, profitCost: 0, profitCommission: 0, netProfit: Math.round(s.netRevenue * 0.19), netMargin: 0.21, costedVariants: 84 },
  platforms: [group('TRENDYOL', 'TRENDYOL', { netRevenue: s.netRevenue * 0.62, grossSales: s.grossSales * 0.62, orders: 480 }), group('HEPSIBURADA', 'HEPSIBURADA', { netRevenue: s.netRevenue * 0.21, grossSales: s.grossSales * 0.21, orders: 160 }), group('N11', 'N11', { netRevenue: s.netRevenue * 0.1, grossSales: s.grossSales * 0.1, orders: 80 }), group('SHOPIFY', 'SHOPIFY', { netRevenue: s.netRevenue * 0.07, grossSales: s.grossSales * 0.07, orders: 58 })],
  cities: [group('İstanbul', 'İstanbul'), group('Ankara', 'Ankara'), group('Bilinmiyor', 'Bilinmiyor')],
  timeSeries: now, previousTimeSeries: before,
  topProducts: [{ variantId: 'v1', productId: 'p1', title: 'Koltuk Kılıfı', sku: 'KLF-1', brand: 'Ravencia', category: 'Kılıf', soldQuantity: 120, returnedQuantity: 4, returnRate: 0.03, salesAmount: 98000, estimatedCommission: 0, commissionCoveredAmount: 0, modelCode: 'KLF-2026', imageUrl: '/pack/brand/ravencia-symbol-256.webp', options: [{ name: 'Beden', value: 'L' }, { name: 'Renk', value: 'Antrasit' }] }],
  topReturned: [], brands: [group('Ravencia', 'Ravencia')], categories: [group('Kılıf', 'Kılıf')],
  returnReasons: [{ reason: 'Beden uymadı', known: true, count: 12 }, { reason: 'Kusurlu', known: true, count: 5 }],
  cancellations: { cancelledOrders: s.cancelledOrders, cancelledQuantity: s.cancelledOrders, cancelRate: 0.031, warningRate: 0.02, platforms: [], reasons: [] },
  cargo: [{ label: 'Trendyol Express', known: true, packages: 520, share: 0.7, averageHoursToShip: 18, measuredShip: 500, averageHoursToDeliver: 52, measuredDeliver: 480, lateShipments: 4, measuredLate: 500, lateRate: 0.01 }, { label: 'Aras Kargo', known: true, packages: 220, share: 0.3, averageHoursToShip: 22, measuredShip: 200, averageHoursToDeliver: 60, measuredDeliver: 190, lateShipments: 3, measuredLate: 200, lateRate: 0.015 }],
  heatmap: Array.from({ length: 7 }, () => Array.from({ length: 24 }, () => Math.round(rnd() * 5))),
  customers: { identifiedOrders: s.orders, unidentifiedOrders: 0, uniqueCustomers: Math.round(s.orders * 0.86), repeatCustomers: 60, repeatRate: 0.09, ordersPerCustomer: 1.16 },
  invoices: { buckets: [{ key: 'INVOICED', label: 'Faturalı', packages: 690, amount: s.netRevenue * 0.93 }, { key: 'NOT_INVOICED', label: 'Faturasız', packages: 50, amount: s.netRevenue * 0.07 }, { key: 'UNKNOWN', label: 'Bilinmiyor', packages: 0, amount: 0 }], platforms: [] },
  stock: { periodDays: 30, bestSellers: [{ variantId: 'v1', title: 'Koltuk Kılıfı', sku: 'KLF-1', soldQuantity: 120, available: 14, dailyAverage: 4, daysLeft: 3.5 }, { variantId: 'v2', title: 'Masa Örtüsü', sku: 'MO-1', soldQuantity: 80, available: 200, dailyAverage: 2.6, daysLeft: 76 }], soldOut: [] },
  channels: [{ id: 'c1', platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol', isTest: false }],
  costs: {
    source: 'trendyol-finance', lastSyncedAt: '2026-10-23T08:00:00Z', coveredFrom: '2026-07-24T00:00:00Z', coveredThrough: '2026-10-23T08:00:00Z', financeOrders: 600, settledOrders: 420, coverage: 0.7, realCostTotal: 0,
    commission: { real: commission * 0.7, realOrders: 420, estimatedForSettled: 0, estimatedForUnsettled: commission * 0.3, combined: commission },
    cargo: { real: cargo * 0.8, billedOrders: 560, unbilledOrders: 140, averagePerOrder: 72, sampleOrders: 560, estimated: cargo * 0.2, combined: cargo },
    lines: [{ category: 'SERVICE_FEE', label: 'Hizmet', isCost: true, amount: fees * 0.6, records: 30, orderLinked: 0, unlinked: 0 }, { category: 'STOPPAGE', label: 'Stopaj', isCost: true, amount: fees * 0.1, records: 30, orderLinked: 0, unlinked: 0 }, { category: 'ADVERTISING', label: 'Reklam', isCost: true, amount: fees * 0.3, records: 4, orderLinked: 0, unlinked: 0 }],
    channels: [], products: []
  },
  geography: { countries: [group('TR', 'Türkiye')], previousCities: [], previousCountries: [], abroadOrders: 0 },
  dataNotes: ['2 siparişte il okunamadı.']
}


const shares = { TRENDYOL: [0.6, 0], HEPSIBURADA: [0.25, 3], N11: [0.1, 7], SHOPIFY: [0.05, 11] }
report.platformHeatmaps = Object.entries(shares).map(([code, [share, shift]]) => ({ platformCode: code, heatmap: report.heatmap.map(row => row.map((value, hour) => Math.round(value * share * 2 * (0.5 + ((hour + shift) % 24) / 24)))) }))
report.heatmap = Array.from({ length: 7 }, (_, d) => Array.from({ length: 24 }, (_, h) => report.platformHeatmaps.reduce((s, p) => s + p.heatmap[d][h], 0)))
export { report }
// Products as the marketplace's product report shows them, the movers and the cancelled amount.
const names = [['Kadın Ceket', 'RYP001', 'Siyah'], ['Deri Mont', 'SG1461', 'Kahverengi'], ['Yün Kaban', 'MZ029YD', 'Bej'], ['Triko Hırka', 'MZ043DDC', 'Mor'], ['Kapüşonlu Yağmurluk', 'MZ025DY', 'Lacivert'], ['Uzun Trençkot', 'MZ031YRYD', 'Siyah'], ['Şişme Yelek', 'SLR009BYD', 'Lacivert'], ['Polar Sweat', 'FK5404', 'Haki']]
const product = ([title, model, colour], index, sold, before) => ({ variantId: `m${index}`, productId: `p${index}`, title, sku: `${model}-${index}`, brand: 'Ravencia', category: 'Dış Giyim', soldQuantity: sold, returnedQuantity: index % 3 === 0 ? 1 : 0, returnRate: index % 3 === 0 ? 1 / Math.max(1, sold) : 0,
  salesAmount: sold * (520 + index * 90), estimatedCommission: sold * (520 + index * 90) * 0.214, commissionCoveredAmount: sold * (520 + index * 90), netAmount: (sold - (index % 3 === 0 ? 1 : 0)) * (520 + index * 90), modelCode: model, imageUrl: null,
  options: [{ name: 'Renk', value: colour }, { name: 'Beden', value: ['S', 'M', 'L'][index % 3] }], previousSoldQuantity: before, available: 40 - index * 5 })
report.topProducts = names.map((name, index) => product(name, index, 24 - index * 2, [8, 14, 3, 25, 9, 12, 0, 20][index]))
report.rising = report.topProducts.filter(item => item.soldQuantity > item.previousSoldQuantity).sort((a, b) => (b.soldQuantity - b.previousSoldQuantity) - (a.soldQuantity - a.previousSoldQuantity))
report.falling = [...report.topProducts.filter(item => item.soldQuantity < item.previousSoldQuantity), product(['Kaşe Palto', 'KM1833', 'Haki'], 9, 0, 11)].sort((a, b) => (b.previousSoldQuantity - b.soldQuantity) - (a.previousSoldQuantity - a.soldQuantity))
report.cancellations.cancelledAmount = report.cancellations.cancelledQuantity * 812.5
