import { useLayoutEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { backgroundPollHeaders, hubApi } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { dashboardOperationalLinks } from '../../shared/dashboard-operational-links'
import { InvoiceGlyph } from '../invoicing/invoice-state-glyph'
import { RrIcon, RrLogo } from '../marketplace/returns-ui'
import './dashboard-sade.css'
import { formatIstanbul, istanbulDateKey } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'
import { channelTotals, comparisonText, daySeries, hourSeries, istanbulHour, otherCurrencies, periodDays, primaryCurrency, revenueReport, type DashboardChannel, type DayPoint, type Performance, type Range } from './dashboard-performance'

// The overview page, as drawn in docs/tasarim-onerileri/genel-bakis-yeni.html.

type DashboardMetrics = { pendingOrders: number; lateOrders: number; pendingReturns: number; pendingByConnection?: Record<string, number>; lateByConnection?: Record<string, number>; pendingReturnsByConnection?: Record<string, number>; deadJobCount?: number; manualReviewJobCount?: number; recentJobCount?: number }
type DashboardBootstrap = { metrics: DashboardMetrics; channels?: DashboardChannel[] }
type ProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number }
type MatrixCell = { state: 'ok' | 'warn' | 'bad' | 'off'; lastSuccessAt: string | null; lastProblemAt: string | null }
type MatrixRow = { connectionId: string; platformCode: string; displayName: string; externalStoreId: string; status: string; cells: Record<string, MatrixCell>; issue: string | null }

const PERIODS: Array<[Range, string]> = [['today', 'Bugün'], ['7', 'Son 7 Gün'], ['30', 'Son 30 Gün'], ['90', 'Son 90 Gün'], ['custom', 'Özel Tarih']]
const periodLabel = (range: Range) => PERIODS.find(([value]) => value === range)?.[1] ?? ''
const PLATFORMS: Array<[string, string, string]> = [['TRENDYOL', 'Trendyol', '#f27a1a'], ['HEPSIBURADA', 'Hepsiburada', '#f5b301'], ['SHOPIFY', 'Shopify', '#5e8e3e'], ['N11', 'N11', '#7c5cff'], ['PAZARAMA', 'Pazarama', '#2f9bd8']]
const platformColor = (code: string, index: number) => PLATFORMS.find(([value]) => value === code.toUpperCase())?.[2] ?? ['#9b7aff', '#59d6bd', '#79a4ff', '#f0b15a'][index % 4]
const DATA_KEYS: Array<[string, string]> = [['orders', 'Sipariş'], ['products', 'Ürün'], ['stock', 'Stok'], ['price', 'Fiyat'], ['returns', 'İade'], ['invoices', 'Fatura']]
const CELL_STATE = { ok: 'Güncel', warn: 'Gecikiyor', bad: 'Hata var', off: 'Son 7 günde işlem yok' }
// Refreshed while the page is on screen only; the polls say they are automatic so they do not keep an idle session alive.
const refresh = { refetchInterval: 60_000, refetchIntervalInBackground: false, refetchOnWindowFocus: true, staleTime: 30_000 } as const
const poll = { headers: backgroundPollHeaders }

const icon = (body: ReactNode, size = 18) => <svg className="rr-ico" width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{body}</svg>
const ICONS = {
  cart: icon(<><path d="M1.5 2.5h2l1.6 7.4a1 1 0 0 0 1 .8h5.6a1 1 0 0 0 1-.8L14.5 5.5H4.4" /><circle cx="6.5" cy="13.3" r=".9" /><circle cx="12" cy="13.3" r=".9" /></>),
  late: icon(<><path d="M13.6 9.3A5.8 5.8 0 1 1 9.3 2.4" /><path d="M8 5v3.2l2 1.3" /><path d="M12.6 2.6v2.6M12.6 7.1v.1" /></>),
  ret: icon(<><path d="M5.5 4 2.5 7l3 3" /><path d="M2.5 7h7a4 4 0 0 1 0 8H7" /></>),
  invoice: icon(<><path d="M4 2h5l3 3v9H4z" /><path d="M9 2v3h3M6 8.5h4M6 11h3" /></>),
  due: icon(<><rect x="2.5" y="3.5" width="11" height="10" rx="1.5" /><path d="M2.5 6.5h11M5.5 2v3M10.5 2v3M8 9v2" /></>),
  lowst: icon(<><path d="M2.5 5 8 2.2 13.5 5v6L8 13.8 2.5 11z" /><path d="M2.5 5 8 7.8 13.5 5M8 7.8v6" /></>),
  up: icon(<path d="M4 10l4-4 4 4" />, 12),
  down: icon(<path d="M4 6l4 4 4-4" />, 12),
}

