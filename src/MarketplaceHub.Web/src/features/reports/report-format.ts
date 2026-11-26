import { dateRangePresets } from '../../shared/date-range'
import { istanbulDateKey } from '../../shared/format-date'
import { noValue } from '../../shared/metric-value'
import { platformDisplayName, platformDisplayOrder } from '../../shared/platform-names'
import { newChangeText } from '../../shared/components/ChangePill'
import type { ApiAnswer } from '../../shared/api/api-answer'

// The periods one click away above the reports: the panel's shared ranges (dateRangePresets; weeks start on Monday) for
// today's Istanbul calendar day, the account's time zone, as on Genel Bakış, so a computer set to another time zone does
// not move "Bugün" or "Bu Ay" to its own calendar. Any other period is picked in the date picker beside them ("Özel").
const presetKeys: Record<string, string> = { 'Bugün': 'today', 'Dün': 'yesterday', 'Bu Hafta': 'week', 'Bu Ay': 'month', 'Geçen Ay': 'last-month', 'Son 30 Gün': '30', 'Bu Yıl': 'year' }
export function periodPresets(today = istanbulDateKey()) {
  return dateRangePresets(today).map(preset => ({ key: presetKeys[preset.label] ?? preset.label, label: preset.label, range: preset.range }))
}

// The product cost the profit takes off, as the server's ReportProfitCost (ReportProfitRate.cs) works it out (owner,
// 13 Nov 2026). The deductions come off every sale, so the cost must cover every kept unit too: the profit is worked out
// only when at least 95 % of the kept units carry a cost (usable; else "Maliyet Eksik"). Between 95 and 100 % the units
// without a cost are priced by their share of the sales: the known cost's ratio to its units' revenue times the revenue
// of the units without one; without that revenue (an older report) the average cost per unit stands in. A profit with an
// estimated part is marked "Tahmini".
export const minimumCostCoverage = 0.95
export type ProfitCost = { usable: boolean; known: number; estimated: number; missing: number; total: number }
export function profitCost(cost: ReportSales['cost']): ProfitCost {
  const missing = Math.max(0, cost.keptQuantity - cost.costedQuantity)
  const usable = cost.costedQuantity > 0 && cost.costTotal !== 0 && cost.costedQuantity >= cost.keptQuantity * minimumCostCoverage
  const known = cost.costTotal
  if (!usable || missing === 0) return { usable, known, estimated: 0, missing, total: known }
  const keptRevenue = cost.keptRevenue ?? 0
  const raw = cost.costedRevenue > 0 && keptRevenue >= cost.costedRevenue
    ? known * (keptRevenue - cost.costedRevenue) / cost.costedRevenue
    : known * missing / cost.costedQuantity
  const estimated = Math.round(raw * 100) / 100
  return { usable, known, estimated, missing, total: known + estimated }
}

// The sales report's contract (GET /reports/sales, ReportsContracts.cs) and the formatting the page shares.
export type ReportSummary = ReportSalesAnswer['summary']
export type ReportGroup = ReportSalesAnswer['platforms'][number]
export type ReportTimePoint = ReportSalesAnswer['timeSeries'][number]
export type ReportProduct = ReportSalesAnswer['topProducts'][number]
export type ReportReason = ReportSalesAnswer['returnReasons'][number]
export type ReportCancelGroup = ReportSalesAnswer['cancellations']['platforms'][number]
export type ReportCargo = ReportSalesAnswer['cargo'][number]
export type ReportStockRow = ReportSalesAnswer['stock']['bestSellers'][number]
// GET /reports/sales. CONTRACT: every member agrees with the contract (ReportSalesView) but two codes the API declares as
// strings: granularity (ReportBuilder.Day / Week / Month) and finance.source (ReportCosts.FinanceSource / EstimateSource).
// It stays written here because those two sit deep in the answer and the report tests build it whole.
type ReportSalesAnswer = ApiAnswer<'getReportsSales'>
// CONTRACT: the API declares the buckets' size as a string; ReportsService writes day, week or month.
// and the costs' source as a string; ReportsService writes trendyol-finance or estimate.
export type ReportSales = Omit<ReportSalesAnswer, 'granularity' | 'costs' | 'previousAccount'> & {
  granularity: 'day' | 'week' | 'month'; costs: ReportCosts
  previousAccount: (Omit<NonNullable<ReportSalesAnswer['previousAccount']>, 'costs'> & { costs: ReportCosts }) | null
}

