// The sales report's contract (GET /reports/sales, ReportsContracts.cs) and the formatting the page shares.
export type ReportSummary = {
  orders: number; cancelledOrders: number; cancelRate: number | null; soldQuantity: number; returnedQuantity: number; keptQuantity: number
  returnClaims: number; returnRate: number | null; ordersWithAmount: number; grossSales: number; returnedAmount: number; netRevenue: number
  averageOrderValue: number | null; ordersWithoutCity: number; ordersWithoutAmount: number; otherCurrencyOrders: number
  // The same orders at their list price and the marketplace's discount on them (absent in an older server's answer).
  listAmount?: number; discountAmount?: number
}
export type ReportGroup = {
  key: string; label: string; known: boolean; isTest: boolean; orders: number; soldQuantity: number; returnedQuantity: number; returnClaims: number
  returnRate: number | null; ordersWithAmount: number; grossSales: number; netRevenue: number; estimatedCommission: number; commissionCoveredRevenue: number
  children: ReportGroup[]; split: ReportGroup[]
}
export type ReportTimePoint = { start: string; orders: number; soldQuantity: number; returnedQuantity: number; netRevenue: number; cancelledOrders: number }
export type ReportProduct = {
  variantId: string | null; productId: string | null; title: string; sku: string; brand: string; category: string; soldQuantity: number; returnedQuantity: number
  returnRate: number | null; salesAmount: number; estimatedCommission: number; commissionCoveredAmount: number
  modelCode?: string | null; imageUrl?: string | null; options?: Array<{ name: string; value: string }> | null
  netAmount?: number; previousSoldQuantity?: number | null; available?: number | null
}
export type ReportReason = { reason: string; known: boolean; count: number }
export type ReportCancelGroup = { key: string; label: string; orders: number; cancelledOrders: number; cancelledQuantity: number; cancelRate: number | null }
export type ReportCargo = {
  label: string; known: boolean; packages: number; share: number | null; averageHoursToShip: number | null; measuredShip: number
  averageHoursToDeliver: number | null; measuredDeliver: number; lateShipments: number; measuredLate: number; lateRate: number | null
}
export type ReportStockRow = { variantId: string | null; title: string; sku: string; soldQuantity: number; available: number | null; dailyAverage: number | null; daysLeft: number | null }
export type ReportSales = {
  from: string; to: string; previousFrom: string; previousTo: string; granularity: 'day' | 'week' | 'month'; includesTest: boolean; generatedAt: string
  summary: ReportSummary; previousSummary: ReportSummary
  commission: { coveredRevenue: number; coverage: number | null; estimatedCommission: number; estimatedPayout: number; effectiveRate: number | null }
  cost: {
    keptQuantity: number; costedQuantity: number; coverage: number | null; costTotal: number; costedRevenue: number; grossProfit: number; grossMargin: number | null
    profitQuantity: number; profitCoverage: number | null; profitRevenue: number; profitCost: number; profitCommission: number; netProfit: number; netMargin: number | null; costedVariants: number
  }
  platforms: ReportGroup[]; cities: ReportGroup[]; timeSeries: ReportTimePoint[]; previousTimeSeries: ReportTimePoint[]
  topProducts: ReportProduct[]; topReturned: ReportProduct[]; brands: ReportGroup[]; categories: ReportGroup[]; returnReasons: ReportReason[]
  cancellations: { cancelledOrders: number; cancelledQuantity: number; cancelRate: number | null; warningRate: number; platforms: ReportCancelGroup[]; reasons: ReportReason[]; cancelledAmount?: number }
  cargo: ReportCargo[]; heatmap: number[][]; platformHeatmaps?: Array<{ platformCode: string; heatmap: number[][] }> | null
  rising?: ReportProduct[] | null; falling?: ReportProduct[] | null
  customers: { identifiedOrders: number; unidentifiedOrders: number; uniqueCustomers: number; repeatCustomers: number; repeatRate: number | null; ordersPerCustomer: number | null }
  invoices: { buckets: Array<{ key: string; label: string; packages: number; amount: number }>; platforms: Array<{ platformCode: string; invoiced: number; notInvoiced: number; unknown: number }> }
  stock: { periodDays: number; bestSellers: ReportStockRow[]; soldOut: ReportStockRow[] }
  channels: Array<{ id: string; platformCode: string; displayName: string; isTest: boolean }>
  costs: ReportCosts
  geography: ReportGeography
  dataNotes: string[]
}

