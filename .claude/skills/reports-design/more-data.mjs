// The Stok, Hakediş and Sipariş Kârlılığı answers for the preview, with figures as large as a real store's, so a strip's
// labels and figures are measured at their real length (the audit's CLIP and height checks).
const group = (key, label, units, capital, value, known = true) =>
  ({ key, label, known, products: 12, variants: 40, units, capital, value, pricedUnits: value === null ? 0 : units, soldUnits: 180, coverDays: 64 })
export const inventory = {
  generatedAt: '2026-11-07T09:00:00Z', from: '2026-10-09', to: '2026-11-07', periodDays: 30, deadDays: 90,
  summary: { units: 4872, availableUnits: 4810, products: 212, stockedProducts: 188, variants: 1460, stockedVariants: 1104, emptyVariants: 356, lowStockProducts: 37, capital: 1638267.4, capitalExVat: 1365222.83,
    missingCostVariants: 41, missingCostUnits: 96, value: 3912880.5, pricedUnits: 4872, missingPriceUnits: 12, profit: 2274613.1, margin: 0.58, profitOnCost: 1.388, soldUnits: 700, dailySales: 23.3,
    coverDays: 209, deadVariants: 214, deadUnits: 610, deadCapital: 204118.2 },
  platforms: [{ connectionId: 's1', platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol', isTest: false, offers: 1300, activeOffers: 1100, listedUnits: 4700, unlistedUnits: 172, value: 3800000, pricedUnits: 4700, averagePrice: 808.5 }],
  categories: [group('Bluz', 'Bluz', 2100, 700000, 1700000), group('Elbise', 'Elbise', 1600, 600000, 1400000)],
  colors: [group('Siyah', 'Siyah', 1800, 600000, 1400000), group('Bej', 'Bej', 1200, 400000, 950000)],
  sizes: [group('M', 'M', 1500, 500000, 1200000), group('L', 'L', 1400, 470000, 1100000)],
  priceBands: [group('band-2', '750 – 1.000 TL', 3000, 1000000, 2500000), group('none', 'Fiyatı Yok', 12, 4000, null, false)],
  cover: [group('31-90', '31 – 90 gün', 3000, 1000000, 2500000)],
  topCapital: [{ key: 'p1', productId: 'p1', variantId: null, title: 'Kadın Bej Büyük Beden Viskon Bluz', options: null, modelCode: 'M1', category: 'Bluz', imageUrl: null, units: 160, capital: 52000, value: 126000, soldUnits: 40, recentSoldUnits: 40, coverDays: 120 }],
  dead: [{ key: 'v2', productId: 'p1', variantId: 'v2', title: 'Bluz Beyaz', options: 'Beyaz / M', modelCode: 'M1', category: 'Bluz', imageUrl: null, units: 2, capital: 640, value: 1800, soldUnits: 0, recentSoldUnits: 0, coverDays: null }],
  channels: []
}
const due = (key, kind, platformCode, storeName, region, day, amount, records = 12) =>
  ({ key, kind, connectionId: `s-${platformCode}`, platformCode, storeName, region, regionName: null, day, amount, linesAmount: null, records, status: 'EXPECTED' })
export const payouts = {
  today: '2026-11-07', daysBack: 90, expected: 412867.35, expectedRecords: 540, unknown: 18236.4, unknownRecords: 22, earlyPaymentsTaken: 0, earlyPayments: 0, pendingDelivery: 98312.75, pendingDeliveryOrders: 132,
  next: due('next', 'PAYOUT_DAY', 'TRENDYOL', 'Ravencia', 'TR_GULF', '2026-11-12', 187233.9),
  lastPaid: { ...due('payout:1', 'PAYOUT', 'TRENDYOL', 'Ravencia', 'TR_GULF', '2026-11-05', 201455.2), regionName: 'Türkiye', linesAmount: 201455.2, status: 'PAID' },
  platforms: [
    { platformCode: 'TRENDYOL', expected: 368120.4, records: 480, nextDay: '2026-11-12', nextAmount: 187233.9, unknown: 18236.4, pendingDelivery: 90120.5, pendingDeliveryOrders: 120 },
    { platformCode: 'HEPSIBURADA', expected: 32111.5, records: 40, nextDay: '2026-11-14', nextAmount: 21100, unknown: 0, pendingDelivery: 6100, pendingDeliveryOrders: 8 },
    { platformCode: 'N11', expected: 8120.45, records: 12, nextDay: '2026-11-13', nextAmount: 8120.45, unknown: 0, pendingDelivery: 1500, pendingDeliveryOrders: 2 },
    { platformCode: 'PAZARAMA', expected: 4515, records: 8, nextDay: '2026-11-10', nextAmount: 4515, unknown: 0, pendingDelivery: 592.25, pendingDeliveryOrders: 2 }],
  regions: [
    { region: 'TR_GULF', expected: 301220.4, records: 400, nextDay: '2026-11-12', nextAmount: 187233.9, unknown: 12000, pendingDelivery: 80000, pendingDeliveryOrders: 100 },
    { region: 'EUROPE', expected: 52900, records: 60, nextDay: '2026-11-28', nextAmount: 52900, unknown: 6236.4, pendingDelivery: 8000, pendingDeliveryOrders: 15 },
    { region: 'AZ', expected: 14000, records: 20, nextDay: '2026-11-19', nextAmount: 14000, unknown: 0, pendingDelivery: 2120.5, pendingDeliveryOrders: 5 }],
  upcoming: [
    due('d1', 'PAYOUT_DAY', 'PAZARAMA', 'Pazarama', 'NONE', '2026-11-10', 4515),
    due('d2', 'PAYOUT_DAY', 'TRENDYOL', 'Ravencia', 'TR_GULF', '2026-11-12', 187233.9),
    due('d3', 'DUE_DAY', 'N11', 'n11', 'NONE', '2026-11-13', 8120.45),
    due('d4', 'DUE_DAY', 'HEPSIBURADA', 'Hepsiburada', 'NONE', '2026-11-14', 21100),
    due('d5', 'DUE_DAY', 'TRENDYOL', 'Ravencia', 'EUROPE', '2026-11-28', 52900)],
  past: [{ ...due('payout:1', 'PAYOUT', 'TRENDYOL', 'Ravencia', 'TR_GULF', '2026-11-05', 201455.2), regionName: 'Türkiye', linesAmount: 201455.2, status: 'PAID' }],
  coverage: [{ connectionId: 's-TRENDYOL', platformCode: 'TRENDYOL', storeName: 'Ravencia', state: 'READ', lastSyncedAt: '2026-11-07T09:00:00Z' }]
}
const order = (index) => ({ orderId: `o${index}`, orderNumber: `1093${index}4471`, platformCode: 'TRENDYOL', storeName: 'Ravencia', storeFront: null, microExport: false, orderedAt: '2026-11-06T10:00:00Z',
  state: 'SOLD', amount: 863.4, cost: 320, profit: 168.2, profitOnCost: 0.5256, margin: 0.1948, costs: [], lines: [], unreadableLines: 0 })
export const orderProfit = { items: Array.from({ length: 10 }, (_, index) => order(index)), totalCount: 582, page: 0, pageSize: 50, amount: 502516.4, profit: 97884.12, missingCost: 23,
  profitAmount: 480120.3, profitOrders: 559, margin: 20.39, missingCargo: 4, missingCommission: 0 }