const granularities: ReadonlySet<string> = new Set<ReportSales['granularity']>(['day', 'week', 'month'])
const isGranularity = (value: string): value is ReportSales['granularity'] => granularities.has(value)
// An unknown cost source is never shown as Trendyol's real finance figures: it reads as an estimate.
const readCosts = (costs: ReportSalesAnswer['costs']): ReportCosts => ({ ...costs, source: costs.source === 'trendyol-finance' ? 'trendyol-finance' : 'estimate' })

/** The sales report as the page reads it; an unknown bucket size reads as days (the buckets' own dates still come from the server). */
export function readReportSales(raw: ReportSalesAnswer): ReportSales {
  return {
    ...raw,
    granularity: isGranularity(raw.granularity) ? raw.granularity : 'day',
    costs: readCosts(raw.costs),
    previousAccount: raw.previousAccount ? { ...raw.previousAccount, costs: readCosts(raw.previousAccount.costs) } : null
  }
}

// The sales map: countries for every order (a Trendyol cross-border storefront's order in its country), the previous
// period's cities and countries for the change, and how many orders went abroad (left out of `cities`).
export type ReportGeography = ReportSalesAnswer['geography']

// Of the orders costed from a platform's cargo tariff, how many took their parcel's desi from the marketplace's order,
// the products' entered desi, the desi measured on billed Trendyol parcels (measuredParcels of them) or a default.
export type ReportDesiSources = { marketplace: number; manual: number; measured: number; default: number; measuredParcels: number }

// "Desi: 5 siparişte pazaryerinin bildirdiği, 30 siparişte Trendyol kargo faturalarından ölçülen (1.320 koli)…"
export function desiSourcesNote(sources: ReportDesiSources | null | undefined) {
  if (!sources) return ''
  const count = (value: number) => trNumber(value)
  const parts = [
    sources.marketplace ? `${count(sources.marketplace)} siparişte pazaryerinin siparişte bildirdiği` : '',
    sources.manual ? `${count(sources.manual)} siparişte ürüne girilen` : '',
    sources.measured ? `${count(sources.measured)} siparişte Trendyol kargo faturalarından ölçülen (${count(sources.measuredParcels)} koli)` : '',
    sources.default ? `${count(sources.default)} siparişte ${sources.measuredParcels ? 'mağaza ortalaması' : 'varsayılan 1 desi'}` : ''
  ].filter(Boolean)
  return parts.length ? ` Desi: ${parts.join(', ')}.` : ''
}

