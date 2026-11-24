// Raporlar (/reports) and the data behind its tabs: the sales report (report-sales.mjs), stock, payouts and order
// profitability (report-more.mjs), the ad report, the profit rate and the price assumptions. Synthetic figures only.
import { report } from './report-sales.mjs'
import { inventory, orderProfit, payouts } from './report-more.mjs'
import { ist } from './sales.mjs'

const adReport = {
  stores: [{ connectionId: 'c-ty', storeName: 'Ravencia Moda Trendyol' }],
  reports: [{
    id: 'ad1', connectionId: 'c-ty', storeName: 'Ravencia Moda Trendyol', fileName: 'Ürün Reklamları Raporum - 19.10.2026-09.00.xlsx', exportedAt: ist('2026-10-19 09:00'), exportedAtFromFileName: true, uploadedAt: ist('2026-10-19 09:12'),
    totals: { spent: 12545.31, revenue: 124357.98, directRevenue: 25178.07, indirectRevenue: 99179.91, roas: 9.91, clicks: 11787, impressions: 470337, cpc: 1.06, ctr: 0.0251, sales: 139, directSales: 29, indirectSales: 110 },
    campaigns: [
      { name: 'Ürün-Kampanya A', status: 'Yayında', start: '2026-10-05 10:00', end: null, productCount: 27, totalBudget: 10000, dailyBudget: null, remainingBudget: 0, spent: 10000, cpcBid: null, actualCpc: 1.02, clicks: 9800, impressions: 400000, directSales: 20, indirectSales: 80, totalSales: 100, directRevenue: 18000, indirectRevenue: 80000, totalRevenue: 98000, roas: 9.8 },
      { name: 'Ürün-Kampanya B', status: 'Durduruldu', start: '2026-10-11 15:20', end: '2026-10-16 10:00', productCount: 17, totalBudget: null, dailyBudget: 750, remainingBudget: 704.69, spent: 2545.31, cpcBid: '1,10 - 1,30', actualCpc: 1.28, clicks: 1987, impressions: 70337, directSales: 9, indirectSales: 30, totalSales: 39, directRevenue: 7178.07, indirectRevenue: 19179.91, totalRevenue: 26357.98, roas: 10.36 },
    ],
  }],
}

const payoutLines = {
  items: [
    { externalId: '900000001', connectionId: 'c-ty', platformCode: 'TRENDYOL', transactionType: 'Satış', category: 'SALE', transactionDate: ist('2026-10-14 10:00'), dueDay: '2026-10-22', orderNumber: '10400000001', invoiceNumber: null, country: 'Türkiye', region: 'TR_GULF', gross: 1299.9, commission: 279.5, net: 1020.4, status: 'EXPECTED', paymentOrderId: null, barcode: '8684471201111', currency: 'TRY' },
    { externalId: '900000002', connectionId: 'c-ty', platformCode: 'TRENDYOL', transactionType: 'Satış', category: 'SALE', transactionDate: ist('2026-10-14 15:40'), dueDay: '2026-10-22', orderNumber: '10400000002', invoiceNumber: null, country: 'Türkiye', region: 'TR_GULF', gross: 899, commission: 193.3, net: 705.7, status: 'EXPECTED', paymentOrderId: null, barcode: '8684471202222', currency: 'TRY' },
  ],
  totalCount: 2, page: 0, pageSize: 100, net: 1726.1, nextCursor: null, hasMore: false,
}

const assumptionStore = (connectionId, platformCode, displayName, orders, sales, rate) => ({
  connectionId, platformCode, displayName, orders, sales, commissionRate: rate, commissionRateBeforeVat: rate === null ? null : Math.round(rate / 1.2 * 1000) / 1000, takesCommission: rate !== null, realCommissionOrders: Math.round(orders * 0.6),
  cargo: 72, cargoSource: 'tariff', serviceFee: 10.19, serviceFeeRate: 0, serviceFeeSource: 'fixed', serviceFeeOrders: orders, azerbaijanServiceFee: null, internationalServiceFeeRate: null, paymentFeeRate: null, paymentFeeFixed: null,
})
const priceAssumptions = {
  from: '2026-09-21', to: '2026-10-20', days: 30, clothingOnly: true, minimumOrders: 10,
  stores: [assumptionStore('c-ty', 'TRENDYOL', 'Ravencia Moda Trendyol', 480, 412000, 0.21), assumptionStore('c-hb', 'HEPSIBURADA', 'Ravencia Moda Hepsiburada', 160, 141000, 0.19), assumptionStore('c-sh', 'SHOPIFY', 'Ravencia Shopify Mağazası', 58, 61000, null)],
}

export const reports = [
  ['GET /reports/sales', report],
  ['GET /reports/stock', inventory],
  ['GET /reports/payouts/lines', payoutLines],
  ['GET /reports/payouts', payouts],
  ['GET /reports/order-profitability', orderProfit],
  ['GET /reports/ad-report', adReport],
  ['GET /reports/price-assumptions', priceAssumptions],
  ['GET /reports/profit-rate', { months: [{ from: '2026-07-01', to: '2026-07-31', rate: 1.42 }, { from: '2026-08-01', to: '2026-08-31', rate: 1.51 }, { from: '2026-09-01', to: '2026-09-30', rate: 1.5875 }] }],
]