const money = (amount: number, currency = 'TRY') => formatMoney(amount, currency || 'TRY', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
const count = (value: number) => value.toLocaleString('tr-TR', { maximumFractionDigits: 0 })
function trend(current: number, previous: number): [string, boolean] {
  if (previous <= 0) return [current > 0 ? '%100' : '%0', true]
  const delta = ((current - previous) / previous) * 100
  return [`${delta < 0 ? '-' : ''}%${Math.abs(delta).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`, delta >= 0]
}
function age(timestamp: string | null) {
  if (!timestamp) return null
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(timestamp).getTime()) / 60_000))
  if (minutes < 1) return 'Az önce'
  if (minutes < 60) return `${minutes} dk önce`
  const hours = Math.floor(minutes / 60)
  return hours < 48 ? `${hours} sa önce` : `${Math.floor(hours / 24)} gün önce`
}
// What a matrix cell says: its own state first, the time of the last success after it.
export function matrixCellText(cell: MatrixCell) {
  const when = age(cell.lastSuccessAt)
  if (cell.state === 'off') return 'İşlem Yok'
  if (cell.state === 'bad') return 'Hata Var'
  if (cell.state === 'warn') return when ? `Gecikiyor · ${when}` : 'Gecikiyor'
  return when ?? 'Güncel'
}
// Series days are Istanbul calendar days (YYYY-MM-DD) and read as such; noon keeps the name of the day in any clock.
const dayLong = (day: string) => formatIstanbul(`${day}T12:00:00Z`, { day: 'numeric', month: 'long', year: 'numeric', weekday: 'long' })
const dayShort = (day: string) => { const dayOfMonth = Number(day.slice(8, 10)); return dayOfMonth === 1 ? formatIstanbul(`${day}T12:00:00Z`, { day: 'numeric', month: 'short' }).replace(/\.$/, '') : String(dayOfMonth) }
function axisLabel(value: number) {
  if (!value) return '₺0'
  return value >= 1000 ? `₺${(value / 1000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} B` : `₺${count(value)}`
}
function niceStep(max: number) {
  const rough = Math.max(1, max) / 5
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  return Math.ceil(rough / magnitude) * magnitude
}

// One breakdown per store, shown when the card is hovered.
function Breakdown({ rows }: { rows: Array<{ key: string; code: string; name: string; value: string }> }) {
  return rows.length ? <div className="rr-tip rr-bd" role="tooltip">{rows.map(row => <div className="rr-bd-r" key={row.key}><RrLogo code={row.code} /><span>{row.name}</span><b className="rr-num">{row.value}</b></div>)}</div> : null
}