// The real costs read from Trendyol's account statement (source "trendyol-finance"), next to the estimates; with
// "estimate" nothing was read for the period and every field below the counts is null or empty.
export type ReportCosts = {
  source: 'trendyol-finance' | 'estimate'; lastSyncedAt: string | null; coveredFrom: string | null; coveredThrough: string | null
  financeOrders: number; settledOrders: number; coverage: number | null; realCostTotal: number | null
  // missingOrders: orders whose commission is unknown (no rate on the order nor of its store), left out of the estimate.
  commission: { real: number; realOrders: number; estimatedForSettled: number; estimatedForUnsettled: number; combined: number; missingOrders?: number } | null
  cargo: { real: number; billedOrders: number; unbilledOrders: number; averagePerOrder: number | null; sampleOrders: number; estimated: number | null; combined: number; platformOrders?: number; platformEstimated?: number; desiSources?: ReportDesiSources | null
    // unbilledReturns: returned orders whose return cargo is estimated; unestimatedOrders: orders whose cargo could not be
    // estimated (store without a tariff), so estimated and combined are short of them.
    unbilledReturns?: number; unestimatedOrders?: number
    // returnBilled: the billed return cargo of returnBilledOrders of the period's orders (part of real); returnOperationFees:
    // Trendyol's "Yurtdışı Operasyon İade Bedeli" records dated in the period (they name no order).
    returnBilled?: number; returnBilledOrders?: number; returnOperationFees?: number; returnOperationRecords?: number } | null
  // The costs of the period's orders only: amount = orderLinked (their own records) + estimated (orders without one).
  lines: Array<{ category: string; label: string; isCost: boolean; amount: number; records: number; orderLinked: number; estimated?: number }>
  // The records of the period's dates tied to no order of the period (the previous payment day's service fee and
  // withholding tax, advertising): shown apart and taken off the net profit in a line of their own (never off an order's
  // profit); unlinkedTotal adds up the deductions (isCost).
  unlinked?: Array<{ category: string; label: string; isCost: boolean; amount: number; records: number }> | null
  unlinkedTotal?: number
  // The service fee and withholding tax invoices whose payout's lines are not read yet: their orders' estimates already
  // take them off, so they are only told (tahminle karşılandı), never taken off again.
  coveredByEstimate?: Array<{ category: string; label: string; isCost: boolean; amount: number; records: number }> | null
  // The net VAT of the Trendyol orders (server-side estimate): sales VAT less the VAT in the product cost and in Trendyol's
  // charges; below zero it is carried forward.
  netVat?: { salesVat: number; productCostVat: number; expenseVat: number; net: number } | null
  channels: Array<{ connectionId: string; platformCode: string; displayName: string; source: string; realCommission: number; estimatedCommission: number; amounts: Array<{ category: string; amount: number }>; estimatedCargo: number; total: number }>
  products: Array<{ variantId: string | null; title: string; sku: string; barcode: string; commission: number; discount: number; coupon: number; records: number }>
}

export const unknownLabel = 'Bilinmiyor'

export function platformName(code: string) { return platformDisplayName(code) }

// The channels that sell, in the order the panel lists them; an invoicing connection (Trendyol e-Faturam) is none.
const salesChannelOrder: readonly string[] = platformDisplayOrder
export const isSalesChannel = (code: string) => salesChannelOrder.includes(code.toUpperCase())
export const bySalesChannel = (a: string, b: string) => salesChannelOrder.indexOf(a.toUpperCase()) - salesChannelOrder.indexOf(b.toUpperCase())

// A variant's colour and size out of all its catalogue attributes (fabric, collar, maker's address… stay out).
const colourNames = /^(renk|color|colour)$/i
const sizeNames = /^(beden|size|ebat|numara)$/i
export function variantColourAndSize(options: Array<{ name: string; value: string }> | null | undefined) {
  const find = (pattern: RegExp) => options?.find(option => pattern.test(option.name.trim()))?.value?.trim() || null
  return { colour: find(colourNames), size: find(sizeNames) }
}

// Each platform's own colour, on the sales map and the overview's charts alike.
const platformColors: Record<string, string> = { TRENDYOL: '#f27a1a', HEPSIBURADA: '#f5b301', SHOPIFY: '#5e8e3e', N11: '#7c5cff', PAZARAMA: '#2f9bd8', PTTAVM: '#1f3a8a' }
export function platformColor(code: string) { return platformColors[code.toUpperCase()] ?? '#8a8f98' }

// A number in Turkish with the given options: the same text as value.toLocaleString('tr-TR', options), from one formatter
// per set of options kept for the page's life (toLocaleString builds a new one on every call, which cost ~60 ms of a
// slowed phone's report render).
const formatters = new Map<string, Intl.NumberFormat>()
export function trNumber(value: number, options: Intl.NumberFormatOptions = {}) {
  const key = JSON.stringify(options)
  let formatter = formatters.get(key)
  if (!formatter) { formatter = new Intl.NumberFormat('tr-TR', options); formatters.set(key, formatter) }
  return formatter.format(value)
}