// The sales map: countries for every order (a Trendyol cross-border storefront's order in its country), the previous
// period's cities and countries for the change, and how many orders went abroad (left out of `cities`).
export type ReportGeography = { countries: ReportGroup[]; previousCities: ReportGroup[]; previousCountries: ReportGroup[]; abroadOrders: number }

// The real costs read from Trendyol's account statement (source "trendyol-finance"), next to the estimates; with
// "estimate" nothing was read for the period and every field below the counts is null or empty.
export type ReportCosts = {
  source: 'trendyol-finance' | 'estimate'; lastSyncedAt: string | null; coveredFrom: string | null; coveredThrough: string | null
  financeOrders: number; settledOrders: number; coverage: number | null; realCostTotal: number | null
  commission: { real: number; realOrders: number; estimatedForSettled: number; estimatedForUnsettled: number; combined: number } | null
  cargo: { real: number; billedOrders: number; unbilledOrders: number; averagePerOrder: number | null; sampleOrders: number; estimated: number | null; combined: number } | null
  lines: Array<{ category: string; label: string; isCost: boolean; amount: number; records: number; orderLinked: number; unlinked: number }>
  channels: Array<{ connectionId: string; platformCode: string; displayName: string; source: string; realCommission: number; estimatedCommission: number; amounts: Array<{ category: string; amount: number }>; estimatedCargo: number; total: number }>
  products: Array<{ variantId: string | null; title: string; sku: string; barcode: string; commission: number; discount: number; coupon: number; records: number }>
}

export const unknownLabel = 'Bilinmiyor'

const platformNames: Record<string, string> = { TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', N11: 'n11', SHOPIFY: 'Shopify', PAZARAMA: 'Pazarama', PTTAVM: 'PTT AVM' }
export function platformName(code: string) { return platformNames[code.toUpperCase()] ?? code }

// Each platform's own colour, on the sales map and the overview's charts alike.
const platformColors: Record<string, string> = { TRENDYOL: '#f27a1a', HEPSIBURADA: '#f5b301', SHOPIFY: '#5e8e3e', N11: '#7c5cff', PAZARAMA: '#2f9bd8', PTTAVM: '#1f3a8a' }
export function platformColor(code: string) { return platformColors[code.toUpperCase()] ?? '#8a8f98' }

const number = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 })
const whole = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 })
const money = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 2, minimumFractionDigits: 2 })
const moneyShort = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', notation: 'compact', maximumFractionDigits: 1 })

export function formatNumber(value: number | null | undefined) { return value === null || value === undefined || Number.isNaN(value) ? '—' : number.format(value) }
export function formatCount(value: number | null | undefined) { return value === null || value === undefined || Number.isNaN(value) ? '—' : whole.format(value) }
export function formatTl(value: number | null | undefined) { return value === null || value === undefined || Number.isNaN(value) ? '—' : money.format(value) }
const moneyWhole = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 })
export function formatTlWhole(value: number) { return moneyWhole.format(value) }
export function formatTlShort(value: number) { return Math.abs(value) >= 10_000 ? moneyShort.format(value) : money.format(value) }
export function formatPercent(rate: number | null | undefined, digits = 1) {
  return rate === null || rate === undefined || Number.isNaN(rate) ? '—' : `%${(rate * 100).toLocaleString('tr-TR', { minimumFractionDigits: digits, maximumFractionDigits: digits })}`
}
export function formatHours(hours: number | null | undefined) {
  if (hours === null || hours === undefined) return '—'
  return hours >= 48 ? `${(hours / 24).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} gün` : `${hours.toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sa`
}
export function formatDay(value: string) { const [year, month, day] = value.split('-'); return `${day}.${month}.${year}` }