// Daily revenue: area and line for this period, dashed line for the previous one. The day tooltip follows the
// pointer, above it when the page has room there, otherwise below.
// An hourly chart (a one-day period) compares each hour with the same hour of the previous day.
function RevenueChart({ points, previous, currency, value = point => point.amount, axis = axisLabel, hourly = false }: { points: DayPoint[]; previous: DayPoint[]; currency: string; value?: (point: DayPoint) => number; axis?: (value: number) => string; hourly?: boolean }) {
  const [hover, setHover] = useState<{ index: number; x: number; y: number } | null>(null)
  const chartRef = useRef<HTMLDivElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  const W = 760, H = 260, L = 64, R = 12, T = 12, B = 30
  const max = niceStep(Math.max(1, ...points.map(value), ...previous.map(value))) * 5
  const x = (index: number, length = points.length) => L + (length > 1 ? index / (length - 1) : 0) * (W - L - R)
  const y = (value: number) => T + (1 - value / max) * (H - T - B)
  const line = (series: DayPoint[]) => series.map((point, index) => `${index ? 'L' : 'M'}${x(index, series.length).toFixed(1)} ${y(value(point)).toFixed(1)}`).join(' ')
  const labelStep = Math.max(1, Math.round(points.length / 6))
  useLayoutEffect(() => {
    const tip = tipRef.current, chart = chartRef.current
    if (!tip || !chart || !hover) return
    const box = chart.getBoundingClientRect(), width = tip.offsetWidth, height = tip.offsetHeight, gap = 14
    const left = Math.max(0, Math.min(box.width - width, hover.x - box.left - width / 2))
    const above = hover.y - height - gap >= 8
    tip.style.left = `${left}px`
    tip.style.top = `${above ? hover.y - box.top - height - gap : hover.y - box.top + gap + 4}px`
    tip.style.visibility = ''
  }, [hover])
  const move = (event: ReactMouseEvent<HTMLDivElement>) => {
    if ((event.target as Element).closest('.rr-ctip')) return
    const box = event.currentTarget.getBoundingClientRect()
    const px = (event.clientX - box.left) / box.width * W
    const index = Math.max(0, Math.min(points.length - 1, Math.round((px - L) / (W - L - R) * (points.length - 1))))
    setHover({ index, x: event.clientX, y: event.clientY })
  }
  const point = hover ? points[hover.index] : null
  const before = hover ? (hourly ? previous[hover.index] ?? null : points[hover.index - 1] ?? previous[previous.length - 1]) : null
  return <div ref={chartRef} className="rr-chart" onMouseMove={move} onMouseLeave={() => setHover(null)}>
    <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={hourly ? 'Saatlik ciro grafiği' : 'Günlük ciro grafiği'}>
      <g className="rr-grid">{Array.from({ length: 6 }, (_, index) => { const value = max / 5 * index; return <g key={index}><line x1={L} x2={W - R} y1={y(value)} y2={y(value)} /><text x={L - 10} y={y(value) + 4} textAnchor="end">{axis(value)}</text></g> })}</g>
      <g className="rr-xax">{points.map((item, index) => index % labelStep === 0 || (!item.label && Number(item.day.slice(8, 10)) === 1) ? <text key={item.day} x={x(index)} y={H - 8} textAnchor="middle">{item.label ?? dayShort(item.day)}</text> : null)}</g>
      <path className="rr-area" d={`${line(points)} L${x(points.length - 1)} ${y(0)} L${x(0)} ${y(0)} Z`} />
      {previous.length > 0 && <path className="rr-prev" d={line(previous)} />}
      <path className="rr-cur" d={line(points)} />
      {/* A one-day period has no line to draw: its value (and the previous day's) shows as a point. */}
      {points.length === 1 && <circle className="rr-hp" cx={x(0)} cy={y(value(points[0]))} r="4.5" />}
      {previous.length === 1 && points.length === 1 && <circle className="rr-hp rr-hp-prev" cx={x(0)} cy={y(value(previous[0]))} r="3.5" />}
      {hover && point && <><line className="rr-hl" x1={x(hover.index)} x2={x(hover.index)} y1={T} y2={H - B} /><circle className="rr-hp" cx={x(hover.index)} cy={y(value(point))} r="4.5" /></>}
    </svg>
    {hover && point && <div ref={tipRef} className="rr-ctip" style={{ visibility: 'hidden' }}><b>{point.label ? `${dayLong(point.day.slice(0, 10))} · ${point.label}–${point.label.slice(0, 2)}:59` : dayLong(point.day)}</b><table><thead><tr><th /><th>{hourly ? 'Bu Saat' : 'Seçilen Gün'}</th><th>{hourly ? 'Önceki Gün Aynı Saat' : 'Önceki Gün'}</th></tr></thead><tbody>
      <tr><td>Sipariş</td><td className="rr-num">{count(point.orderCount)}</td><td className="rr-num">{before ? count(before.orderCount) : '—'}</td></tr>
      <tr><td>Ürün (Adet)</td><td className="rr-num">{count(point.productQuantity)}</td><td className="rr-num">{before ? count(before.productQuantity) : '—'}</td></tr>
      <tr><td>Kargo (Paket)</td><td className="rr-num">{count(point.packageCount)}</td><td className="rr-num">{before ? count(before.packageCount) : '—'}</td></tr>
      <tr><td>Ciro</td><td className="rr-num"><b>{money(point.amount, currency)}</b></td><td className="rr-num">{before ? money(before.amount, currency) : '—'}</td></tr>
      {(point.returnedOrderCount > 0 || (before?.returnedOrderCount ?? 0) > 0) && <tr><td>İade</td><td className="rr-num">{money(point.returnedAmount, currency)}</td><td className="rr-num">{before ? money(before.returnedAmount, currency) : '—'}</td></tr>}
    </tbody></table></div>}
  </div>
}