const number = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 })
const whole = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 })
const money = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 2, minimumFractionDigits: 2 })
const moneyShort = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', notation: 'compact', maximumFractionDigits: 1 })

export function formatNumber(value: number | null | undefined) { return value === null || value === undefined || Number.isNaN(value) ? '—' : number.format(value) }
export function formatCount(value: number | null | undefined) { return value === null || value === undefined || Number.isNaN(value) ? '—' : whole.format(value) }
export function formatTl(value: number | null | undefined) { return value === null || value === undefined || Number.isNaN(value) ? '—' : money.format(value) }
const moneyWhole = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 })
export function formatTlWhole(value: number) { return moneyWhole.format(value) }
// Zero is "₺0" like the short figures beside it on an axis ("₺50 B"), not "₺0,00".
export function formatTlShort(value: number) { return value === 0 ? moneyWhole.format(0) : Math.abs(value) >= 10_000 ? moneyShort.format(value) : money.format(value) }
export function formatPercent(rate: number | null | undefined, digits = 1) {
  return rate === null || rate === undefined || Number.isNaN(rate) ? '—' : `%${trNumber(rate * 100, { minimumFractionDigits: digits, maximumFractionDigits: digits })}`
}
export function formatHours(hours: number | null | undefined) {
  if (hours === null || hours === undefined) return '—'
  return hours >= 48 ? `${trNumber(hours / 24, { maximumFractionDigits: 1 })} gün` : `${trNumber(hours, { maximumFractionDigits: 1 })} sa`
}
export function formatDay(value: string) { const [year, month, day] = value.split('-'); return `${day}.${month}.${year}` }

// A money value that only means something when the bucket had orders with an amount (Shopify sends none).
// The one rule for a figure that is not known (no amount, no cost, no price, no comparison): "—" in the normal ink, never a
// word and never a colour; the reason, when it helps, is the card's sub-line or its explanation. A count that is really zero
// stays "0" (owner, 13 Nov 2026).
export { noValue }
export function moneyOrMissing(value: number, ordersWithAmount: number) { return ordersWithAmount > 0 ? formatTl(value) : noValue }

// The change against the previous period. A rate is compared by its difference in percentage points, written as such
// (13,7 % to 11,8 % is "−1,9 puan"), so it is not read as a relative change; a count or amount as a percentage.
export type Change = { text: string; direction: 'up' | 'down' | 'flat' }
export function change(current: number | null | undefined, previous: number | null | undefined, kind: 'value' | 'rate' = 'value'): Change | null {
  if (current === null || current === undefined || previous === null || previous === undefined) return null
  if (kind === 'rate') {
    const points = (current - previous) * 100
    if (Math.abs(points) < 0.05) return { text: '0,0 puan', direction: 'flat' }
    return { text: `${points > 0 ? '+' : '−'}${trNumber(Math.abs(points), { maximumFractionDigits: 1, minimumFractionDigits: 1 })} puan`, direction: points > 0 ? 'up' : 'down' }
  }
  // Something against nothing in the previous period is new ("▲ Yeni", no "+∞%"); nothing against nothing has no
  // change (the pill's "■ %0").
  if (previous === 0) return current > 0 ? { text: newChangeText, direction: 'up' } : null
  const percent = (current - previous) / Math.abs(previous) * 100
  if (Math.abs(percent) < 0.05) return { text: '%0', direction: 'flat' }
  return { text: `${percent > 0 ? '+' : '−'}%${trNumber(Math.abs(percent), { maximumFractionDigits: 1 })}`, direction: percent > 0 ? 'up' : 'down' }
}