// A money value that only means something when the bucket had orders with an amount (Shopify sends none).
export function moneyOrMissing(value: number, ordersWithAmount: number) { return ordersWithAmount > 0 ? formatTl(value) : 'Tutar yok' }

// The change against the previous period. A rate is compared in points; a count or amount as a percentage, and not at
// all when the previous period had nothing (no "+∞%").
export type Change = { text: string; direction: 'up' | 'down' | 'flat' }
export function change(current: number | null, previous: number | null, kind: 'value' | 'rate' = 'value'): Change | null {
  if (current === null || previous === null) return null
  if (kind === 'rate') {
    const points = (current - previous) * 100
    if (Math.abs(points) < 0.05) return { text: '0,0 puan', direction: 'flat' }
    return { text: `${points > 0 ? '+' : '−'}${Math.abs(points).toLocaleString('tr-TR', { maximumFractionDigits: 1, minimumFractionDigits: 1 })} puan`, direction: points > 0 ? 'up' : 'down' }
  }
  if (previous === 0) return current === 0 ? { text: '%0', direction: 'flat' } : null
  const percent = (current - previous) / Math.abs(previous) * 100
  if (Math.abs(percent) < 0.05) return { text: '%0', direction: 'flat' }
  return { text: `${percent > 0 ? '+' : '−'}%${Math.abs(percent).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`, direction: percent > 0 ? 'up' : 'down' }
}

// The report request: calendar days (yyyy-MM-dd), platforms and buckets; an empty choice is left out.
export function reportQuery(filters: { from: string; to: string; platforms: string[] | null; granularity: string; includeTest: boolean }) {
  const query = new URLSearchParams({ from: filters.from, to: filters.to })
  if (filters.platforms?.length) query.set('platformCodes', filters.platforms.join(','))
  if (filters.granularity) query.set('granularity', filters.granularity)
  if (filters.includeTest) query.set('includeTest', 'true')
  return query.toString()
}

// A spreadsheet reads a cell starting with = + - @ (or a tab / carriage return) as a formula: such text gets a leading apostrophe.
function csvSafe(text: string) { return /^[=+\-@\t\r]/.test(text) ? `'${text}` : text }
export type CsvColumn<T> = { header: string; value: (row: T) => string | number | null | undefined }
export function toCsv<T>(rows: T[], columns: CsvColumn<T>[]) {
  const cell = (value: string | number | null | undefined) => {
    const text = value === null || value === undefined ? '' : typeof value === 'number' ? value.toLocaleString('tr-TR', { useGrouping: false, maximumFractionDigits: 4 }) : csvSafe(value)
    return `"${text.replaceAll('"', '""')}"`
  }
  // Semicolons and a byte-order mark: Excel in Turkish opens it in columns with the letters right.
  return '﻿' + [columns.map(column => cell(column.header)).join(';'), ...rows.map(row => columns.map(column => cell(column.value(row))).join(';'))].join('\r\n')
}
export function downloadCsv(name: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 0)
}

// "Bilinmiyor" rows always come last, whatever the sort.
export function sortRows<T>(rows: T[], value: (row: T) => number | string | null, direction: 'asc' | 'desc', unknown: (row: T) => boolean) {
  return [...rows].sort((left, right) => {
    const unknownOrder = Number(unknown(left)) - Number(unknown(right))
    if (unknownOrder !== 0) return unknownOrder
    const a = value(left)
    const b = value(right)
    const order = a === null ? 1 : b === null ? -1 : typeof a === 'number' && typeof b === 'number' ? a - b : String(a).localeCompare(String(b), 'tr')
    return direction === 'asc' ? order : -order
  })
}
