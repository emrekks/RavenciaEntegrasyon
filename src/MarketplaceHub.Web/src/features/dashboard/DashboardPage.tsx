import { useLayoutEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { dashboardOperationalLinks } from '../../shared/dashboard-operational-links'
import { InvoiceGlyph } from '../invoicing/invoice-state-glyph'
import { RrAnchor, RrIcon, RrLogo } from '../marketplace/returns-ui'
import './dashboard-sade.css'

// The overview page, as drawn in docs/tasarim-onerileri/genel-bakis-yeni.html.

type DashboardMetrics = { pendingOrders: number; lateOrders: number; pendingReturns: number; uninvoicedInvoices: number; lowStockProducts: number; pendingByPlatform: Record<string, number>; lateByPlatform?: Record<string, number>; pendingReturnsByPlatform?: Record<string, number>; deadJobCount?: number; manualReviewJobCount?: number; recentJobCount?: number }
type DashboardBootstrap = { metrics: DashboardMetrics; platforms: { name: string; status: string }[] }
type RevenuePoint = { day: string; amount: number; orderCount: number; productQuantity: number; shipmentCount: number; currency: string }
type ProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number }
type MatrixCell = { state: 'ok' | 'warn' | 'bad' | 'off'; lastSuccessAt: string | null; lastProblemAt: string | null }
type MatrixRow = { connectionId: string; platformCode: string; displayName: string; externalStoreId: string; status: string; cells: Record<string, MatrixCell>; issue: string | null }
type Range = '7' | '30' | '90' | 'custom'

const PERIODS: Array<[Range, string]> = [['7', 'Son 7 Gün'], ['30', 'Son 30 Gün'], ['90', 'Son 90 Gün'], ['custom', 'Özel Tarih']]
const periodLabel = (range: Range) => PERIODS.find(([value]) => value === range)?.[1] ?? ''
const PLATFORMS: Array<[string, string, string]> = [['TRENDYOL', 'Trendyol', '#f27a1a'], ['HEPSIBURADA', 'Hepsiburada', '#ff6000'], ['SHOPIFY', 'Shopify', '#5e8e3e'], ['N11', 'N11', '#7c5cff'], ['PAZARAMA', 'Pazarama', '#2f9bd8']]
const platformCode = (name: string) => PLATFORMS.find(([code]) => name.toLocaleUpperCase('tr-TR').replace(/İ/g, 'I').includes(code))?.[0] ?? name.toUpperCase()
const platformName = (name: string) => PLATFORMS.find(([code]) => code === platformCode(name))?.[1] ?? name
const platformColor = (name: string, index: number) => PLATFORMS.find(([code]) => code === platformCode(name))?.[2] ?? ['#9b7aff', '#59d6bd', '#79a4ff', '#f0b15a'][index % 4]
const DATA_KEYS: Array<[string, string]> = [['orders', 'Sipariş'], ['products', 'Ürün'], ['stock', 'Stok'], ['price', 'Fiyat'], ['returns', 'İade'], ['invoices', 'Fatura']]
const CELL_STATE = { ok: 'Güncel', warn: 'Gecikiyor', bad: 'Durdu', off: 'Son 7 günde işlem yok' }
const refresh = { refetchInterval: 60_000, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 30_000 } as const

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