// The report request: calendar days (yyyy-MM-dd), platforms and buckets; an empty choice is left out.
export function reportQuery(filters: { from: string; to: string; platforms: string[] | null; granularity: string; includeTest: boolean }) {
  const query = new URLSearchParams({ dateFrom: filters.from, dateTo: filters.to })
  if (filters.platforms?.length) query.set('platforms', filters.platforms.join(','))
  if (filters.granularity) query.set('granularity', filters.granularity)
  if (filters.includeTest) query.set('includeTest', 'true')
  return query.toString()
}

// A spreadsheet reads a cell starting with = + - @ (or a tab / carriage return) as a formula: such text gets a leading apostrophe.
function csvSafe(text: string) { return /^[=+\-@\t\r]/.test(text) ? `'${text}` : text }
export type CsvColumn<T> = { header: string; value: (row: T) => string | number | null | undefined }
export function toCsv<T>(rows: T[], columns: CsvColumn<T>[]) {
  const cell = (value: string | number | null | undefined) => {
    const text = value === null || value === undefined ? '' : typeof value === 'number' ? trNumber(value, { useGrouping: false, maximumFractionDigits: 4 }) : csvSafe(value)
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

export type ReportChannel = { id: string; platformCode: string; displayName: string; isTest: boolean }

// Stok raporu (ReportsContracts.cs, ReportInventoryView): the stock now (MAIN) with its cost (capital) and its value at the
// panel price (as the product editor shows it); the period only sets the sales the cover days are read from. Every amount is
// worked out over what is known; missingCost* and missingPriceUnits count the rest. Money is VAT included.
export type ReportStockSummary = {
  units: number; availableUnits: number; products: number; stockedProducts: number; variants: number; stockedVariants: number; emptyVariants: number
  lowStockProducts: number; capital: number; capitalExVat: number | null; missingCostVariants: number; missingCostUnits: number; value: number | null
  pricedUnits: number; missingPriceUnits: number; profit: number | null; margin: number | null; profitOnCost: number | null; soldUnits: number
  dailySales: number | null; coverDays: number | null; deadVariants: number; deadUnits: number; deadCapital: number
}
export type ReportStockGroup = { key: string; label: string; known: boolean; products: number; variants: number; units: number; capital: number; value: number | null; pricedUnits: number; soldUnits: number; coverDays: number | null }
export type ReportStockPlatform = { connectionId: string; platformCode: string; storeName: string; isTest: boolean; offers: number; activeOffers: number; listedUnits: number
  unlistedUnits: number; value: number | null; pricedUnits: number; averagePrice: number | null }
export type ReportStockItem = { key: string; productId: string; variantId: string | null; title: string; options: string | null; modelCode: string | null; category: string
  imageUrl: string | null; units: number; capital: number | null; value: number | null; soldUnits: number; recentSoldUnits: number; coverDays: number | null }
export type ReportInventory = {
  generatedAt: string; from: string; to: string; periodDays: number; deadDays: number; summary: ReportStockSummary
  platforms: ReportStockPlatform[]; categories: ReportStockGroup[]; colors: ReportStockGroup[]; sizes: ReportStockGroup[]; priceBands: ReportStockGroup[]
  cover: ReportStockGroup[]; topCapital: ReportStockItem[]; dead: ReportStockItem[]; channels: ReportChannel[]
}

// Stok's Tahmini Kâr: the real profit on cost of each of the last whole months, oldest first (GET /reports/profit-rate).
export type ReportProfitRate = ApiAnswer<'getReportsProfitRate'>

export function profitRateQuery(platforms: string[] | null) {
  return platforms?.length ? new URLSearchParams({ platforms: platforms.join(',') }).toString() : ''
}

export function stockQuery(filters: { from: string; to: string; platforms: string[] | null }) {
  const query = new URLSearchParams({ dateFrom: filters.from, dateTo: filters.to })
  if (filters.platforms?.length) query.set('platforms', filters.platforms.join(','))
  return query.toString()
}