// Option A of genel-bakis-platform-sekmeleri.html: one metric of the performance card. The cells that can be drawn
// pick the chart's metric; the bar shows where the latest day sits between the weakest and the strongest one.
function KpiCell({ label, value, delta, note, avgLabel = 'Günlük ortalama', avg, min, max, position, on, onPick, loading }: { loading?: boolean; label: string; value: string; delta: [string, boolean] | null; note?: string; avgLabel?: string; avg: string; min: string; max: string; position: number; on?: boolean; onPick?: () => void }) {
  return <div className={`rr-kc${on ? ' rr-on' : ''}${onPick ? ' rr-pick' : ''}${loading ? ' rr-kc-wait' : ''}`} role={onPick ? 'tab' : undefined} aria-selected={onPick ? Boolean(on) : undefined} tabIndex={onPick ? 0 : undefined} onClick={onPick} onKeyDown={event => { if (onPick && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); onPick() } }}>
    <small>{label}</small><strong className="rr-num">{value}</strong>
    {delta ? <span className={`rr-trend ${delta[1] ? 'rr-up' : 'rr-down'}`}>{delta[1] ? ICONS.up : ICONS.down}<span className="rr-trend-v">{delta[0]}</span><em>önceki döneme göre</em></span> : <span className="rr-trend"><em>{note}</em></span>}
    <span className="rr-avg">{avgLabel} <b className="rr-num">{avg}</b></span>
    <div className="rr-krange" role="img" aria-label={`${min} – ${max}`}><span className="rr-kbar"><i style={{ left: `${Math.max(0, Math.min(1, position)) * 100}%` }} /></span><span className="rr-kends"><span className="rr-num">{min}</span><span className="rr-num">{max}</span></span></div>
  </div>
}

function dailyStats(values: number[], format: (value: number) => string, average: number) {
  if (!values.length) return { avg: format(0), min: format(0), max: format(0), position: 0.5 }
  const low = Math.min(...values), high = Math.max(...values), today = values[values.length - 1]
  return { avg: format(average), min: format(low), max: format(high), position: high > low ? (today - low) / (high - low) : 0.5 }
}

const sumOf = (points: DayPoint[], pick: (point: DayPoint) => number) => points.reduce((total, point) => total + pick(point), 0)