function dateKey(value: Date) {
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`
}
function rangeDates(range: Range, from: string, to: string, now: Date) {
  if (range === 'custom') {
    const start = new Date(`${from}T00:00:00`), end = new Date(`${to}T23:59:59.999`)
    return start <= end ? { start, end } : { start: end, end: start }
  }
  const start = new Date(now)
  start.setHours(0, 0, 0, 0)
  start.setDate(start.getDate() - (Number(range) - 1))
  return { start, end: now }
}
function previousRange(start: Date, end: Date) {
  const day = 86_400_000
  const previousEnd = new Date(start.getTime() - day)
  return { start: new Date(previousEnd.getTime() - Math.max(day, end.getTime() - start.getTime())), end: previousEnd }
}
const revenuePath = (from: Date, to: Date, platform = 'ALL') => `/dashboard/revenue-series?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&platform=${encodeURIComponent(platform)}`
const money = (amount: number, currency = 'TRY') => new Intl.NumberFormat('tr-TR', { style: 'currency', currency: currency || 'TRY', minimumFractionDigits: 0, maximumFractionDigits: 0 }).format(amount)
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
const dayLong = (day: string) => new Date(day).toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric', weekday: 'long' })
const dayShort = (day: string) => { const date = new Date(day); return date.getDate() === 1 ? date.toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' }).replace(/\.$/, '') : String(date.getDate()) }
function axisLabel(value: number) {
  if (!value) return '₺0'
  return value >= 1000 ? `₺${(value / 1000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} B` : `₺${count(value)}`
}
function niceStep(max: number) {
  const rough = Math.max(1, max) / 5
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  return Math.ceil(rough / magnitude) * magnitude
}

// One breakdown per marketplace, shown when the card is hovered.
function Breakdown({ rows }: { rows: Array<[string, string]> }) {
  return rows.length ? <div className="rr-tip rr-bd" role="tooltip">{rows.map(([platform, value]) => <div className="rr-bd-r" key={platform}><RrLogo code={platformCode(platform)} /><span>{platformName(platform)}</span><b className="rr-num">{value}</b></div>)}</div> : null
}
const breakdownRows = (counts: Record<string, number> | undefined) => Object.entries(counts ?? {}).filter(([, value]) => value > 0).sort((left, right) => right[1] - left[1]).map(([platform, value]) => [platform, `${count(value)} adet`] as [string, string])

function PeriodTrigger({ value, options, onPick, icon: lead }: { value: string; options: Array<[string, string]>; onPick: (value: string) => void; icon?: ReactNode }) {
  return <RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}>{lead}<span>{options.find(([key]) => key === value)?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 220 }}>{options.map(([key, label]) => { const on = key === value; return <button key={key} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { onPick(key); close() }}>{key !== 'ALL' && PLATFORMS.some(([code]) => code === key) ? <RrLogo code={key} /> : <span className="rr-radio" aria-hidden="true" />}<span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor>
}

// Daily revenue: area and line for this period, dashed line for the previous one. The day tooltip follows the
// pointer, above it when the page has room there, otherwise below.
function RevenueChart({ points, previous, currency }: { points: RevenuePoint[]; previous: RevenuePoint[]; currency: string }) {
  const [hover, setHover] = useState<{ index: number; x: number; y: number } | null>(null)
  const chartRef = useRef<HTMLDivElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  const W = 760, H = 260, L = 64, R = 12, T = 12, B = 30
  const max = niceStep(Math.max(1, ...points.map(point => point.amount), ...previous.map(point => point.amount))) * 5
  const x = (index: number, length = points.length) => L + (length > 1 ? index / (length - 1) : 0) * (W - L - R)
  const y = (value: number) => T + (1 - value / max) * (H - T - B)
  const line = (series: RevenuePoint[]) => series.map((point, index) => `${index ? 'L' : 'M'}${x(index, series.length).toFixed(1)} ${y(point.amount).toFixed(1)}`).join(' ')
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
  const before = hover ? points[hover.index - 1] ?? previous[previous.length - 1] : null
  return <div ref={chartRef} className="rr-chart" onMouseMove={move} onMouseLeave={() => setHover(null)}>
    <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Günlük ciro grafiği">
      <g className="rr-grid">{Array.from({ length: 6 }, (_, index) => { const value = max / 5 * index; return <g key={index}><line x1={L} x2={W - R} y1={y(value)} y2={y(value)} /><text x={L - 10} y={y(value) + 4} textAnchor="end">{axisLabel(value)}</text></g> })}</g>
      <g className="rr-xax">{points.map((item, index) => index % labelStep === 0 || new Date(item.day).getDate() === 1 ? <text key={item.day} x={x(index)} y={H - 8} textAnchor="middle">{dayShort(item.day)}</text> : null)}</g>
      <path className="rr-area" d={`${line(points)} L${x(points.length - 1)} ${y(0)} L${x(0)} ${y(0)} Z`} />
      {previous.length > 0 && <path className="rr-prev" d={line(previous)} />}
      <path className="rr-cur" d={line(points)} />
      {hover && point && <><line className="rr-hl" x1={x(hover.index)} x2={x(hover.index)} y1={T} y2={H - B} /><circle className="rr-hp" cx={x(hover.index)} cy={y(point.amount)} r="4.5" /></>}
    </svg>
    {hover && point && <div ref={tipRef} className="rr-ctip" style={{ visibility: 'hidden' }}><b>{dayLong(point.day)}</b><table><thead><tr><th /><th>Seçilen Gün</th><th>Önceki Gün</th></tr></thead><tbody>
      <tr><td>Sipariş</td><td className="rr-num">{count(point.orderCount)}</td><td className="rr-num">{before ? count(before.orderCount) : '—'}</td></tr>
      <tr><td>Ürün (Adet)</td><td className="rr-num">{count(point.productQuantity ?? 0)}</td><td className="rr-num">{before ? count(before.productQuantity ?? 0) : '—'}</td></tr>
      <tr><td>Kargo (Paket)</td><td className="rr-num">{count(point.shipmentCount ?? 0)}</td><td className="rr-num">{before ? count(before.shipmentCount ?? 0) : '—'}</td></tr>
      <tr><td>Ciro</td><td className="rr-num"><b>{money(point.amount, currency)}</b></td><td className="rr-num">{before ? money(before.amount, currency) : '—'}</td></tr>
    </tbody></table></div>}
  </div>
}

// Option 9 of genel-bakis-performans.html: the daily average, and where today sits between the weakest and the strongest day.
function PerfCard({ label, value, delta, avgLabel = 'Günlük ortalama', avg, min, max, minLabel = 'En Düşük Gün', maxLabel = 'En Yüksek Gün', position, breakdown }: { label: string; value: string; delta: [string, boolean] | null; avgLabel?: string; avg: string; min: string; max: string; minLabel?: string; maxLabel?: string; position: number; breakdown?: Array<[string, string]> }) {
  return <article className="rr-pcardm" tabIndex={0}>
    <small>{label}</small><strong className="rr-num">{value}</strong>
    {delta ? <span className={`rr-trend ${delta[1] ? 'rr-up' : 'rr-down'}`}>{delta[1] ? ICONS.up : ICONS.down}{delta[0]}<em>önceki döneme göre</em></span> : <span className="rr-trend"><em>Katalogdaki güncel durum</em></span>}
    <span className="rr-avg">{avgLabel} <b className="rr-num">{avg}</b></span>
    <div className="rr-range" role="img" aria-label={`${minLabel} ${min}, ${maxLabel} ${max}`}><i style={{ left: `${Math.max(0, Math.min(1, position)) * 100}%` }} /></div>
    <div className="rr-ends"><span><small>{minLabel}</small><b className="rr-num">{min}</b></span><span><small>{maxLabel}</small><b className="rr-num">{max}</b></span></div>
    {breakdown && <Breakdown rows={breakdown} />}
  </article>
}

function dailyStats(values: number[], format: (value: number) => string, average: number) {
  if (!values.length) return { avg: format(0), min: format(0), max: format(0), position: 0.5 }
  const low = Math.min(...values), high = Math.max(...values), today = values[values.length - 1]
  return { avg: format(average), min: format(low), max: format(high), position: high > low ? (today - low) / (high - low) : 0.5 }
}

export function DashboardPage() {
  const today = dateKey(new Date())
  const [range, setRange] = useState<Range>('30')
  const [from, setFrom] = useState(today)
  const [to, setTo] = useState(today)
  const [chartRange, setChartRange] = useState<Exclude<Range, 'custom'>>('30')
  const [chartPlatform, setChartPlatform] = useState('ALL')
  const [noticeClosed, setNoticeClosed] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const now = new Date()
  const bootstrap = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap'), ...refresh })
  const invoiceSummary = useQuery({ queryKey: ['invoice-workspace-summary'], queryFn: () => hubApi<{ dueSoonCount: number; uninvoicedCount?: number }>('/invoice-workspace/summary'), ...refresh })
  const productSummary = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<ProductSummary>('/products/summary'), ...refresh })
  const matrix = useQuery({ queryKey: ['dashboard-integration-matrix'], queryFn: () => hubApi<MatrixRow[]>('/dashboard/integration-matrix'), ...refresh, refetchInterval: 300_000, staleTime: 120_000 })
  const platforms = Array.from(new Set((bootstrap.data?.platforms ?? []).map(platform => platform.name)))
  const report = rangeDates(range, from, to, now)
  const reportPrevious = previousRange(report.start, report.end)
  const chart = rangeDates(chartRange, from, to, now)
  const chartPrevious = previousRange(chart.start, chart.end)
  const reportKey = `${range}:${from}:${to}`
  const reportQuery = useQuery({ queryKey: ['dashboard-revenue-series', reportKey], queryFn: () => hubApi<RevenuePoint[]>(revenuePath(report.start, report.end)), ...refresh })
  const reportPreviousQuery = useQuery({ queryKey: ['dashboard-revenue-series-previous', reportKey], queryFn: () => hubApi<RevenuePoint[]>(revenuePath(reportPrevious.start, reportPrevious.end)), ...refresh })
  const chartQuery = useQuery({ queryKey: ['dashboard-chart-revenue-series', chartRange, chartPlatform], queryFn: () => hubApi<RevenuePoint[]>(revenuePath(chart.start, chart.end, chartPlatform)), ...refresh })
  const chartPreviousQuery = useQuery({ queryKey: ['dashboard-chart-revenue-series-previous', chartRange, chartPlatform], queryFn: () => hubApi<RevenuePoint[]>(revenuePath(chartPrevious.start, chartPrevious.end, chartPlatform)), ...refresh })
  const channelQuery = useQuery({ queryKey: ['dashboard-channel-revenue', reportKey, platforms], queryFn: () => Promise.all(platforms.map(async platform => ({ platform, points: await hubApi<RevenuePoint[]>(revenuePath(report.start, report.end, platform)) }))), enabled: platforms.length > 0, ...refresh })

  const metrics = bootstrap.data?.metrics
  const series = reportQuery.data ?? []
  const previousSeries = reportPreviousQuery.data ?? []
  const currency = series.find(point => point.amount > 0)?.currency || 'TRY'
  const total = series.reduce((sum, point) => sum + point.amount, 0)
  const orders = series.reduce((sum, point) => sum + point.orderCount, 0)
  const previousTotal = previousSeries.reduce((sum, point) => sum + point.amount, 0)
  const previousOrders = previousSeries.reduce((sum, point) => sum + point.orderCount, 0)
  const basket = orders ? total / orders : 0
  const previousBasket = previousOrders ? previousTotal / previousOrders : 0
  const days = Math.max(1, series.length)
  const channels = (channelQuery.data ?? []).map(channel => ({ platform: channel.platform, amount: channel.points.reduce((sum, point) => sum + point.amount, 0), orders: channel.points.reduce((sum, point) => sum + point.orderCount, 0) })).filter(channel => channel.amount > 0 || channel.orders > 0).sort((left, right) => right.amount - left.amount || right.orders - left.orders)
  const channelAmount = channels.reduce((sum, channel) => sum + channel.amount, 0)
  const channelOrders = channels.reduce((sum, channel) => sum + channel.orders, 0)
  const channelShares = channels.map((channel, index) => ({ ...channel, color: platformColor(channel.platform, index), share: channelAmount > 0 ? channel.amount / channelAmount * 100 : channelOrders > 0 ? channel.orders / channelOrders * 100 : 0 }))
  const donut = channelShares.length ? `conic-gradient(${channelShares.map((channel, index) => { const start = channelShares.slice(0, index).reduce((sum, item) => sum + item.share, 0); return `${channel.color} ${start}% ${start + channel.share}%` }).join(', ')})` : 'conic-gradient(var(--rr-line) 0 100%)'
  const chartSeries = chartQuery.data ?? []
  const chartPreviousSeries = chartPreviousQuery.data ?? []
  const chartCurrency = chartSeries.find(point => point.amount > 0)?.currency || 'TRY'
  const chartTotal = chartSeries.reduce((sum, point) => sum + point.amount, 0)
  const chartPreviousTotal = chartPreviousSeries.reduce((sum, point) => sum + point.amount, 0)
  const chartDelta = trend(chartTotal, chartPreviousTotal)
  const rows = matrix.data ?? []
  const attention = rows.filter(row => row.issue || Object.values(row.cells).some(cell => cell.state === 'bad' || cell.state === 'warn')).length
  const errors = [bootstrap.error, productSummary.error, reportQuery.error, reportPreviousQuery.error, chartQuery.error, chartPreviousQuery.error, channelQuery.error, matrix.error].filter(Boolean)
  const loadingValue = (loading: boolean, value: number | undefined) => loading ? '—' : count(value ?? 0)
  const revenueStats = dailyStats(series.map(point => point.amount), value => money(value, currency), total / days)
  const orderStats = dailyStats(series.map(point => point.orderCount), count, orders / days)
  const basketStats = dailyStats(series.filter(point => point.orderCount > 0).map(point => point.amount / point.orderCount), value => money(value, currency), basket)
  const products = productSummary.data
  const ops: Array<[keyof typeof ICONS, string, number | null, 'warn' | 'bad' | '', string, Record<string, number> | undefined]> = [
    ['cart', 'Bekleyen Siparişler', bootstrap.isLoading ? null : metrics?.pendingOrders ?? 0, 'warn', dashboardOperationalLinks.pendingOrders, metrics?.pendingByPlatform],
    ['late', 'Geciken Siparişler', bootstrap.isLoading ? null : metrics?.lateOrders ?? 0, 'bad', '/orders', metrics?.lateByPlatform],
    ['ret', 'Bekleyen İadeler', bootstrap.isLoading ? null : metrics?.pendingReturns ?? 0, 'warn', dashboardOperationalLinks.pendingReturns, metrics?.pendingReturnsByPlatform],
    ['invoice', 'Bekleyen Faturalar', bootstrap.isLoading || invoiceSummary.isLoading ? null : invoiceSummary.data?.uninvoicedCount ?? metrics?.uninvoicedInvoices ?? 0, '', dashboardOperationalLinks.pendingInvoices, undefined],
    ['due', 'Yaklaşan Faturalar', invoiceSummary.isLoading ? null : invoiceSummary.data?.dueSoonCount ?? 0, '', dashboardOperationalLinks.dueInvoices, undefined],
    ['lowst', 'Düşük Stok', bootstrap.isLoading ? null : metrics?.lowStockProducts ?? 0, 'warn', '/products', undefined],
  ]
  function downloadReport() {
    const escape = (value: string | number) => `"${String(value).replace(/"/g, '""')}"`
    const csvRows = [['Tarih', 'Ciro', 'Sipariş', 'Para Birimi'], ...series.map(point => [new Date(point.day).toLocaleDateString('tr-TR'), point.amount.toLocaleString('tr-TR', { maximumFractionDigits: 2 }), point.orderCount, point.currency])]
    const url = URL.createObjectURL(new Blob([`﻿${csvRows.map(row => row.map(escape).join(';')).join('\r\n')}`], { type: 'text/csv;charset=utf-8' }))
    const link = document.createElement('a')
    const name = `ravencia-rapor-${dateKey(new Date())}.csv`
    link.href = url
    link.download = name
    link.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 0)
    setToast(`${name} indirildi (Tarih, Ciro, Sipariş, Para Birimi).`)
  }
  const storeLabel = (row: MatrixRow) => /^\d+$/.test(row.externalStoreId) ? `Mağaza ${row.externalStoreId}` : row.externalStoreId

  return <section className="content f3 rr rr-dashboard" aria-label="Genel Bakış">
    <div className="rr-ph"><div><div className="rr-crumb">Operasyon Merkezi</div><h1>Genel Bakış</h1><p>Siparişler, iadeler, faturalar ve stok için bugün ilgilenmeniz gerekenler ve satış performansınız.</p></div><div className="rr-acts">
      <PeriodTrigger value={range} options={PERIODS} onPick={value => setRange(value as Range)} icon={<RrIcon name="cal" />} />
      {range === 'custom' && <DateRangePicker className="rr-date" from={from} to={to} onChange={value => { setFrom(value.from); setTo(value.to) }} label="Rapor Tarihi" placeholder="Başlangıç – Bitiş" chevron={<RrIcon name="chev" />} />}
      <button type="button" className="rr-btn" onClick={downloadReport} disabled={reportQuery.isLoading}><RrIcon name="export" />Raporu İndir</button>
    </div></div>
    {errors.length > 0 && !noticeClosed && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span><b>Bazı rapor verileri alınamadı.</b> Görünen değerler kısmi olabilir; birkaç dakika içinde tekrar denenir.</span><button type="button" className="rr-icon-btn" onClick={() => setNoticeClosed(true)} aria-label="Uyarıyı kapat"><RrIcon name="close" /></button></div>}
    <section className="rr-dsect"><header><h2>Operasyon Metrikleri</h2><p>Bugün ilgilenmeniz gereken işler</p></header><div className="rr-opgrid">{ops.map(([kind, label, value, tone, to, byPlatform]) => <Link key={kind} className={`rr-opcard${tone && value ? ` rr-${tone}` : ''}`} to={to}><span className="rr-op-ic">{ICONS[kind]}</span><span className="rr-op-t"><strong className="rr-num">{value == null ? '—' : count(value)}</strong><small>{label}</small></span><RrIcon name="arrowR" /><Breakdown rows={breakdownRows(byPlatform)} /></Link>)}</div></section>
    <section className="rr-dsect"><header><h2>Performans Metrikleri</h2><p>{range === 'custom' ? 'Seçilen dönem' : periodLabel(range)} · önceki eşit dönemle karşılaştırma</p></header><div className="rr-metrics rr-perf">
      <PerfCard label="Toplam Gelir" value={reportQuery.isLoading ? '—' : money(total, currency)} delta={trend(total, previousTotal)} {...revenueStats} breakdown={channels.map(channel => [channel.platform, money(channel.amount, currency)])} />
      <PerfCard label="Toplam Sipariş" value={loadingValue(reportQuery.isLoading, orders)} delta={trend(orders, previousOrders)} {...orderStats} breakdown={channels.map(channel => [channel.platform, `${count(channel.orders)} adet`])} />
      <PerfCard label="Ortalama Sepet" value={reportQuery.isLoading ? '—' : money(basket, currency)} delta={trend(basket, previousBasket)} {...basketStats} />
      <PerfCard label="Aktif Ürün" value={loadingValue(productSummary.isLoading, products?.activeCount)} delta={null} avgLabel="Stoksuz ürün" avg={count(products?.outOfStockCount ?? 0)} minLabel="Düşük Stoklu" min={count(products?.lowStockCount ?? 0)} maxLabel="Toplam Ürün" max={count(products?.totalCount ?? 0)} position={products?.totalCount ? products.activeCount / products.totalCount : 0} />
    </div></section>
    <section className="rr-dsect"><header><h2>Detaylı Görünüm</h2></header><div className="rr-detail-grid">
      <article className="rr-panel rr-dcard"><div className="rr-panel-h"><div><h2>Gelir Performansı</h2><p>Satışlarınızın büyük resmini görün.</p></div><div className="rr-ctools">
        <PeriodTrigger value={chartRange} options={PERIODS.filter(([value]) => value !== 'custom')} onPick={value => setChartRange(value as Exclude<Range, 'custom'>)} />
        <PeriodTrigger value={chartPlatform} options={[['ALL', 'Tüm Platformlar'], ...platforms.map(platform => [platform, platformName(platform)] as [string, string])]} onPick={setChartPlatform} />
      </div></div>
        <div className="rr-dbody"><div className="rr-ctotal"><small>{periodLabel(chartRange)}{chartPlatform === 'ALL' ? '' : ` · ${platformName(chartPlatform)}`}</small><strong className="rr-num">{chartQuery.isLoading ? '—' : money(chartTotal, chartCurrency)}</strong><span className={`rr-trend ${chartDelta[1] ? 'rr-up' : 'rr-down'}`}>{chartDelta[1] ? ICONS.up : ICONS.down}{chartDelta[0]}<em>önceki döneme göre</em></span></div>
          {chartSeries.some(point => point.amount > 0) ? <RevenueChart points={chartSeries} previous={chartPreviousSeries} currency={chartCurrency} /> : <div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Gelir Verisi Yok</b><span className="rr-lead">Bu dönem için gelir verisi bulunmuyor.</span></div>}
          <div className="rr-legend"><span><i className="rr-lg-cur" />Bu Dönem</span><span><i className="rr-lg-prev" />Önceki Dönem</span></div></div></article>
      <article className="rr-panel rr-dcard"><div className="rr-panel-h"><div><h2>Satış Kanalları</h2><p>Seçilen dönemin gelir dağılımı</p></div><Link className="rr-btn rr-link" to="/integrations">Kanalları Yönet <RrIcon name="arrowR" /></Link></div>
        {channelShares.length ? <div className="rr-dbody rr-chan"><div className="rr-donut" style={{ background: donut }}><div><small>Toplam Sipariş</small><strong className="rr-num">{count(channelOrders)}</strong><span>{range === 'custom' ? 'Seçilen Dönem' : periodLabel(range)}</span></div></div><ul className="rr-clist">{channelShares.map(channel => <li key={channel.platform}><i style={{ background: channel.color }} /><RrLogo code={platformCode(channel.platform)} /><span>{platformName(channel.platform)}</span><b className="rr-num">%{channel.share.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}</b></li>)}</ul></div> : <div className="rr-dbody"><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Kanal Dağılımı Yok</b><span className="rr-lead">Bu dönem için kanal dağılımı bulunmuyor.</span></div></div>}
        <div className="rr-dfoot"><span>{channelShares.length} kanal · {range === 'custom' ? 'Seçilen Dönem' : periodLabel(range)}</span><Link className="rr-btn rr-link" to="/integrations">Bağlantıları Yönet <RrIcon name="arrowR" /></Link></div></article>
    </div></section>
    <section className="rr-panel"><div className="rr-panel-h"><div><h2>Entegrasyon Durumu</h2><p>Pazaryerleriyle bağlantının son 7 günü</p></div><span className={`rr-hpill${attention ? ' rr-attn' : ''}`}><InvoiceGlyph kind={attention ? 'waiting' : 'done'} />{attention ? `${attention} konu dikkatinizi bekliyor` : 'Her şey yolunda'}</span></div>
      <div className="rr-mxwrap"><table className="rr-mxt"><thead><tr><th>Pazar Yeri</th>{DATA_KEYS.map(([, label]) => <th key={label}>{label}</th>)}<th /></tr></thead><tbody>
        {!rows.length && <tr><td colSpan={DATA_KEYS.length + 2}><span className="rr-lead">{matrix.isLoading ? 'Bağlantılar yükleniyor…' : 'Aktif pazar yeri bağlantısı bulunmuyor.'}</span></td></tr>}
        {rows.map(row => <tr key={row.connectionId} className={row.issue ? 'rr-attn' : undefined}><td><span className="rr-mx-nm"><RrLogo code={row.platformCode} /><span><b>{row.displayName}</b><small title={row.issue ?? undefined}>{row.issue ?? storeLabel(row)}</small></span></span></td>
          {DATA_KEYS.map(([key, label]) => { const cell = row.cells[key] ?? { state: 'off', lastSuccessAt: null, lastProblemAt: null }; const when = age(cell.lastSuccessAt); return <td key={key} data-l={label}><span className={`rr-mx rr-${cell.state}`} title={`${label} · ${CELL_STATE[cell.state]}`}><i />{cell.state === 'off' ? 'Yok' : when ?? 'Durdu'}</span></td> })}
          <td className="rr-mx-act">{row.issue && <Link className="rr-btn rr-link" to="/jobs">İncele</Link>}</td></tr>)}
      </tbody></table></div>
      <div className="rr-mx-foot"><span>Son 24 saatte <b className="rr-num">{count(metrics?.recentJobCount ?? 0)}</b> istek · <b className="rr-num">{count(metrics?.deadJobCount ?? 0)}</b> tamamlanamayan iş · <b className="rr-num">{count(metrics?.manualReviewJobCount ?? 0)}</b> karar bekleyen iş</span><span className="rr-legend"><span><i className="rr-mx-l rr-ok" />Güncel</span><span><i className="rr-mx-l rr-warn" />Gecikiyor</span><span><i className="rr-mx-l rr-bad" />Durdu</span><span><i className="rr-mx-l rr-off" />Yok</span></span></div>
    </section>
    {toast && <NoticeToast kind="success" title="Rapor İndirildi" message={toast} onClose={() => setToast(null)} durationMs={6000} />}
  </section>
}