export function DashboardPage() {
  // Periods are Istanbul calendar days, whatever the browser's clock says.
  const today = istanbulDateKey()
  const [range, setRange] = useState<Range>('30')
  const [custom, setCustom] = useState({ from: today, to: today })
  // The store picked in the channel list filters the whole performance card; the metric picks what the chart draws.
  const [channel, setChannel] = useState('ALL')
  const [metric, setMetric] = useState<0 | 1 | 2>(0)
  const [noticeClosed, setNoticeClosed] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const bootstrap = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap', poll), ...refresh })
  const invoiceSummary = useQuery({ queryKey: ['invoice-workspace-summary'], queryFn: () => hubApi<{ dueSoonCount: number; uninvoicedCount?: number }>('/invoice-workspace/summary', poll), ...refresh })
  const productSummary = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<ProductSummary>('/products/summary', poll), ...refresh })
  const matrix = useQuery({ queryKey: ['dashboard', 'integration-matrix'], queryFn: () => hubApi<MatrixRow[]>('/dashboard/integration-matrix', poll), ...refresh, refetchInterval: 300_000, staleTime: 120_000 })
  // A custom range picked only halfway asks for nothing until its end is picked too.
  const days = periodDays(range, custom, today)
  const performance = useQuery({ placeholderData: keepPreviousData, queryKey: ['dashboard', 'performance', days?.from, days?.to], queryFn: () => hubApi<Performance>(`/dashboard/performance?from=${days!.from}&to=${days!.to}`, poll), enabled: days !== null, ...refresh })

  const metrics = bootstrap.data?.metrics
  // The report also names stores that are switched off or wait for new credentials; the live list wins where both have one.
  const channels = new Map([...(performance.data?.channels ?? []), ...(bootstrap.data?.channels ?? [])].map(item => [item.connectionId, item]))
  const data = performance.data
  const loading = performance.isLoading
  const currency = primaryCurrency(data?.current ?? [])
  // A one-day period is drawn and measured hour by hour, up to the hour the day has reached.
  const hourly = Boolean(data && data.from === data.to && data.currentHours)
  const lastHour = data && data.partialDay ? istanbulHour(data.currentEnd) : 23
  const series = data ? (hourly ? hourSeries(data.currentHours ?? [], data.from, channel, currency, lastHour) : daySeries(data.current, data.from, data.to, channel, currency)) : []
  const previousSeries = data ? (hourly ? hourSeries(data.previousHours ?? [], data.previousFrom, channel, currency, lastHour) : daySeries(data.previous, data.previousFrom, data.previousTo, channel, currency)) : []
  const total = sumOf(series, point => point.amount)
  const orders = sumOf(series, point => point.orderCount)
  const previousTotal = sumOf(previousSeries, point => point.amount)
  const previousOrders = sumOf(previousSeries, point => point.orderCount)
  const returnedAmount = sumOf(series, point => point.returnedAmount)
  const returnedOrders = sumOf(series, point => point.returnedOrderCount)
  const foreign = data ? otherCurrencies(data.current, channel, currency) : []
  const basket = orders ? total / orders : 0
  const previousBasket = previousOrders ? previousTotal / previousOrders : 0
  const dayCount = Math.max(1, series.length)
  const storeTotals = data ? channelTotals(data, currency) : []
  const channelAmount = storeTotals.reduce((sum, item) => sum + item.amount, 0)
  const channelOrders = storeTotals.reduce((sum, item) => sum + item.orders, 0)
  const channelShares = storeTotals.map((item, index) => ({ ...item, color: platformColor(item.platformCode, index), share: channelAmount > 0 ? item.amount / channelAmount * 100 : channelOrders > 0 ? item.orders / channelOrders * 100 : 0 }))
  const selected = channel === 'ALL' ? null : channels.get(channel) ?? null
  const selectedName = selected?.displayName ?? storeTotals.find(item => item.connectionId === channel)?.name ?? ''
  const periodText = range === 'custom' ? 'Seçilen dönem' : periodLabel(range)
  const chartValues: Array<[(point: DayPoint) => number, (value: number) => string, string]> = [
    [point => point.amount, axisLabel, hourly ? 'Saatlik Ciro' : 'Günlük Ciro'],
    [point => point.orderCount, value => count(value), hourly ? 'Saatlik Sipariş' : 'Günlük Sipariş'],
    [point => point.orderCount ? point.amount / point.orderCount : 0, axisLabel, hourly ? 'Saatlik Ortalama Sepet' : 'Günlük Ortalama Sepet'],
  ]
  // Only marketplaces: invoicing providers are connections too, but they carry no orders, stock or prices.
  const rows = (matrix.data ?? []).filter(row => PLATFORMS.some(([code]) => code === row.platformCode.toUpperCase()))
  const attention = rows.filter(row => row.issue || Object.values(row.cells).some(cell => cell.state === 'bad' || cell.state === 'warn')).length
  const errors = [bootstrap.error, productSummary.error, performance.error, matrix.error].filter(Boolean)
  const loadingValue = (busy: boolean, value: number | undefined) => busy ? '—' : count(value ?? 0)
  const revenueStats = dailyStats(series.map(point => point.amount), value => money(value, currency), total / dayCount)
  const orderStats = dailyStats(series.map(point => point.orderCount), count, orders / dayCount)
  const basketStats = dailyStats(series.filter(point => point.orderCount > 0).map(point => point.amount / point.orderCount), value => money(value, currency), basket)
  const products = productSummary.data
  const breakdown = (counts: Record<string, number> | undefined) => Object.entries(counts ?? {}).filter(([, value]) => value > 0).sort((left, right) => right[1] - left[1])
    .map(([connectionId, value]) => { const store = channels.get(connectionId); return { key: connectionId, code: store?.platformCode ?? '', name: store?.displayName ?? 'Kaldırılmış mağaza', value: `${count(value)} adet` } })
  const ops: Array<[keyof typeof ICONS, string, number | null, 'warn' | 'bad' | '', string, Record<string, number> | undefined]> = [
    ['cart', 'Bekleyen Siparişler', bootstrap.isLoading ? null : metrics?.pendingOrders ?? 0, 'warn', dashboardOperationalLinks.pendingOrders, metrics?.pendingByConnection],
    ['late', 'Geciken Siparişler', bootstrap.isLoading ? null : metrics?.lateOrders ?? 0, 'bad', dashboardOperationalLinks.lateOrders, metrics?.lateByConnection],
    ['ret', 'Bekleyen İadeler', bootstrap.isLoading ? null : metrics?.pendingReturns ?? 0, 'warn', dashboardOperationalLinks.pendingReturns, metrics?.pendingReturnsByConnection],
    ['invoice', 'Bekleyen Faturalar', invoiceSummary.isLoading ? null : invoiceSummary.data?.uninvoicedCount ?? 0, '', dashboardOperationalLinks.pendingInvoices, undefined],
    ['due', 'Yaklaşan Faturalar', invoiceSummary.isLoading ? null : invoiceSummary.data?.dueSoonCount ?? 0, '', dashboardOperationalLinks.dueInvoices, undefined],
    // The same count as the product list's low-stock filter it opens.
    ['lowst', 'Düşük Stok', productSummary.isLoading ? null : products?.lowStockCount ?? 0, 'warn', dashboardOperationalLinks.lowStock, undefined],
  ]
  // The file is made from the data on screen only once it belongs to the chosen period and store.
  const reportReady = Boolean(data) && days !== null && !performance.isPlaceholderData && !performance.isFetching && data!.from === days.from && data!.to === days.to
  function downloadReport() {
    if (!reportReady || !data) return
    const report = revenueReport(data, channel, selectedName)
    const url = URL.createObjectURL(new Blob([report.content], { type: 'text/csv;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url
    link.download = report.name
    link.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 0)
    setToast(`${report.name} indirildi (${report.columns.join(', ')}).`)
  }
  const storeLabel = (row: MatrixRow) => /^\d+$/.test(row.externalStoreId) ? `Mağaza ${row.externalStoreId}` : row.externalStoreId.length > 24 ? `Mağaza ${row.externalStoreId.slice(0, 8)}…` : row.externalStoreId

  return <section className="content f3 rr rr-dashboard" aria-label="Genel Bakış">
    <div className="rr-ph"><div><div className="rr-crumb">Operasyon Merkezi</div><h1>Genel Bakış</h1><p>Siparişler, iadeler, faturalar ve stok için bugün ilgilenmeniz gerekenler ve satış performansınız.</p></div><div className="rr-acts">
      <button type="button" className="rr-btn" onClick={downloadReport} disabled={!reportReady} title={reportReady ? undefined : 'Rapor verisi hazırlanıyor'}><RrIcon name="export" />Raporu İndir</button>
    </div></div>
    {errors.length > 0 && !noticeClosed && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span><b>Bazı rapor verileri alınamadı.</b> Görünen değerler kısmi olabilir; birkaç dakika içinde tekrar denenir.</span><button type="button" className="rr-icon-btn" onClick={() => setNoticeClosed(true)} aria-label="Uyarıyı kapat"><RrIcon name="close" /></button></div>}
    <section className="rr-dsect"><header><h2>Operasyon Metrikleri</h2><p>Bugün ilgilenmeniz gereken işler</p></header><div className="rr-opgrid">{ops.map(([kind, label, value, tone, to, byConnection]) => <Link key={kind} className={`rr-opcard${tone && value ? ` rr-${tone}` : ''}`} to={to}><span className="rr-op-ic">{ICONS[kind]}</span><span className="rr-op-t"><strong className="rr-num">{value == null ? '—' : count(value)}</strong><small>{label}</small></span><RrIcon name="arrowR" /><Breakdown rows={breakdown(byConnection)} /></Link>)}</div></section>
    <section className="rr-panel rr-pf" style={selected ? { ['--pf-accent' as string]: platformColor(selected.platformCode, 0) } : undefined}>
      <div className="rr-pf-h"><div><h2>Performans</h2><p>{periodText} · {channel === 'ALL' ? 'Tüm Platformlar' : selectedName} · {comparisonText(range, data)}</p></div><div className="rr-pf-tools">
        {range === 'custom' && <DateRangePicker className="rr-date" from={custom.from} to={custom.to} onChange={value => setCustom({ from: value.from, to: value.to })} label="Rapor Tarihi" placeholder="Başlangıç – Bitiş" chevron={<RrIcon name="chev" />} />}
        <div className="rr-seg" role="radiogroup" aria-label="Dönem">{PERIODS.map(([value, label]) => <button key={value} type="button" role="radio" aria-checked={range === value} className={range === value ? 'rr-on' : undefined} onClick={() => setRange(value)}>{value === 'custom' ? 'Özel' : label.replace('Son ', '')}</button>)}</div>
      </div></div>
      {days === null && <p className="rr-pf-note" role="status">Raporu görmek için bitiş tarihini de seçin.</p>}
      <div className="rr-kgrid" role="tablist" aria-label="Grafikte gösterilecek metrik">
        <KpiCell loading={loading} label="Toplam Gelir" avgLabel={hourly ? 'Saatlik ortalama' : 'Günlük ortalama'} value={loading ? '—' : money(total, currency)} delta={trend(total, previousTotal)} {...revenueStats} on={metric === 0} onPick={() => setMetric(0)} />
        <KpiCell loading={loading} label="Toplam Sipariş" avgLabel={hourly ? 'Saatlik ortalama' : 'Günlük ortalama'} value={loadingValue(loading, orders)} delta={trend(orders, previousOrders)} {...orderStats} on={metric === 1} onPick={() => setMetric(1)} />
        <KpiCell loading={loading} label="Ortalama Sepet" avgLabel={hourly ? 'Saatlik ortalama' : 'Günlük ortalama'} value={loading ? '—' : money(basket, currency)} delta={trend(basket, previousBasket)} {...basketStats} on={metric === 2} onPick={() => setMetric(2)} />
        <KpiCell loading={productSummary.isLoading} label="Aktif Ürün" value={loadingValue(productSummary.isLoading, products?.activeCount)} delta={null} note={channel === 'ALL' ? 'Katalogdaki güncel durum' : 'Tüm platformlar'} avgLabel="Stoksuz ürün" avg={count(products?.outOfStockCount ?? 0)} min={`Düşük stok ${count(products?.lowStockCount ?? 0)}`} max={`Toplam ${count(products?.totalCount ?? 0)}`} position={products?.totalCount ? products.activeCount / products.totalCount : 0} />
      </div>
      {!loading && (returnedOrders > 0 || foreign.length > 0) && <p className="rr-pf-note">
        {returnedOrders > 0 && <span>İade: <b className="rr-num">{money(returnedAmount, currency)}</b> · {count(returnedOrders)} sipariş (ciroya dahil değil)</span>}
        {foreign.length > 0 && <span>Diğer para birimleri: {foreign.map(item => <b key={item.currency} className="rr-num">{money(item.amount, item.currency)} ({count(item.orders)} sipariş) </b>)}</span>}
      </p>}
      <div className="rr-pf-body">
        <div className="rr-pf-chart"><b className="rr-pf-label">{chartValues[metric][2]}{channel === 'ALL' ? '' : ` · ${selectedName}`}</b>
          {loading ? <div className="rr-chart rr-chart-wait" aria-hidden="true" style={{ aspectRatio: '760 / 260' }} /> : series.some(point => chartValues[metric][0](point) > 0) ? <RevenueChart key={metric} hourly={hourly} points={series} previous={previousSeries} currency={currency} value={chartValues[metric][0]} axis={chartValues[metric][1]} /> : <div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Gelir Verisi Yok</b><span className="rr-lead">Bu dönem için gelir verisi bulunmuyor.</span></div>}
          <div className="rr-legend"><span><i className="rr-lg-cur" />Bu Dönem</span><span><i className="rr-lg-prev" />Önceki Dönem</span></div></div>
        <div className="rr-pf-chan">
          <div className="rr-pf-chan-h"><b>Kanallara Göre Ciro</b>{selected && <span className="rr-ftag"><RrLogo code={selected.platformCode} />{selectedName}<button type="button" onClick={() => setChannel('ALL')} aria-label="Mağaza süzgecini kaldır"><RrIcon name="close" /></button></span>}</div>
          {channelShares.length ? <>
            <span className="rr-stack" aria-hidden="true">{channelShares.map(item => <i key={item.connectionId} style={{ width: `${item.share}%`, background: item.color }} />)}</span>
            <div className={`rr-crows${performance.isFetching ? ' rr-crows-wait' : ''}`}>{channelShares.map(item => { const on = channel === item.connectionId; const delta = trend(item.amount, item.previousAmount); return <button key={item.connectionId} type="button" className={`rr-crow${on ? ' rr-on' : channel !== 'ALL' ? ' rr-dim' : ''}`} aria-pressed={on} onClick={() => setChannel(on ? 'ALL' : item.connectionId)}><RrLogo code={item.platformCode} /><span className="rr-ct"><span><b>{item.name}</b><small className="rr-num">{money(item.amount, currency)}</small></span><span className="rr-cbar"><i style={{ width: `${item.share}%`, background: item.color }} /></span></span><span className="rr-cv"><b className="rr-num">%{item.share.toLocaleString('tr-TR', { maximumFractionDigits: 0 })}</b><small className={`rr-num ${delta[1] ? 'rr-up' : 'rr-down'}`}>{delta[1] ? '+' : ''}{delta[0]}</small></span></button> })}</div>
          </> : <div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Kanal Dağılımı Yok</b><span className="rr-lead">Bu dönem için kanal dağılımı bulunmuyor.</span></div>}
          <Link className="rr-btn rr-link" to="/integrations">Kanalları Yönet <RrIcon name="arrowR" /></Link>
        </div>
      </div>
    </section>
    <section className="rr-panel"><div className="rr-panel-h"><div><h2>Entegrasyon Durumu</h2><p>Pazaryerleriyle bağlantının son 7 günü</p></div><span className={`rr-hpill${attention ? ' rr-attn' : ''}`}><InvoiceGlyph kind={attention ? 'waiting' : 'done'} />{attention ? `${attention} konu dikkatinizi bekliyor` : 'Her şey yolunda'}</span></div>
      <div className="rr-mxwrap"><table className="rr-mxt"><thead><tr><th>Pazar Yeri</th>{DATA_KEYS.map(([, label]) => <th key={label}>{label}</th>)}<th /></tr></thead><tbody>
        {!rows.length && <tr><td colSpan={DATA_KEYS.length + 2}><span className="rr-lead">{matrix.isLoading ? 'Bağlantılar yükleniyor…' : 'Aktif pazar yeri bağlantısı bulunmuyor.'}</span></td></tr>}
        {rows.map(row => <tr key={row.connectionId} className={row.issue ? 'rr-attn' : undefined}><td><span className="rr-mx-nm"><RrLogo code={row.platformCode} /><span><b>{row.displayName}</b><small title={row.issue ?? undefined}>{row.issue ?? storeLabel(row)}</small></span></span></td>
          {DATA_KEYS.map(([key, label]) => { const cell = row.cells[key] ?? { state: 'off', lastSuccessAt: null, lastProblemAt: null }; return <td key={key} data-l={label}><span className={`rr-mx rr-${cell.state}`} title={`${label} · ${CELL_STATE[cell.state]}`}><i />{matrixCellText(cell)}</span></td> })}
          <td className="rr-mx-act">{row.issue && <Link className="rr-btn rr-link" to="/jobs?durum=hata">İncele</Link>}</td></tr>)}
      </tbody></table></div>
      <div className="rr-mx-foot"><span>Son 24 saatte <b className="rr-num">{count(metrics?.recentJobCount ?? 0)}</b> istek · <b className="rr-num">{count(metrics?.deadJobCount ?? 0)}</b> tamamlanamayan iş · <b className="rr-num">{count(metrics?.manualReviewJobCount ?? 0)}</b> karar bekleyen iş</span><span className="rr-legend"><span><i className="rr-mx-l rr-ok" />Güncel</span><span><i className="rr-mx-l rr-warn" />Gecikiyor</span><span><i className="rr-mx-l rr-bad" />Hata Var</span><span><i className="rr-mx-l rr-off" />İşlem Yok</span></span></div>
    </section>
    {toast && <NoticeToast kind="success" title="Rapor İndirildi" message={toast} onClose={() => setToast(null)} durationMs={6000} />}
  </section>
}
