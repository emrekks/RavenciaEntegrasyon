import { Fragment, useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, UiIcon, type UiIconName } from '../../shared/components'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformMultiSelect } from '../../shared/platform-multi-select'
import { ViewTabsWithMore } from '../../shared/view-tabs'
import {
  change, downloadCsv, formatCount, formatDay, formatHours, formatNumber, formatPercent, formatTl, formatTlShort, moneyOrMissing, platformColor, platformName, reportQuery, sortRows, toCsv, unknownLabel,
  type Change, type CsvColumn, type ReportCosts, type ReportGroup, type ReportProduct, type ReportReason, type ReportSales, type ReportStockRow, type ReportTimePoint
} from './report-format'
import { CommissionTariffsPanel, commissionTariffPlatforms } from '../marketplace/integrations/CommissionTariffsPanel'
import { SalesMap } from './SalesMap'
import { BarsBackdrop, CountUp, Ring, smoothPath } from './report-visuals'
import './reports.css'

// The weekly commission tariffs the reports read a missing commission from, uploaded here per store (live stores of a
// platform whose tariff export the panel reads); a store is picked when there is more than one.
function CommissionTariffs({ data }: { data: ReportSales }) {
  const stores = data.channels.filter(channel => commissionTariffPlatforms.has(channel.platformCode) && !channel.isTest)
  const [storeId, setStoreId] = useState(stores[0]?.id ?? '')
  const store = stores.find(channel => channel.id === storeId) ?? stores[0]
  if (!store) return <p className="reports-empty">Komisyon tarifesi yüklenebilecek bir mağaza yok (şimdilik yalnız Trendyol).</p>
  return <div className="reports-tariffs">
    {stores.length > 1 && <label className="reports-field"><span>Mağaza</span><select value={store.id} onChange={event => setStoreId(event.target.value)}>
      {stores.map(channel => <option key={channel.id} value={channel.id}>{channel.displayName}</option>)}
    </select></label>}
    <CommissionTariffsPanel key={store.id} connectionId={store.id} platformName={platformName(store.platformCode)} />
  </div>
}

// The report as tabs: an overview, then one tab per area with its own breakdown (Ürünler and Operasyon have sub-tabs),
// and the commission tariffs. A card's "Tümünü Gör" moves to its area's tab (?view=…&sub=…); an older ?detail=… link
// lands on the same tab.
type ViewKey = 'overview' | 'geo' | 'products' | 'profit' | 'ops' | 'customers' | 'tariffs'
const viewKeys: ViewKey[] = ['overview', 'geo', 'products', 'profit', 'ops', 'customers', 'tariffs']
type ProductSub = 'products' | 'brands'
type OpsSub = 'returns' | 'cancellations' | 'cargo' | 'invoices' | 'stock'
type DetailKey = 'platforms' | 'cities' | 'products' | 'brands' | 'returns' | 'cancellations' | 'cargo' | 'time' | 'customers' | 'stock' | 'cost' | 'invoices'
const detailHome: Record<DetailKey, { view: ViewKey; sub?: string }> = {
  platforms: { view: 'profit' }, cost: { view: 'profit' }, cities: { view: 'geo' },
  products: { view: 'products', sub: 'products' }, brands: { view: 'products', sub: 'brands' },
  returns: { view: 'ops', sub: 'returns' }, cancellations: { view: 'ops', sub: 'cancellations' }, cargo: { view: 'ops', sub: 'cargo' }, invoices: { view: 'ops', sub: 'invoices' }, stock: { view: 'ops', sub: 'stock' },
  time: { view: 'customers' }, customers: { view: 'customers' }
}
const isDetailKey = (value: string | null): value is DetailKey => value !== null && value in detailHome
const isViewKey = (value: string | null): value is ViewKey => value !== null && (viewKeys as string[]).includes(value)

// A tab's label: its icon and the text, which keeps the width of its selected weight (as viewTabLabel does).
const tabLabel = (icon: UiIconName, text: string) => <><UiIcon name={icon} size={15} className="rv-view-tab-icon" /><span className="rv-tab-text" data-text={text}>{text}</span></>

function dayKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function lastDays(days: number) { const end = new Date(); const start = new Date(); start.setDate(start.getDate() - days + 1); return { from: dayKey(start), to: dayKey(end) } }

// Raporlar: sales, returns, cancellations, cargo, customers, stock and profit for a chosen period and platforms, each
// compared with the previous period of the same length. Every number comes from the server's report; a value the data
// cannot support is shown as "—" or "Tutar yok" with the reason in the data notes, never as a guess.
export function ReportsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const detailParam = searchParams.get('detail')
  const legacy = isDetailKey(detailParam) ? detailHome[detailParam] : null
  const viewParam = searchParams.get('view')
  const view: ViewKey = legacy?.view ?? (isViewKey(viewParam) ? viewParam : 'overview')
  const sub = legacy?.sub ?? searchParams.get('sub')
  const body = useRef<HTMLDivElement>(null)
  const [range, setRange] = useState(() => lastDays(30))
  const [platforms, setPlatforms] = useState<string[] | null>(null)
  const [granularity, setGranularity] = useState('')
  const [includeTest, setIncludeTest] = useState(false)
  const query = reportQuery({ from: range.from, to: range.to, platforms, granularity, includeTest })
  const report = useQuery({ queryKey: ['reports-sales', query], queryFn: () => hubApi<ReportSales>(`/reports/sales?${query}`), staleTime: 60_000, placeholderData: previous => previous })
  const data = report.data
  const platformOptions = useMemo(() => [...new Set((data?.channels ?? []).map(channel => channel.platformCode))].map(code => ({ value: code, label: platformName(code) })), [data?.channels])
  const go = (nextView: ViewKey, nextSub: string | null = null) => setSearchParams(current => {
    const next = new URLSearchParams(current)
    next.delete('detail')
    if (nextView === 'overview') next.delete('view'); else next.set('view', nextView)
    if (nextSub) next.set('sub', nextSub); else next.delete('sub')
    return next
  }, { replace: true })
  // "Tümünü Gör": the area's tab, scrolled to the tabs so the breakdown starts at the top.
  const openDetail = (key: DetailKey) => { go(detailHome[key].view, detailHome[key].sub ?? null); body.current?.scrollIntoView?.({ block: 'start', behavior: 'smooth' }) }

  return <section className="content reports-page">
    <div className="page-heading reports-heading">
      <div><p className="eyebrow">Analiz</p><h1>Raporlar</h1><p className="reports-lead">Satış, iade, iptal, kargo ve kârlılık; platform, şehir ve ürün kırılımıyla, önceki dönemle karşılaştırmalı.</p></div>
      {data && <p className="reports-period">{formatDay(data.from)} – {formatDay(data.to)}<small>Önceki dönem: {formatDay(data.previousFrom)} – {formatDay(data.previousTo)}</small></p>}
    </div>
    <div className="reports-filters" role="search" aria-label="Rapor filtreleri">
      <div className="reports-field"><span>Dönem</span><DateRangePicker from={range.from} to={range.to} onChange={next => { if (next.from && next.to) setRange({ from: next.from, to: next.to }) }} label="Rapor dönemi" /></div>
      <div className="reports-field"><PlatformMultiSelect label="Platform" options={platformOptions} selectedCodes={platforms} onChange={next => setPlatforms(next.length ? next : null)} /></div>
      <label className="reports-field"><span>Gruplama</span><select value={granularity} onChange={event => setGranularity(event.target.value)} aria-label="Zaman gruplaması">
        <option value="">Otomatik</option><option value="day">Gün</option><option value="week">Hafta</option><option value="month">Ay</option>
      </select></label>
      <label className="reports-toggle"><input type="checkbox" checked={includeTest} onChange={event => setIncludeTest(event.target.checked)} /><span>Test Mağazalarını Dahil Et</span></label>
      {report.isFetching && data && <span className="reports-refreshing" role="status"><UiIcon name="loader" size={14} />Güncelleniyor</span>}
    </div>
    {report.isLoading && <Busy text="Rapor hazırlanıyor…" />}
    {report.isError && !data && <ErrorBox error={report.error} onRetry={() => void report.refetch()} />}
    {data && (() => {
      const tabs = reportTabs(data)
      return <>
        <div ref={body} className="reports-tabs-anchor" />
        <ViewTabsWithMore className="reports-tabs" ariaLabel="Rapor görünümü" more={[]} value={view} onChange={value => go(value as ViewKey)}
          items={tabs.map(tab => ({ value: tab.value, label: tabLabel(tab.icon, tab.label), count: tab.count, countTone: tab.warn ? 'warn' : undefined }))} />
        <div className="reports-body" role="tabpanel" aria-label={tabs.find(tab => tab.value === view)?.label}>
          {view === 'overview' ? <Overview data={data} onMore={openDetail} />
            : view === 'geo' ? <Geography data={data} />
            : view === 'products' ? <ProductsView data={data} sub={sub === 'brands' ? 'brands' : 'products'} onSub={next => go('products', next)} />
            : view === 'profit' ? <ProfitView data={data} />
            : view === 'ops' ? <OperationsView data={data} sub={isOpsSub(sub) ? sub : 'returns'} onSub={next => go('ops', next)} />
            : view === 'customers' ? <CustomersView data={data} />
            : <CommissionTariffs data={data} />}
        </div>
      </>
    })()}
  </section>
}

type ReportTab = { value: ViewKey; label: string; icon: UiIconName; count?: string; warn?: boolean }

// The tabs; profit carries its margin and operations its cancel rate (warning colour over the line), so all seven still
// fit one row on a laptop (the overview's figures are its KPIs, right under the tabs). A count is never left out once
// shown: the tabs would keep showing the last one remembered.
function reportTabs(data: ReportSales): ReportTab[] {
  const now = data.summary
  const margin = data.cost.profitQuantity > 0 ? data.cost.netMargin : data.cost.costedQuantity > 0 ? data.cost.grossMargin : null
  return [
    { value: 'overview', label: 'Genel Bakış', icon: 'dashboard' },
    { value: 'geo', label: 'Coğrafya', icon: 'globe' },
    { value: 'products', label: 'Ürünler', icon: 'products' },
    { value: 'profit', label: 'Kârlılık', icon: 'trend', count: formatPercent(margin), warn: margin !== null && margin < 0 },
    { value: 'ops', label: 'Operasyon', icon: 'truck', count: `İptal ${formatPercent(now.cancelRate)}`, warn: (now.cancelRate ?? 0) > data.cancellations.warningRate },
    { value: 'customers', label: 'Müşteriler', icon: 'users' },
    { value: 'tariffs', label: 'Tarifeler', icon: 'invoice' }
  ]
}

const isOpsSub = (value: string | null): value is OpsSub => value === 'returns' || value === 'cancellations' || value === 'cargo' || value === 'invoices' || value === 'stock'

// A tab's own sub-tabs, in the panel's tab style.
function SubTabs<T extends string>({ label, value, items, onChange }: { label: string; value: T; items: Array<{ value: T; label: string; icon: UiIconName; count?: string; warn?: boolean }>; onChange: (value: T) => void }) {
  return <ViewTabsWithMore className="reports-subtabs" ariaLabel={label} more={[]} value={value} onChange={next => onChange(next as T)}
    items={items.map(item => ({ value: item.value, label: tabLabel(item.icon, item.label), count: item.count, countTone: item.warn ? 'warn' : undefined }))} />
}

// The period's course: the measure chosen in the corner as a smooth line, the previous period dashed behind it.
function TrendPanel({ data }: { data: ReportSales }) {
  const [metric, setMetric] = useState<Metric>('netRevenue')
  const [showPrevious, setShowPrevious] = useState(true)
  return <Panel title="Satış Trendi" className="reports-trend" info={`${data.granularity === 'day' ? 'Günlük' : data.granularity === 'week' ? 'Haftalık' : 'Aylık'} toplamlar; kesikli çizgi önceki dönemin aynı sıradaki dilimi.`}
    actions={<>
      <select aria-label="Grafik ölçüsü" value={metric} onChange={event => setMetric(event.target.value as Metric)}>{(Object.keys(metricLabels) as Metric[]).map(key => <option key={key} value={key}>{metricLabels[key]}</option>)}</select>
      <label className="reports-toggle"><input type="checkbox" checked={showPrevious} onChange={event => setShowPrevious(event.target.checked)} /><span>Önceki Dönem</span></label>
    </>}>
    <TrendChart points={data.timeSeries} previous={data.previousTimeSeries} metric={metric} showPrevious={showPrevious} granularity={data.granularity} />
  </Panel>
}

// Four headline tiles, each one large figure: net revenue and orders over their bars for the period, what the
// marketplaces took (a ring of its share of net revenue) and the estimated profit (a ring of its margin).
function Headline({ data, flow }: { data: ReportSales; flow: ProfitSteps }) {
  const now = data.summary
  const before = data.previousSummary
  const noAmount = now.ordersWithAmount === 0
  const step = (key: string) => flow.steps?.find(item => item.key === key) ?? null
  const payout = step('payout')?.value ?? null
  const profit = step('profit')
  const profitValue = profit?.value ?? null
  const charged = payout !== null ? now.netRevenue - payout : null
  const chargedShare = charged !== null && now.netRevenue > 0 ? charged / now.netRevenue : null
  const margin = profitValue === null ? null : data.cost.profitQuantity > 0 ? data.cost.netMargin : data.cost.grossMargin
  const finance = financeCosts(data) !== null
  return <div className="reports-kpis">
    <Kpi label="Net Ciro" value={noAmount ? null : now.netRevenue} missing="Tutar yok" format={formatTl} delta={noAmount ? null : change(now.netRevenue, before.netRevenue)}
      hint={`Önceki ${before.ordersWithAmount > 0 ? formatTl(before.netRevenue) : '—'}`} info="Satış tutarından iade edilen tutar düşülür; Genel Bakış ile aynı kural."
      visual={<BarsBackdrop values={data.timeSeries.map(point => point.netRevenue)} />} />
    <Kpi label="Sipariş" value={now.orders} format={formatCount} delta={change(now.orders, before.orders)} hint={`${formatNumber(now.soldQuantity)} adet`}
      info="İptal edilmeyen siparişler; tamamı iade edilenler dahil. Adet, sipariş edilen − iptal edilendir."
      visual={<BarsBackdrop values={data.timeSeries.map(point => point.orders)} />} />
    <Kpi label={finance ? 'Pazaryeri Kesintileri' : 'Tahmini Komisyon'} value={charged} missing="Bilinmiyor" format={formatTl}
      hint={chargedShare === null ? 'Komisyon bilinmiyor' : `Net cironun ${formatPercent(chargedShare)}`}
      info={finance ? 'Komisyon, kargo ve Trendyol kesintileri; gerçek ve henüz kesinleşmeyenler için tahmini. Ürün maliyeti hariç.' : 'Siparişte gelen komisyon, yoksa yüklenen tarifedeki bant. Kargo ve diğer kesintiler okunmuyor.'}
      visual={<Ring value={chargedShare} tone="bad" label={`Net cironun ${formatPercent(chargedShare)} kadarı`} />} />
    <Kpi label={profit?.label ?? 'Tahmini Kâr'} value={profitValue} missing={profit?.missing ?? '—'} format={formatTl} tone={profitValue !== null && profitValue < 0 ? 'bad' : undefined}
      hint={margin === null ? 'Maliyet girilince görünür' : `${data.cost.profitQuantity > 0 ? 'Net' : 'Brüt'} marj ${formatPercent(margin)}`}
      info={profit?.note ?? 'Ürün kartında varyant maliyeti girildiğinde kâr burada görünür.'}
      visual={<Ring value={margin} tone={margin !== null && margin < 0 ? 'bad' : 'good'} label={`Marj ${formatPercent(margin)}`} />} />
  </div>
}

// A change is good or bad by its direction; for returns and cancellations, up is bad.
function Delta({ delta, invert = false }: { delta: Change | null | undefined; invert?: boolean }) {
  if (!delta) return <em className="reports-delta is-flat" title="Önceki dönemde veri yok">—</em>
  const tone = delta.direction === 'flat' ? 'flat' : (delta.direction === 'up') !== invert ? 'good' : 'bad'
  return <em className={`reports-delta is-${tone}`} title="Önceki döneme göre">{delta.direction === 'up' ? '▲' : delta.direction === 'down' ? '▼' : '■'} {delta.text}</em>
}

// One headline tile: its figure counts up; the "?" button explains the formula on hover and on keyboard focus.
function Kpi({ label, value, missing = '—', format, delta, hint, info, tone, visual }: {
  label: string; value: number | null; missing?: string; format: (value: number) => string; delta?: Change | null; hint?: string; info: string; tone?: 'bad'; visual?: ReactNode
}) {
  return <article className={`reports-kpi${tone ? ` is-${tone}` : ''}${value === null ? ' is-missing' : ''}`}>
    <span className="reports-kpi-head"><small>{label}</small><InfoTip label={label} text={info} /></span>
    <strong>{value === null ? missing : <CountUp value={value} format={format} />}</strong>
    <span className="reports-kpi-foot">{delta !== undefined && <Delta delta={delta} />}{hint && <span>{hint}</span>}</span>
    {visual && <span className="reports-kpi-visual">{visual}</span>}
  </article>
}

function InfoTip({ label, text }: { label: string; text: string }) {
  const id = useId()
  return <span className="reports-info">
    <button type="button" aria-label={`${label} nasıl hesaplanır`} aria-describedby={id}><UiIcon name="help" size={14} /></button>
    <span role="tooltip" id={id}>{text}</span>
  </span>
}

function DataNotes({ notes }: { notes: string[] }) {
  if (!notes.length) return null
  return <details className="reports-notes">
    <summary><UiIcon name="info" size={16} />Veri Notları <span>{notes.length}</span><small>Rakamların nasıl hesaplandığı ve eksik bilgiler</small></summary>
    <ul>{notes.map(note => <li key={note}>{note}</li>)}</ul>
  </details>
}

// A panel's explanation is either written under its title or, on the overview, kept behind a "?" beside it.
function Panel({ title, description, info, actions, children, className }: { title: string; description?: string; info?: string; actions?: ReactNode; children: ReactNode; className?: string }) {
  return <section className={`reports-panel${className ? ` ${className}` : ''}`}>
    <header><div>{info ? <span className="reports-panel-title"><h2>{title}</h2><InfoTip label={title} text={info} /></span> : <h2>{title}</h2>}{description && <p>{description}</p>}</div>{actions && <div className="reports-panel-actions">{actions}</div>}</header>
    {children}
  </section>
}

type Column<T> = { key: string; header: string; value: (row: T) => number | string | null; render?: (row: T) => ReactNode; numeric?: boolean; csv?: (row: T) => string | number | null }

// A table with sortable columns, the unknown bucket last and muted, an optional row expansion and its CSV export.
function DataTable<T>({ title, description, rows, columns, rowKey, unknown, expand, csvName, empty = 'Bu dönemde kayıt yok.', initialSort, footer }: {
  title: string; description?: string; rows: T[]; columns: Column<T>[]; rowKey: (row: T) => string; unknown?: (row: T) => boolean
  expand?: (row: T) => ReactNode; csvName: string; empty?: string; initialSort?: string; footer?: ReactNode
}) {
  const [sort, setSort] = useState<{ key: string; direction: 'asc' | 'desc' }>(() => ({ key: initialSort ?? '', direction: 'desc' }))
  const [open, setOpen] = useState<string | null>(null)
  const isUnknown = unknown ?? (() => false)
  const column = columns.find(item => item.key === sort.key)
  const sorted = column ? sortRows(rows, column.value, sort.direction, isUnknown) : sortRows(rows, () => 0, 'desc', isUnknown)
  const exportCsv = () => downloadCsv(`${csvName}.csv`, toCsv(sorted, columns.map<CsvColumn<T>>(item => ({ header: item.header, value: item.csv ?? item.value }))))
  return <Panel title={title} description={description} actions={<button type="button" className="secondary reports-csv" onClick={exportCsv} disabled={!rows.length}><UiIcon name="download" size={16} />CSV</button>}>
    {rows.length === 0 ? <p className="reports-empty">{empty}</p> : <div className="reports-table-wrap"><table className="reports-table">
      <thead><tr>{expand && <th aria-label="Ayrıntı" className="reports-expand-col" />}{columns.map(item => <th key={item.key} className={item.numeric ? 'is-num' : undefined} aria-sort={sort.key === item.key ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined}>
        <button type="button" onClick={() => setSort(current => ({ key: item.key, direction: current.key === item.key && current.direction === 'desc' ? 'asc' : 'desc' }))}>{item.header}{sort.key === item.key && <span aria-hidden="true">{sort.direction === 'desc' ? '↓' : '↑'}</span>}</button>
      </th>)}</tr></thead>
      <tbody>{sorted.map(row => {
        const key = rowKey(row)
        const expanded = open === key
        return <Fragment key={key}>
          <tr className={isUnknown(row) ? 'is-unknown' : undefined}>
            {expand && <td className="reports-expand-col"><button type="button" className="reports-expand" aria-expanded={expanded} aria-label={expanded ? 'Ayrıntıyı kapat' : 'Ayrıntıyı aç'} onClick={() => setOpen(expanded ? null : key)}><UiIcon name={expanded ? 'chevronDown' : 'chevronRight'} size={16} /></button></td>}
            {columns.map(item => <td key={item.key} className={item.numeric ? 'is-num' : undefined}>{item.render ? item.render(row) : item.value(row) ?? '—'}</td>)}
          </tr>
          {expand && expanded && <tr className="reports-expanded"><td colSpan={columns.length + 1}>{expand(row)}</td></tr>}
        </Fragment>
      })}</tbody>
      {footer && <tfoot>{footer}</tfoot>}
    </table></div>}
  </Panel>
}

// Horizontal bars with the value written out; the unknown bucket is muted.
function Bars({ items, format, label }: { items: Array<{ key: string; label: string; value: number; muted?: boolean; note?: string }>; format: (value: number) => string; label: string }) {
  const max = Math.max(0, ...items.map(item => item.value))
  if (!items.length || max <= 0) return <p className="reports-empty">Gösterilecek değer yok.</p>
  return <ul className="reports-bars" aria-label={label}>{items.map(item => <li key={item.key} className={item.muted ? 'is-unknown' : undefined}>
    <span className="reports-bar-label" title={item.label}>{item.label}</span>
    <span className="reports-bar-track" aria-hidden="true"><span style={{ width: `${Math.max(2, item.value / max * 100)}%` }} /></span>
    <span className="reports-bar-value">{format(item.value)}{item.note && <small>{item.note}</small>}</span>
  </li>)}</ul>
}

type Metric = 'netRevenue' | 'orders' | 'soldQuantity' | 'returnedQuantity' | 'cancelledOrders'
const metricLabels: Record<Metric, string> = { netRevenue: 'Net Ciro', orders: 'Sipariş', soldQuantity: 'Satılan Adet', returnedQuantity: 'İade Adedi', cancelledOrders: 'İptal' }

// The period's buckets as a line, the previous period's (bucket by bucket) as a dashed line behind it. As on the
// overview, the bucket under the pointer is marked and its card follows the pointer, above it when there is room.
function TrendChart({ points, previous, metric, showPrevious, granularity }: { points: ReportTimePoint[]; previous: ReportTimePoint[]; metric: Metric; showPrevious: boolean; granularity: string }) {
  const [hover, setHover] = useState<{ index: number; x: number; y: number } | null>(null)
  const wrapRef = useRef<HTMLDivElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const tip = tipRef.current, wrap = wrapRef.current
    if (!tip || !wrap || !hover) return
    const box = wrap.getBoundingClientRect(), tipWidth = tip.offsetWidth, tipHeight = tip.offsetHeight, gap = 14
    tip.style.left = `${Math.max(0, Math.min(box.width - tipWidth, hover.x - box.left - tipWidth / 2))}px`
    tip.style.top = `${hover.y - tipHeight - gap >= 8 ? hover.y - box.top - tipHeight - gap : hover.y - box.top + gap + 4}px`
    tip.style.visibility = ''
  }, [hover])
  const width = 760
  const height = 240
  const pad = { top: 16, right: 16, bottom: 34, left: 64 }
  const values = points.map(point => point[metric])
  const previousValues = previous.slice(0, points.length).map(point => point[metric])
  const max = Math.max(1, ...values, ...(showPrevious ? previousValues : []))
  const x = (index: number) => pad.left + (points.length <= 1 ? (width - pad.left - pad.right) / 2 : index * (width - pad.left - pad.right) / (points.length - 1))
  const y = (value: number) => pad.top + (height - pad.top - pad.bottom) * (1 - value / max)
  const gradient = useId()
  const path = (series: number[]) => smoothPath(series.map((value, index) => [x(index), y(value)]))
  const format = (value: number) => metric === 'netRevenue' ? formatTlShort(value) : formatNumber(value)
  const labelEvery = Math.max(1, Math.ceil(points.length / 8))
  const bucketLabel = (start: string) => { const [, month, day] = start.split('-'); return granularity === 'month' ? `${month}.${start.slice(0, 4)}` : `${day}.${month}` }
  const ticks = [0, 0.25, 0.5, 0.75, 1].map(step => max * step)
  if (!points.length) return <p className="reports-empty">Bu dönemde veri yok.</p>
  const move = (event: ReactMouseEvent<HTMLDivElement>) => {
    if ((event.target as Element).closest('.reports-chart-tip')) return
    const box = event.currentTarget.getBoundingClientRect()
    const px = box.width > 0 ? (event.clientX - box.left) / box.width * width : pad.left
    const index = points.length <= 1 ? 0 : Math.max(0, Math.min(points.length - 1, Math.round((px - pad.left) / (width - pad.left - pad.right) * (points.length - 1))))
    setHover({ index, x: event.clientX, y: event.clientY })
  }
  const point = hover ? points[hover.index] : null
  const before = hover ? previous[hover.index] ?? null : null
  const bucketName = (start: string) => granularity === 'month' ? `${start.slice(5, 7)}.${start.slice(0, 4)}` : granularity === 'week' ? `${formatDay(start)} haftası` : formatDay(start)
  const rows: Array<[string, (item: ReportTimePoint) => string]> = [
    ['Net Ciro', item => formatTl(item.netRevenue)], ['Sipariş', item => formatCount(item.orders)], ['Satılan Adet', item => formatNumber(item.soldQuantity)],
    ['İade Adedi', item => formatNumber(item.returnedQuantity)], ['İptal', item => formatCount(item.cancelledOrders)]
  ]
  return <div ref={wrapRef} className="reports-chart-wrap" onMouseMove={move} onMouseLeave={() => setHover(null)}>
    <svg className="reports-chart" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${metricLabels[metric]} zaman serisi`}>
      <title>{`${metricLabels[metric]}: ${points.map(item => `${bucketLabel(item.start)} ${format(item[metric])}`).join(', ')}`}</title>
      {ticks.map(tick => <g key={tick} className="reports-chart-grid"><line x1={pad.left} x2={width - pad.right} y1={y(tick)} y2={y(tick)} /><text x={pad.left - 8} y={y(tick) + 4} textAnchor="end">{format(tick)}</text></g>)}
      <defs><linearGradient id={gradient} x1="0" x2="0" y1="0" y2="1">
        <stop offset="0%" className="reports-chart-stop" stopOpacity={0.34} /><stop offset="100%" className="reports-chart-stop" stopOpacity={0} />
      </linearGradient></defs>
      {showPrevious && previousValues.length > 0 && <path className="reports-chart-previous" d={path(previousValues)} />}
      {/* Keyed by the measure, so the line draws itself again when another measure is chosen. */}
      <path key={`area-${metric}`} className="reports-chart-area" style={{ fill: `url(#${gradient})` }} d={`${path(values)} L${x(values.length - 1).toFixed(1)},${y(0)} L${x(0).toFixed(1)},${y(0)} Z`} />
      <path key={`line-${metric}`} className="reports-chart-line" pathLength={1} d={path(values)} />
      {points.map((item, index) => <g key={item.start}>
        {points.length <= 40 && <circle className="reports-chart-dot" cx={x(index)} cy={y(item[metric])} r={1.8} />}
        {index % labelEvery === 0 && <text className="reports-chart-x" x={x(index)} y={height - 12} textAnchor="middle">{bucketLabel(item.start)}</text>}
      </g>)}
      {hover && point && <>
        <line className="reports-chart-hover-line" x1={x(hover.index)} x2={x(hover.index)} y1={pad.top} y2={height - pad.bottom} />
        {showPrevious && previousValues[hover.index] !== undefined && <circle className="reports-chart-hover-dot is-previous" cx={x(hover.index)} cy={y(previousValues[hover.index])} r={3} />}
        <circle className="reports-chart-hover-dot" cx={x(hover.index)} cy={y(point[metric])} r={4} />
      </>}
    </svg>
    {hover && point && <div ref={tipRef} className="reports-chart-tip" style={{ visibility: 'hidden' }} role="presentation">
      <b>{bucketName(point.start)}</b>
      <table><thead><tr><th /><th>Bu Dönem</th><th>{before ? bucketName(before.start) : 'Önceki Dönem'}</th></tr></thead>
        <tbody>{rows.map(([label, show]) => <tr key={label}><td>{label}</td><td>{show(point)}</td><td>{before ? show(before) : '—'}</td></tr>)}</tbody></table>
    </div>}
  </div>
}

type OpenDetail = (key: DetailKey) => void

// Genel Bakış, airy like a ledger: four headline tiles, the cost lines across the page, the period's course beside the
// profit funnel, then the platforms' shares and a plain card per area whose "Tümünü Gör" moves to its tab. The
// explanations sit behind the "?" buttons, so the page reads as figures first.
function Overview({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const flow = profitSteps(data)
  return <div className="reports-summary reports-overview">
    <Headline data={data} flow={flow} />
    <DataNotes notes={data.dataNotes} />
    <CostLines data={data} onMore={onMore} />
    <div className="reports-summary-row">
      <TrendPanel data={data} />
      <ProfitFunnel data={data} flow={flow} onMore={onMore} />
    </div>
    <div className="reports-areas">
      <PlatformShare data={data} onMore={onMore} />
      <ProductCard data={data} onMore={onMore} />
      <OrderCard data={data} onMore={onMore} />
      <ReturnCard data={data} onMore={onMore} />
      <CargoCard data={data} onMore={onMore} />
      <InvoiceCard data={data} onMore={onMore} />
      <StockCard data={data} onMore={onMore} />
    </div>
  </div>
}

// Coğrafya: the sales map with its breakdown, the provinces ranked by sales and by returns, and the full city table.
function Geography({ data }: { data: ReportSales }) {
  return <div className="reports-summary">
    <SalesMap data={data} />
    <Cities data={data} />
  </div>
}

// Ürünler: the best sellers, the most returned, brands and categories as bars, then their tables under two sub-tabs.
function ProductsView({ data, sub, onSub }: { data: ReportSales; sub: ProductSub; onSub: (sub: ProductSub) => void }) {
  return <div className="reports-summary">
    <div className="reports-lists is-pairs">
      <Panel title="En Çok Satanlar" description="Satılan adede göre; yanında satış tutarı." className="reports-list">
        <Bars label="En çok satan ürünler" format={formatNumber} items={[...data.topProducts].sort((a, b) => b.soldQuantity - a.soldQuantity).slice(0, 8)
          .map(product => ({ key: product.variantId ?? product.sku, label: product.title, value: product.soldQuantity, note: formatTl(product.salesAmount) }))} />
      </Panel>
      <Panel title="En Çok İade Edilenler" description="İade adedine göre; yanında iade oranı." className="reports-list">
        <Bars label="En çok iade edilen ürünler" format={formatNumber} items={[...data.topReturned].sort((a, b) => b.returnedQuantity - a.returnedQuantity).slice(0, 8)
          .map(product => ({ key: product.variantId ?? product.sku, label: product.title, value: product.returnedQuantity, note: formatPercent(product.returnRate) }))} />
      </Panel>
      <Panel title="Markalar" description="Satılan adede göre." className="reports-list">
        <Bars label="Markalara göre satış" format={formatNumber} items={topGroups(data.brands, group => group.soldQuantity)} />
      </Panel>
      <Panel title="Kategoriler" description="Satılan adede göre." className="reports-list">
        <Bars label="Kategorilere göre satış" format={formatNumber} items={topGroups(data.categories, group => group.soldQuantity)} />
      </Panel>
    </div>
    <SubTabs label="Ürün kırılımı" value={sub} onChange={onSub} items={[
      { value: 'products', label: 'Ürünler', icon: 'products', count: formatCount(data.topProducts.length) },
      { value: 'brands', label: 'Marka ve Kategori', icon: 'layers', count: formatCount(data.brands.filter(group => group.known).length) }
    ]} />
    <div role="tabpanel">{sub === 'brands' ? <BrandsAndCategories data={data} /> : <Products data={data} />}</div>
  </div>
}

// Kârlılık: coverage, profit and commission figures, Trendyol's real costs, and the platforms with their commission.
function ProfitView({ data }: { data: ReportSales }) {
  return <div className="reports-summary">
    <CostAndProfit data={data} />
    <Platforms data={data} />
  </div>
}

// Operasyon: returns, cancellations, cargo, invoices and stock as sub-tabs, each opening with its breakdown as bars.
function OperationsView({ data, sub, onSub }: { data: ReportSales; sub: OpsSub; onSub: (sub: OpsSub) => void }) {
  const now = data.summary
  const packages = data.cargo.reduce((sum, row) => sum + row.packages, 0)
  const invoiced = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const lowStock = data.stock.bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const cancelWarn = (now.cancelRate ?? 0) > data.cancellations.warningRate
  return <div className="reports-summary">
    <SubTabs label="Operasyon kırılımı" value={sub} onChange={onSub} items={[
      { value: 'returns', label: 'İadeler', icon: 'returns', count: formatCount(now.returnClaims) },
      { value: 'cancellations', label: 'İptaller', icon: 'close', count: formatCount(now.cancelledOrders), warn: cancelWarn },
      { value: 'cargo', label: 'Kargo', icon: 'truck', count: formatCount(packages) },
      { value: 'invoices', label: 'Fatura', icon: 'invoice', count: formatCount(invoiced) },
      { value: 'stock', label: 'Stok', icon: 'stock', count: formatCount(lowStock), warn: lowStock > 0 }
    ]} />
    <div role="tabpanel" className="reports-summary">
      {sub === 'returns' && <>
        <div className="reports-lists is-pairs">
          <Panel title="İade Nedenleri" description="Pazaryerinin bildirdiği nedene göre." className="reports-list"><Bars label="İade nedenleri" format={formatCount} items={topReasons(data.returnReasons)} /></Panel>
          <Panel title="İade Gelen İller" description="İade adedine göre; yanında iade oranı." className="reports-list"><Bars label="İllere göre iade" format={formatNumber} items={topGroups(data.cities, group => group.returnedQuantity, group => formatPercent(group.returnRate))} /></Panel>
        </div>
        <Returns data={data} />
      </>}
      {sub === 'cancellations' && <>
        <div className="reports-lists is-pairs">
          <Panel title="İptal Nedenleri" description="Siparişte gelen nedene göre." className="reports-list"><Bars label="İptal nedenleri" format={formatCount} items={topReasons(data.cancellations.reasons)} /></Panel>
          <Panel title="Platformlara Göre İptal Oranı" description={`Uyarı eşiği ${formatPercent(data.cancellations.warningRate)}.`} className="reports-list">
            <Bars label="Platformlara göre iptal oranı" format={value => formatPercent(value)} items={data.cancellations.platforms.filter(row => row.cancelRate !== null)
              .map(row => ({ key: row.key, label: platformName(row.label), value: row.cancelRate ?? 0, note: `${formatCount(row.cancelledOrders)} iptal` }))} />
          </Panel>
        </div>
        <Cancellations data={data} />
      </>}
      {sub === 'cargo' && <>
        <Panel title="Kargo Firmalarının Payı" description="Paket sayısına göre." className="reports-list">
          <Bars label="Kargo firmalarının payı" format={value => formatPercent(value)} items={[...data.cargo].sort((a, b) => b.packages - a.packages)
            .map(row => ({ key: row.label, label: row.label, value: row.share ?? 0, note: `${formatCount(row.packages)} paket`, muted: !row.known }))} />
        </Panel>
        <Cargo data={data} />
      </>}
      {sub === 'invoices' && <>
        <Panel title="Fatura Durumu" description="Paket sayısına göre." className="reports-list">
          <Bars label="Fatura durumu" format={formatCount} items={data.invoices.buckets.map(bucket => ({ key: bucket.key, label: bucket.label, value: bucket.packages, note: formatTl(bucket.amount), muted: bucket.key === 'UNKNOWN' }))} />
        </Panel>
        <Invoices data={data} />
      </>}
      {sub === 'stock' && <Stock data={data} />}
    </div>
  </div>
}

// Müşteriler: who buys (unique and repeat customers) and when (weekday and hour).
function CustomersView({ data }: { data: ReportSales }) {
  return <div className="reports-summary">
    <Customers data={data} />
    <TimeOfDay data={data} />
  </div>
}

// A narrow card shows only the arrow; the button keeps its name for screen readers and the pointer's tooltip.
function MoreButton({ title, onClick, compact = false }: { title: string; onClick: () => void; compact?: boolean }) {
  return <button type="button" className={`secondary reports-more${compact ? ' is-compact' : ''}`} onClick={onClick} aria-label={`${title}: Tümünü Gör`} title={compact ? 'Tümünü Gör' : undefined}>
    {!compact && 'Tümünü Gör'}<UiIcon name="arrowRight" size={14} />
  </button>
}

type FlowStep = { key: string; label: string; value: number | null; kind: 'total' | 'minus' | 'subset'; missing?: string; note?: string }

// Trendyol's account statement when it was read for the period (else null): the commission and cargo of the period's
// orders, real where Trendyol settled or billed them and estimated for the rest, and the store's fees by their date.
function financeCosts(data: ReportSales): ReportCosts | null { return data.costs?.source === 'trendyol-finance' ? data.costs : null }

const feeCategories = [
  { category: 'SERVICE_FEE', key: 'fee', label: 'Hizmet Bedeli' },
  { category: 'STOPPAGE', key: 'stoppage', label: 'Stopaj' },
  { category: 'ADVERTISING', key: 'ads', label: 'Reklam' },
  { category: 'PENALTY', key: 'penalty', label: 'Ceza' },
  { category: 'EARLY_PAYMENT', key: 'early', label: 'Erken Ödeme' }
]

function feeLines(costs: ReportCosts) {
  return feeCategories.flatMap(fee => {
    const line = costs.lines.find(item => item.category === fee.category)
    return line && line.amount !== 0 ? [{ ...fee, value: line.amount }] : []
  })
}

function commissionNote(costs: ReportCosts) {
  const commission = costs.commission
  if (!commission) return ''
  return commission.realOrders > 0
    ? `Gerçek ${formatCount(commission.realOrders)} sipariş ${formatTl(commission.real)} · kalanı tahmini ${formatTl(commission.estimatedForUnsettled)}`
    : 'Hesap kesimi henüz yok; tümü tahmini'
}

function cargoNote(cargo: NonNullable<ReportCosts['cargo']>) {
  const billed = `Fatura ${formatTl(cargo.real)} (${formatCount(cargo.billedOrders)} sipariş)`
  if (cargo.unbilledOrders === 0) return billed
  return cargo.estimated === null
    ? `${billed} · faturası gelmeyen ${formatCount(cargo.unbilledOrders)} siparişin kargosu tahmin edilemedi`
    : `${billed} · faturası gelmeyen ${formatCount(cargo.unbilledOrders)} sipariş tahmini ${formatTl(cargo.estimated)}`
}

// Platforms with orders in the period whose cargo and fees are not read (only Trendyol's statement is).
function unreadFeePlatforms(data: ReportSales) {
  return data.platforms.filter(group => group.known && group.key !== 'TRENDYOL' && group.orders > 0).map(group => platformName(group.key))
}

// Satış Tutarı → Net Ciro → komisyon (and, with Trendyol's statement, cargo and fees) → kâr, from known amounts only.
// Each step says what it leaves out; the profit covers only the sales whose cost (and commission) is known. No steps
// when no order carried an amount.
type ProfitSteps = { steps: FlowStep[] | null; description: string }

function profitSteps(data: ReportSales): ProfitSteps {
  const now = data.summary
  const commission = data.commission
  const cost = data.cost
  const finance = financeCosts(data)
  const unread = unreadFeePlatforms(data)
  const description = finance
    ? `Komisyon ve kargo gerçek tutarla, henüz kesinleşmeyenler tahmini; kesintiler Trendyol cari hesabından.${unread.length ? ` ${unread.join(', ')} kargo ve hizmet bedeli düşülmedi.` : ''}`
    : 'Yalnız okunabilen kalemlerle; kargo, hizmet bedeli, stopaj, reklam ve ceza düşülmedi.'
  if (now.ordersWithAmount === 0) return { steps: null, description }
  const hasCommission = commission.coveredRevenue > 0
  const partialCommission = (commission.coverage ?? 0) < 1
  const steps: FlowStep[] = [
    { key: 'gross', label: 'Satış Tutarı', value: now.grossSales, kind: 'total', note: now.ordersWithoutAmount > 0 ? `İade öncesi; tutarı olmayan ${formatCount(now.ordersWithoutAmount)} sipariş hariç` : 'İptaller hariç, iade öncesi' },
    { key: 'returned', label: 'İade Edilen Tutar', value: now.returnedAmount, kind: 'minus' },
    { key: 'net', label: 'Net Ciro', value: now.netRevenue, kind: 'total' }
  ]
  if (finance?.commission && finance.cargo) {
    const fees = feeLines(finance)
    const feeTotal = fees.reduce((sum, fee) => sum + fee.value, 0)
    const charged = finance.commission.combined + finance.cargo.combined + feeTotal
    steps.push(
      { key: 'commission', label: 'Komisyon', value: finance.commission.combined, kind: 'minus',
        note: `${commissionNote(finance)}${partialCommission ? '; komisyonu bilinmeyen cirodan düşülmedi' : ''}` },
      { key: 'cargo', label: 'Kargo', value: finance.cargo.combined, kind: 'minus', note: cargoNote(finance.cargo) },
      { key: 'fees', label: 'Kesintiler', value: feeTotal, kind: 'minus',
        note: fees.length ? `${fees.map(fee => fee.label).join(', ')} · Trendyol, işlem tarihine göre` : 'Bu dönemde Trendyol kesintisi yok' },
      { key: 'payout', label: 'Masraflar Düşülmüş', value: now.netRevenue - charged, kind: 'total',
        note: finance.cargo.estimated === null && finance.cargo.unbilledOrders > 0 ? 'Faturası gelmeyen kargo eksik; ürün maliyeti hariç' : 'Gerçek ve tahmini kalemlerle; ürün maliyeti hariç' }
    )
  } else steps.push(
    { key: 'commission', label: 'Tahmini Komisyon', value: hasCommission ? commission.estimatedCommission : null, kind: 'minus', missing: 'Komisyon bilinmiyor',
      note: hasCommission ? `Kapsam ${formatPercent(commission.coverage)}${partialCommission ? '; kapsam dışı cirodan komisyon düşülmedi' : ''}` : 'Siparişte komisyon gelmedi, tarife de yüklenmedi' },
    { key: 'payout', label: 'Komisyon Düşülmüş', value: hasCommission ? now.netRevenue - commission.estimatedCommission : null, kind: 'total', missing: '—',
      note: hasCommission ? (partialCommission ? 'Tahmini; gerçek tutar daha düşük olabilir' : 'Tahmini; kesinleşmiş hakediş değil') : undefined }
  )
  if ((cost.coverage ?? 0) === 0 || cost.costedQuantity === 0) steps.push({ key: 'profit', label: 'Tahmini Kâr', value: null, kind: 'subset', missing: 'Maliyet girilmemiş', note: 'Ürün kartında varyant maliyeti girildiğinde kâr burada görünür' })
  else if (cost.profitQuantity > 0) steps.push({ key: 'profit', label: 'Tahmini Net Kâr', value: cost.netProfit, kind: 'subset',
    note: `Yalnız maliyeti ve komisyonu bilinen ${formatNumber(cost.profitQuantity)} adet (${formatPercent(cost.profitCoverage)}, ${formatTl(cost.profitRevenue)} satış) üzerinden · marj ${formatPercent(cost.netMargin)}` })
  else steps.push({ key: 'profit', label: 'Tahmini Brüt Kâr', value: cost.grossProfit, kind: 'subset',
    note: `Komisyon öncesi; yalnız maliyeti bilinen ${formatNumber(cost.costedQuantity)} adet (${formatPercent(cost.coverage)}) üzerinden · marj ${formatPercent(cost.grossMargin)}` })
  return { steps, description }
}

// The flow as a funnel: each running total (sales, net revenue, after the deductions, the profit) a band as wide as
// its share of the sales, narrowing into the next; beside it every step with its amount, the deductions indented. A
// total the data cannot give is drawn as an outline, a negative one in the danger colour, the profit hatched because
// it covers only part of the sales.
function ProfitFunnel({ data, flow, onMore }: { data: ReportSales; flow: ProfitSteps; onMore: OpenDetail }) {
  const more = <MoreButton title="Kâr Hunisi" onClick={() => onMore('cost')} />
  if (!flow.steps) return <Panel title="Kâr Hunisi" info={flow.description} actions={more}><p className="reports-empty">Bu dönemde tutarı olan sipariş yok; huni hesaplanamaz.</p></Panel>
  const stages = flow.steps.filter(step => step.kind !== 'minus')
  const max = Math.max(1, data.summary.grossSales)
  const width = (value: number | null) => value === null ? 100 : Math.max(10, Math.min(100, Math.abs(value) / max * 100))
  const stateClass = (step: FlowStep) => `is-${step.kind}${step.value === null ? ' is-missing' : ''}${step.value !== null && step.value < 0 ? ' is-negative' : ''}`
  return <Panel title="Kâr Hunisi" info={flow.description} actions={more} className="reports-funnel-panel">
    <div className="reports-funnel">
      <ol className="reports-funnel-shape" aria-hidden="true">{stages.map((stage, index) => {
        const top = width(stage.value)
        const bottom = index < stages.length - 1 ? width(stages[index + 1].value) : top * 0.72
        return <li key={stage.key} className={`${stateClass(stage)} is-stage-${index}`} style={{ '--top': `${top}%`, '--bottom': `${bottom}%`, animationDelay: `${index * 110}ms` } as CSSProperties} />
      })}</ol>
      <ol className="reports-flow">{flow.steps.map(step => <li key={step.key} className={stateClass(step)}>
        <span className="reports-flow-label">{step.label}{step.note && <InfoTip label={step.label} text={step.note} />}</span>
        <strong className="reports-flow-value">{step.value === null ? step.missing : `${step.kind === 'minus' ? '− ' : ''}${formatTl(step.value)}`}</strong>
      </li>)}</ol>
    </div>
  </Panel>
}


const unreadCostLines = ['Kargo Faturası', 'Hizmet Bedeli', 'Stopaj', 'Reklam', 'Ceza']

// The cost lines the report can read across the page: a large donut of their shares beside the lines in a roomy grid,
// each with its colour, amount and share; the lines it cannot read are named last, greyed. With Trendyol's statement
// the commission and cargo are real plus estimated and the fees real; without it the commission is an estimate and the
// rest is not read. The shares are among the listed lines only.
function CostLines({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const now = data.summary
  const commission = data.commission
  const cost = data.cost
  const noAmount = now.ordersWithAmount === 0
  const finance = financeCosts(data)
  const unreadPlatforms = finance ? unreadFeePlatforms(data) : []
  const unread = finance ? (unreadPlatforms.length ? `${unreadPlatforms.join(', ')} kargo ve hizmet bedeli` : null) : unreadCostLines.join(', ')
  const lines = [
    ...(finance?.commission && finance.cargo ? [
      { key: 'commission', label: 'Komisyon', value: finance.commission.combined as number | null, missing: 'Bilinmiyor', note: commissionNote(finance) },
      { key: 'cargo', label: 'Kargo', value: finance.cargo.combined as number | null, missing: 'Bilinmiyor', note: cargoNote(finance.cargo) },
      ...feeLines(finance).map(fee => ({ key: fee.key, label: fee.label, value: fee.value as number | null, missing: '', note: 'Trendyol, işlem tarihine göre' }))
    ] : [
      { key: 'commission', label: 'Tahmini Komisyon', value: commission.coveredRevenue > 0 ? commission.estimatedCommission : null, missing: 'Bilinmiyor', note: `Kapsam ${formatPercent(commission.coverage)}` }
    ]),
    { key: 'cost', label: 'Ürün Maliyeti', value: cost.costedQuantity > 0 ? cost.costTotal : null, missing: 'Maliyet girilmemiş', note: cost.costedQuantity > 0 ? `Kapsam ${formatPercent(cost.coverage)} · güncel maliyet` : 'Ürün kartında varyant maliyeti' },
    { key: 'returned', label: 'İade Edilen Tutar', value: noAmount ? null : now.returnedAmount, missing: 'Tutar yok', note: 'Satıştan düşülen' }
  ]
  const total = lines.reduce((sum, line) => sum + Math.max(0, line.value ?? 0), 0)
  let offset = 0
  return <Panel title="Masraf Kalemleri" info={finance ? 'Komisyon ve kargo gerçek ve tahmini toplamdır; kesintiler Trendyol cari hesabından.' : 'Paylar yalnız okunabilen kalemler arasındadır; toplam gider değildir.'}
    actions={<MoreButton title="Masraf Kalemleri" onClick={() => onMore('cost')} />} className="reports-cost-panel">
    <div className="reports-donut-wrap">
      <svg className="reports-donut is-large" viewBox="0 0 128 128" role="img" aria-label={`Bilinen kalemler toplamı ${formatTl(total)}`}>
        <circle className="reports-donut-base" cx="64" cy="64" r="52" />
        {total > 0 && lines.map(line => {
          const share = Math.max(0, line.value ?? 0) / total * 100
          const segment = <circle key={line.key} className={`reports-donut-segment is-${line.key}`} cx="64" cy="64" r="52" pathLength={100} strokeDasharray={`${share} ${100 - share}`} strokeDashoffset={-offset} />
          offset += share
          return segment
        })}
        <text x="64" y="58" textAnchor="middle" className="reports-donut-caption">Bilinen toplam</text>
        <text x="64" y="76" textAnchor="middle" className="reports-donut-total">{total > 0 ? formatTlShort(total) : '—'}</text>
      </svg>
      <ul className="reports-legend">
        {lines.map(line => <li key={line.key} className={line.value === null ? 'is-missing' : undefined}>
          <i className={`is-${line.key}`} aria-hidden="true" />
          <span>{line.label}</span>
          <strong>{line.value === null ? line.missing : formatTl(line.value)}{line.value !== null && total > 0 && <em>{formatPercent(Math.max(0, line.value) / total)}</em>}</strong>
          <small>{line.note}</small>
        </li>)}
        {unread && <li className="is-unread"><i aria-hidden="true" /><span>{finance ? 'Okunmuyor' : 'Henüz okunmuyor'}</span><strong>—</strong><small>{unread}</small></li>}
      </ul>
    </div>
  </Panel>
}

type MetricRow = { label: string; value: string; hint?: string; warn?: boolean; muted?: boolean }

// One area's card as a plain ledger: its icon and title, then label and value rows with room between them.
function MetricCard({ title, icon, rows, onMore }: { title: string; icon: UiIconName; rows: MetricRow[]; onMore: () => void }) {
  return <section className="reports-panel reports-metric">
    <header>
      <div className="reports-metric-title"><span aria-hidden="true"><UiIcon name={icon} size={16} /></span><h2>{title}</h2></div>
      <div className="reports-panel-actions"><MoreButton title={title} onClick={onMore} compact /></div>
    </header>
    <dl>{rows.map(row => <div key={row.label} className={row.warn ? 'is-warn' : row.muted ? 'is-unknown' : undefined}>
      <dt>{row.label}{row.hint && <small>{row.hint}</small>}</dt>
      <dd>{row.value}</dd>
    </div>)}</dl>
  </section>
}

function ProductCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const now = data.summary
  // Per unit only when every order carries its amount; otherwise units without amounts would pull it down.
  const perUnit = now.ordersWithoutAmount === 0 && now.soldQuantity > 0 ? formatTl(now.grossSales / now.soldQuantity) : '—'
  return <MetricCard title="Ürün" icon="products" onMore={() => onMore('products')} rows={[
    { label: 'Satılan Adet', value: formatNumber(now.soldQuantity) },
    { label: 'Elde Kalan Adet', value: formatNumber(now.keptQuantity), hint: 'İade edilmeyen' },
    { label: 'Adet Başına Satış', value: perUnit },
    { label: 'Maliyeti Girilmiş', value: formatPercent(data.cost.coverage), hint: `${formatCount(data.cost.costedVariants)} varyant` }
  ]} />
}

function OrderCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const now = data.summary
  const customers = data.customers
  return <MetricCard title="Sipariş ve Müşteri" icon="users" onMore={() => onMore('customers')} rows={[
    { label: 'Ortalama Sipariş', value: formatTl(now.averageOrderValue), hint: 'Tutarı olan siparişler' },
    { label: 'İptal Oranı', value: formatPercent(now.cancelRate), hint: `${formatCount(now.cancelledOrders)} sipariş · eşik ${formatPercent(data.cancellations.warningRate)}`, warn: (now.cancelRate ?? 0) > data.cancellations.warningRate },
    { label: 'Tekil Müşteri', value: formatCount(customers.uniqueCustomers), hint: `${formatCount(customers.identifiedOrders)} siparişten` },
    { label: 'Tekrar Alım Oranı', value: formatPercent(customers.repeatRate) }
  ]} />
}

function ReturnCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const now = data.summary
  return <MetricCard title="İade" icon="returns" onMore={() => onMore('returns')} rows={[
    { label: 'İade Oranı', value: formatPercent(now.returnRate), hint: 'İade / satılan adet' },
    { label: 'İade Talebi', value: formatCount(now.returnClaims), hint: 'Reddedilenler hariç' },
    { label: 'İade Adedi', value: formatNumber(now.returnedQuantity) },
    { label: 'İade Tutarı', value: now.ordersWithAmount > 0 ? formatTl(now.returnedAmount) : 'Tutar yok' }
  ]} />
}

// Averages over the packages whose times were measured, weighted by that count, so a carrier with one measured
// package does not weigh as much as one with a hundred.
function weightedHours(rows: ReportSales['cargo'], hours: (row: ReportSales['cargo'][number]) => number | null, measured: (row: ReportSales['cargo'][number]) => number) {
  let weight = 0
  let sum = 0
  for (const row of rows) {
    const value = hours(row)
    const count = measured(row)
    if (value === null || count <= 0) continue
    weight += count
    sum += value * count
  }
  return { hours: weight > 0 ? sum / weight : null, measured: weight }
}

function CargoCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const packages = data.cargo.reduce((sum, row) => sum + row.packages, 0)
  const ship = weightedHours(data.cargo, row => row.averageHoursToShip, row => row.measuredShip)
  const deliver = weightedHours(data.cargo, row => row.averageHoursToDeliver, row => row.measuredDeliver)
  const measured = (count: number) => count > 0 ? `${formatCount(count)} pakette ölçüldü` : 'Ölçülemedi'
  const top = [...data.cargo].filter(row => row.known).sort((a, b) => b.packages - a.packages)[0]
  return <MetricCard title="Kargo" icon="truck" onMore={() => onMore('cargo')} rows={[
    { label: 'Paket', value: formatCount(packages) },
    { label: 'Kargo Teslim Alma', value: formatHours(ship.hours), hint: measured(ship.measured) },
    { label: 'Teslimat', value: formatHours(deliver.hours), hint: measured(deliver.measured) },
    { label: 'En Çok Kullanılan', value: top ? top.label : '—', hint: top ? formatPercent(top.share) : undefined }
  ]} />
}

function InvoiceCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const total = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  return <MetricCard title="Fatura" icon="invoice" onMore={() => onMore('invoices')} rows={data.invoices.buckets.map(bucket => ({
    label: bucket.label, value: formatCount(bucket.packages), hint: `${formatPercent(total > 0 ? bucket.packages / total : null)} · ${formatTl(bucket.amount)}`, muted: bucket.key === 'UNKNOWN'
  }))} />
}

function StockCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const bestSellers = data.stock.bestSellers
  const low = bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const unknownStock = bestSellers.filter(row => row.available === null).length
  return <MetricCard title="Stok" icon="stock" onMore={() => onMore('stock')} rows={[
    { label: 'Kalan Günü 7\'den Az', value: formatCount(low), hint: `En çok satan ${formatCount(bestSellers.length)} ürün içinde`, warn: low > 0 },
    { label: 'Satıp Stoğu Biten', value: formatCount(data.stock.soldOut.length), hint: 'Mevcut stoğu sıfır' },
    { label: 'Stoğu Bilinmeyen', value: formatCount(unknownStock), muted: unknownStock === 0 }
  ]} />
}

type BarItem = { key: string; label: string; value: number; muted?: boolean; note?: string }

// The top known groups by a value, with the unknown bucket after them, muted.
function topGroups(groups: ReportGroup[], value: (group: ReportGroup) => number, note?: (group: ReportGroup) => string, limit = 8): BarItem[] {
  const known = groups.filter(group => group.known && value(group) > 0).sort((a, b) => value(b) - value(a)).slice(0, limit)
  const unknown = groups.filter(group => !group.known && value(group) > 0)
  return [...known, ...unknown].map(group => ({ key: group.key, label: group.label, value: value(group), muted: !group.known, note: note?.(group) }))
}

function topReasons(reasons: ReportReason[], limit = 6): BarItem[] {
  const known = reasons.filter(reason => reason.known).sort((a, b) => b.count - a.count).slice(0, limit)
  return [...known, ...reasons.filter(reason => !reason.known)].map(reason => ({ key: reason.reason, label: reason.reason, value: reason.count, muted: !reason.known }))
}

function ListCard({ title, description, detail, onMore, children }: { title: string; description?: string; detail: DetailKey; onMore: OpenDetail; children: ReactNode }) {
  return <Panel title={title} description={description} actions={<MoreButton title={title} onClick={() => onMore(detail)} />} className="reports-list">{children}</Panel>
}

// The platforms' shares of net revenue as a donut in their own colours, each listed with its share and amount.
function PlatformShare({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const rows = data.platforms.filter(group => group.ordersWithAmount > 0 && group.netRevenue > 0).sort((a, b) => b.netRevenue - a.netRevenue)
  const total = rows.reduce((sum, group) => sum + group.netRevenue, 0)
  let offset = 0
  return <ListCard title="Platform Payı" description="Net ciroya göre." detail="platforms" onMore={onMore}>
    {total <= 0 ? <p className="reports-empty">Bu dönemde tutarı olan satış yok.</p> : <div className="reports-share">
      <svg className="reports-donut" viewBox="0 0 128 128" role="img" aria-label={`Platform payı: ${rows.map(group => `${platformName(group.label)} ${formatPercent(group.netRevenue / total)}`).join(', ')}`}>
        <circle className="reports-donut-base" cx="64" cy="64" r="52" />
        {rows.map(group => {
          const share = group.netRevenue / total * 100
          const segment = <circle key={group.key} className="reports-donut-segment" cx="64" cy="64" r="52" style={{ stroke: platformColor(group.key) }} pathLength={100} strokeDasharray={`${share} ${100 - share}`} strokeDashoffset={-offset} />
          offset += share
          return segment
        })}
        <text x="64" y="61" textAnchor="middle" className="reports-donut-total">{formatTlShort(total)}</text>
        <text x="64" y="77" textAnchor="middle" className="reports-donut-caption">{formatCount(rows.length)} platform</text>
      </svg>
      <ul className="reports-share-list">{rows.map(group => <li key={group.key}>
        <i style={{ background: platformColor(group.key) }} aria-hidden="true" /><span>{platformName(group.label)}</span>
        <b>{formatPercent(group.netRevenue / total)}</b><small>{formatTl(group.netRevenue)}</small>
      </li>)}</ul>
    </div>}
    <NoAmountNote platforms={data.platforms} />
  </ListCard>
}

// Platforms that send no amounts have no revenue to draw; their counts are written out instead.
function NoAmountNote({ platforms }: { platforms: ReportGroup[] }) {
  const noAmount = platforms.filter(group => group.ordersWithAmount === 0)
  if (!noAmount.length) return null
  return <p className="reports-note">{noAmount.map(group => `${platformName(group.label)}: tutar yok · ${formatNumber(group.soldQuantity)} adet, ${formatCount(group.orders)} sipariş`).join(' — ')}</p>
}

function commissionCoverage(group: { netRevenue: number; commissionCoveredRevenue: number }) { return group.netRevenue > 0 ? Math.min(1, group.commissionCoveredRevenue / group.netRevenue) : null }

// The columns every order-level bucket shares (platform, store, city, district, brand, category).
function groupColumns(first: Column<ReportGroup>, options: { commission?: boolean; share?: number } = {}): Column<ReportGroup>[] {
  const columns: Column<ReportGroup>[] = [
    first,
    { key: 'orders', header: 'Sipariş', value: row => row.orders, render: row => formatCount(row.orders), numeric: true },
    { key: 'sold', header: 'Satılan Adet', value: row => row.soldQuantity, render: row => formatNumber(row.soldQuantity), numeric: true },
    { key: 'returned', header: 'İade Adedi', value: row => row.returnedQuantity, render: row => formatNumber(row.returnedQuantity), numeric: true },
    { key: 'returnRate', header: 'İade Oranı', value: row => row.returnRate, render: row => formatPercent(row.returnRate), numeric: true },
    { key: 'claims', header: 'İade Talebi', value: row => row.returnClaims, render: row => formatCount(row.returnClaims), numeric: true },
    { key: 'gross', header: 'Satış Tutarı', value: row => row.ordersWithAmount > 0 ? row.grossSales : null, render: row => moneyOrMissing(row.grossSales, row.ordersWithAmount), numeric: true },
    { key: 'net', header: 'Net Ciro', value: row => row.ordersWithAmount > 0 ? row.netRevenue : null, render: row => moneyOrMissing(row.netRevenue, row.ordersWithAmount), numeric: true }
  ]
  if (options.share !== undefined) {
    const total = options.share
    // A bucket without amounts has no share of the revenue to tell, not a share of zero.
    const share = (row: ReportGroup) => total > 0 && row.ordersWithAmount > 0 ? row.netRevenue / total : null
    columns.push({ key: 'share', header: 'Ciro Payı', value: share, render: row => formatPercent(share(row)), numeric: true })
  }
  if (options.commission) columns.push(
    { key: 'commission', header: 'Tahmini Komisyon', value: row => row.commissionCoveredRevenue > 0 ? row.estimatedCommission : null, render: row => row.commissionCoveredRevenue > 0 ? formatTl(row.estimatedCommission) : unknownLabel, numeric: true },
    { key: 'coverage', header: 'Komisyon Kapsamı', value: row => commissionCoverage(row), render: row => formatPercent(commissionCoverage(row)), numeric: true },
    { key: 'payout', header: 'Tahmini Hakediş', value: row => row.commissionCoveredRevenue > 0 ? row.commissionCoveredRevenue - row.estimatedCommission : null,
      render: row => row.commissionCoveredRevenue > 0 ? formatTl(row.commissionCoveredRevenue - row.estimatedCommission) : unknownLabel, numeric: true })
  return columns
}

const labelColumn = (header: string, label: (row: ReportGroup) => string = row => row.label): Column<ReportGroup> => ({ key: 'label', header, value: label, render: row => <span className="reports-cell-label">{label(row)}{row.isTest && <em className="reports-tag">Test</em>}</span> })

function Platforms({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <Panel className="is-wide" title="Platformlara Göre Net Ciro" description="Komisyon ve hakediş tahminidir; kapsam, komisyon bilgisi olan cironun payıdır.">
      {/* A platform without amounts is listed in words, not drawn as a ₺0 bar. */}
      <Bars label="Platformlara göre net ciro" format={formatTl} items={data.platforms.filter(group => group.ordersWithAmount > 0).map(group => ({ key: group.key, label: platformName(group.label), value: group.netRevenue }))} />
      <NoAmountNote platforms={data.platforms} />
    </Panel>
    <div className="is-wide"><DataTable title="Platform Özeti" csvName="rapor-platformlar" rows={data.platforms} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold"
      columns={groupColumns(labelColumn('Platform', row => platformName(row.label)), { commission: true, share: data.summary.netRevenue })}
      expand={row => <DataTable title={`${platformName(row.label)} Mağazaları`} csvName={`rapor-${row.key.toLowerCase()}-magazalar`} rows={row.children} rowKey={child => child.key} initialSort="sold"
        columns={groupColumns(labelColumn('Mağaza'), { commission: true })} />} /></div>
  </div>
}

function Cities({ data }: { data: ReportSales }) {
  const [search, setSearch] = useState('')
  const term = search.trim().toLocaleLowerCase('tr-TR')
  const rows = term ? data.cities.filter(city => city.label.toLocaleLowerCase('tr-TR').includes(term)) : data.cities
  const known = data.cities.filter(city => city.known)
  const total = data.cities.reduce((sum, city) => sum + city.soldQuantity, 0)
  return <div className="reports-grid">
    <Panel title="En Çok Satış Alan 15 İl" description="Satılan adede göre.">
      <Bars label="İllere göre satış" format={formatNumber} items={[...known].sort((a, b) => b.soldQuantity - a.soldQuantity).slice(0, 15).map(city => ({ key: city.key, label: city.label, value: city.soldQuantity }))} />
    </Panel>
    <Panel title="En Çok İade Gelen 15 İl" description="İade adedine göre; yanında iade oranı.">
      <Bars label="İllere göre iade" format={formatNumber} items={[...known].filter(city => city.returnedQuantity > 0).sort((a, b) => b.returnedQuantity - a.returnedQuantity).slice(0, 15)
        .map(city => ({ key: city.key, label: city.label, value: city.returnedQuantity, note: formatPercent(city.returnRate) }))} />
    </Panel>
    <div className="is-wide">
      <div className="reports-search"><UiIcon name="search" size={16} /><input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="İl ara" aria-label="İl ara" /></div>
      <DataTable title="İllere Göre Satış ve İade" description={`Toplam ${formatNumber(total)} adet; adresinde il okunamayan siparişler "Bilinmiyor" satırında. Satırı açınca ilçeler ve platformlar.`}
        csvName="rapor-sehirler" rows={rows} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold" columns={groupColumns(labelColumn('İl'))}
        expand={row => <div className="reports-split">
          <DataTable title={`${row.label} İlçeleri`} csvName={`rapor-${row.key.toLowerCase()}-ilceler`} rows={row.children} rowKey={child => child.key} unknown={child => !child.known} initialSort="sold" columns={groupColumns(labelColumn('İlçe'))} />
          <DataTable title={`${row.label} Platformları`} csvName={`rapor-${row.key.toLowerCase()}-platformlar`} rows={row.split} rowKey={child => child.key} initialSort="sold" columns={groupColumns(labelColumn('Platform', child => platformName(child.label)))} />
        </div>} />
    </div>
  </div>
}

function productColumns(): Column<ReportProduct>[] {
  return [
    { key: 'title', header: 'Ürün', value: row => row.title, render: row => <span className="reports-product"><strong>{row.title}</strong><small>{row.sku}</small></span> },
    { key: 'brand', header: 'Marka', value: row => row.brand },
    { key: 'sold', header: 'Satılan Adet', value: row => row.soldQuantity, render: row => formatNumber(row.soldQuantity), numeric: true },
    { key: 'returned', header: 'İade Adedi', value: row => row.returnedQuantity, render: row => formatNumber(row.returnedQuantity), numeric: true },
    { key: 'returnRate', header: 'İade Oranı', value: row => row.returnRate, render: row => formatPercent(row.returnRate), numeric: true },
    { key: 'sales', header: 'Satış Tutarı', value: row => row.salesAmount, render: row => formatTl(row.salesAmount), numeric: true },
    { key: 'commission', header: 'Tahmini Komisyon', value: row => row.commissionCoveredAmount > 0 ? row.estimatedCommission : null, render: row => row.commissionCoveredAmount > 0 ? formatTl(row.estimatedCommission) : unknownLabel, numeric: true }
  ]
}

function Products({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><DataTable title="En Çok Satan 20 Ürün" description="Varyant bazında; satış tutarı, siparişte ödenen tutarın satıra düşen payıdır." csvName="rapor-en-cok-satanlar" rows={data.topProducts} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={productColumns()} /></div>
    <div className="is-wide"><DataTable title="En Çok İade Edilen 20 Ürün" csvName="rapor-en-cok-iade" rows={data.topReturned} rowKey={row => row.variantId ?? row.sku} initialSort="returned" columns={productColumns()} empty="Bu dönemde iade yok." /></div>
  </div>
}

function BrandsAndCategories({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><DataTable title="Markalar" description="Ürün kartındaki markaya göre; panelde eşleşmeyen satırlar Bilinmiyor." csvName="rapor-markalar" rows={data.brands} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold" columns={groupColumns(labelColumn('Marka'), { commission: true })} /></div>
    <div className="is-wide"><DataTable title="Kategoriler" csvName="rapor-kategoriler" rows={data.categories} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold" columns={groupColumns(labelColumn('Kategori'), { commission: true })} /></div>
  </div>
}

function reasonColumns(total: number, header: string): Column<ReportReason>[] {
  return [
    { key: 'reason', header, value: row => row.reason },
    { key: 'count', header: 'Adet', value: row => row.count, render: row => formatCount(row.count), numeric: true },
    { key: 'share', header: 'Pay', value: row => total > 0 ? row.count / total : null, render: row => formatPercent(total > 0 ? row.count / total : null), numeric: true }
  ]
}

function Returns({ data }: { data: ReportSales }) {
  const claims = data.returnReasons.reduce((sum, reason) => sum + reason.count, 0)
  const byCity = data.cities.filter(city => city.returnedQuantity > 0 || city.returnClaims > 0)
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="İade Talebi" value={formatCount(data.summary.returnClaims)} hint="Reddedilen ve iptal edilenler hariç" />
      <Stat label="İade Adedi" value={formatNumber(data.summary.returnedQuantity)} hint="Kabul edilen iadeler" />
      <Stat label="İade Oranı" value={formatPercent(data.summary.returnRate)} hint="İade / satılan adet" />
      <Stat label="İade Tutarı" value={data.summary.ordersWithAmount > 0 ? formatTl(data.summary.returnedAmount) : 'Tutar yok'} hint="Satıştan düşülen" />
    </div>
    <div className="is-wide"><DataTable title="İade Nedenleri" description="Pazaryerinin bildirdiği nedene göre; neden yoksa Bilinmiyor." csvName="rapor-iade-nedenleri" rows={data.returnReasons} rowKey={row => row.reason} unknown={row => !row.known} initialSort="count" columns={reasonColumns(claims, 'Neden')} empty="Bu dönemde iade talebi yok." /></div>
    <div className="is-wide"><DataTable title="İllere Göre İadeler" csvName="rapor-iade-sehirler" rows={byCity} rowKey={row => row.key} unknown={row => !row.known} initialSort="returned" columns={groupColumns(labelColumn('İl'))} empty="Bu dönemde iade yok." /></div>
  </div>
}

function Stat({ label, value, hint, warn = false }: { label: string; value: string; hint?: string; warn?: boolean }) {
  return <article className={`reports-kpi is-compact${warn ? ' is-warn' : ''}`}><small>{label}</small><strong>{value}</strong>{hint && <span className="reports-kpi-foot"><span>{hint}</span></span>}</article>
}

function Cancellations({ data }: { data: ReportSales }) {
  const cancellations = data.cancellations
  const warn = (rate: number | null) => (rate ?? 0) > cancellations.warningRate
  const reasons = cancellations.reasons.reduce((sum, reason) => sum + reason.count, 0)
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="İptal Edilen Sipariş" value={formatCount(cancellations.cancelledOrders)} />
      <Stat label="İptal Edilen Adet" value={formatNumber(cancellations.cancelledQuantity)} hint="Kısmi iptaller dahil" />
      <Stat label="İptal Oranı" value={formatPercent(cancellations.cancelRate)} warn={warn(cancellations.cancelRate)} hint={`Uyarı eşiği ${formatPercent(cancellations.warningRate)}`} />
    </div>
    {warn(cancellations.cancelRate) && <p className="reports-warning is-wide" role="alert"><UiIcon name="alert" size={16} />İptal oranı {formatPercent(cancellations.warningRate)} eşiğinin üstünde; Trendyol satıcı puanı yönergesi bu oranı izler.</p>}
    <div className="is-wide"><DataTable title="Platformlara Göre İptaller" csvName="rapor-iptaller" rows={cancellations.platforms} rowKey={row => row.key} initialSort="cancelled" columns={[
      { key: 'label', header: 'Platform', value: row => platformName(row.label) },
      { key: 'orders', header: 'Toplam Sipariş', value: row => row.orders, render: row => formatCount(row.orders), numeric: true },
      { key: 'cancelled', header: 'İptal', value: row => row.cancelledOrders, render: row => formatCount(row.cancelledOrders), numeric: true },
      { key: 'quantity', header: 'İptal Adedi', value: row => row.cancelledQuantity, render: row => formatNumber(row.cancelledQuantity), numeric: true },
      { key: 'rate', header: 'İptal Oranı', value: row => row.cancelRate, render: row => <span className={warn(row.cancelRate) ? 'reports-badge is-warn' : undefined}>{formatPercent(row.cancelRate)}</span>, numeric: true }
    ]} empty="Bu dönemde iptal yok." /></div>
    <div className="is-wide"><DataTable title="İptal Nedenleri" description="Siparişte gelen iptal nedenine göre; neden gelmeyenler Bilinmiyor." csvName="rapor-iptal-nedenleri" rows={cancellations.reasons} rowKey={row => row.reason} unknown={row => !row.known} initialSort="count" columns={reasonColumns(reasons, 'Neden')} empty="Bu dönemde iptal yok." /></div>
  </div>
}

function Cargo({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><DataTable title="Kargo Firmaları" description="Süreler pazaryerinin bildirdiği durum zamanlarından hesaplanır (yaklaşık); ölçülen paket sayısı yanında. Teslim alma, kargo firmasının paketi okuttuğu andır; satıcının paketi bıraktığı an değildir." csvName="rapor-kargo"
      rows={data.cargo} rowKey={row => row.label} unknown={row => !row.known} initialSort="packages" columns={[
        { key: 'label', header: 'Kargo Firması', value: row => row.label },
        { key: 'packages', header: 'Paket', value: row => row.packages, render: row => formatCount(row.packages), numeric: true },
        { key: 'share', header: 'Pay', value: row => row.share, render: row => formatPercent(row.share), numeric: true },
        { key: 'ship', header: 'Kargo Teslim Alma', value: row => row.averageHoursToShip, render: row => <Measured value={formatHours(row.averageHoursToShip)} count={row.measuredShip} />, numeric: true, csv: row => row.averageHoursToShip },
        { key: 'deliver', header: 'Teslimat', value: row => row.averageHoursToDeliver, render: row => <Measured value={formatHours(row.averageHoursToDeliver)} count={row.measuredDeliver} />, numeric: true, csv: row => row.averageHoursToDeliver }
        // No "late shipment" column: the shipped time is the carrier's scan, which can fall after the seller's deadline
        // although the package was handed over in time, so a late count would overstate (live check 23 Oct 2026).
      ]} empty="Bu dönemde paket yok." /></div>
  </div>
}

function Measured({ value, count }: { value: string; count: number }) {
  return <span className="reports-measured">{value}<small>{count > 0 ? `${formatCount(count)} pakette ölçüldü` : 'ölçülemedi'}</small></span>
}

const weekdays = ['Pazartesi', 'Salı', 'Çarşamba', 'Perşembe', 'Cuma', 'Cumartesi', 'Pazar']

// The busiest weekday and hour of a 7×24 order heatmap, with their order counts.
function peaks(heatmap: number[][]) {
  const byDay = heatmap.map(row => row.reduce((sum, value) => sum + value, 0))
  const byHour = Array.from({ length: 24 }, (_, hour) => heatmap.reduce((sum, row) => sum + (row[hour] ?? 0), 0))
  const day = byDay.indexOf(Math.max(...byDay))
  const hour = byHour.indexOf(Math.max(...byHour))
  return { day, dayOrders: byDay[day] ?? 0, hour, hourOrders: byHour[hour] ?? 0 }
}
const hourRange = (hour: number) => `${String(hour).padStart(2, '0')}:00–${String(hour + 1).padStart(2, '0')}:00`

function Heatmap({ heatmap, compact = false }: { heatmap: number[][]; compact?: boolean }) {
  const max = Math.max(1, ...heatmap.flat())
  const every = compact ? 6 : 3
  return <div className="reports-heatmap-wrap"><table className={`reports-heatmap${compact ? ' is-compact' : ''}`}>
    <thead><tr><th scope="col"><span className="sr-only">Gün</span></th>{Array.from({ length: 24 }, (_, hour) => <th key={hour} scope="col">{hour % every === 0 ? String(hour).padStart(2, '0') : ''}</th>)}</tr></thead>
    <tbody>{heatmap.map((row, day) => <tr key={weekdays[day]}><th scope="row">{weekdays[day].slice(0, 3)}</th>{row.map((value, hour) =>
      <td key={hour} style={{ '--heat': value / max } as CSSProperties} title={`${weekdays[day]} ${String(hour).padStart(2, '0')}:00 · ${formatCount(value)} sipariş`}><span className="sr-only">{value}</span></td>)}</tr>)}</tbody>
  </table></div>
}

function TimeOfDay({ data }: { data: ReportSales }) {
  const { day: peakDay, dayOrders, hour: peakHour, hourOrders } = peaks(data.heatmap)
  const csv = () => downloadCsv('rapor-saat-gun.csv', toCsv(data.heatmap.map((row, day) => ({ day, row })), [
    { header: 'Gün', value: item => weekdays[item.day] }, ...Array.from({ length: 24 }, (_, hour): CsvColumn<{ day: number; row: number[] }> => ({ header: `${String(hour).padStart(2, '0')}:00`, value: item => item.row[hour] ?? 0 }))
  ]))
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="En Yoğun Gün" value={data.summary.orders > 0 ? weekdays[peakDay] : '—'} hint={data.summary.orders > 0 ? `${formatCount(dayOrders)} sipariş` : undefined} />
      <Stat label="En Yoğun Saat" value={data.summary.orders > 0 ? hourRange(peakHour) : '—'} hint={data.summary.orders > 0 ? `${formatCount(hourOrders)} sipariş` : undefined} />
    </div>
    <Panel className="is-wide" title="Gün ve Saate Göre Siparişler" description="İstanbul saatiyle; iptaller hariç. Koyu hücre daha çok sipariş." actions={<button type="button" className="secondary reports-csv" onClick={csv}><UiIcon name="download" size={16} />CSV</button>}>
      <Heatmap heatmap={data.heatmap} />
    </Panel>
  </div>
}

function Customers({ data }: { data: ReportSales }) {
  const customers = data.customers
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="Tekil Müşteri" value={formatCount(customers.uniqueCustomers)} hint={`${formatCount(customers.identifiedOrders)} siparişten`} />
      <Stat label="Tekrar Alan Müşteri" value={formatCount(customers.repeatCustomers)} hint="Dönemde birden çok sipariş" />
      <Stat label="Tekrar Alım Oranı" value={formatPercent(customers.repeatRate)} />
      <Stat label="Müşteri Başına Sipariş" value={formatNumber(customers.ordersPerCustomer)} />
    </div>
    <p className="reports-note is-wide"><UiIcon name="info" size={16} />Müşteriler, mağazanın müşteri numarası, e-posta ya da telefondan veritabanında üretilen tek yönlü bir anahtarla sayılır; kişisel bilgi rapora gelmez.
      Kimliği okunamayan {formatCount(customers.unidentifiedOrders)} sipariş bu sayılara girmez. Aynı kişi iki pazaryerinde iki müşteri sayılabilir.</p>
  </div>
}

function stockColumns(days: number): Column<ReportStockRow>[] {
  return [
    { key: 'title', header: 'Ürün', value: row => row.title, render: row => <span className="reports-product"><strong>{row.title}</strong><small>{row.sku}</small></span> },
    { key: 'sold', header: 'Elde Kalan Satış', value: row => row.soldQuantity, render: row => formatNumber(row.soldQuantity), numeric: true },
    { key: 'daily', header: `Günlük Ortalama (${days} gün)`, value: row => row.dailyAverage, render: row => formatNumber(row.dailyAverage), numeric: true },
    { key: 'available', header: 'Mevcut Stok', value: row => row.available, render: row => row.available === null ? unknownLabel : formatNumber(row.available), numeric: true },
    { key: 'days', header: 'Kalan Gün', value: row => row.daysLeft, render: row => row.daysLeft === null ? '—' : <span className={row.daysLeft < 7 ? 'reports-badge is-warn' : undefined}>{formatNumber(row.daysLeft)}</span>, numeric: true }
  ]
}

function Stock({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><DataTable title="En Çok Satanların Stoğu" description="Ana depodaki kullanılabilir stok (şu an) ve dönemin günlük satışına göre kalan gün; 7 günden az olanlar işaretli." csvName="rapor-stok" rows={data.stock.bestSellers} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={stockColumns(data.stock.periodDays)} /></div>
    <div className="is-wide"><DataTable title="Satıp Stoğu Biten Ürünler" csvName="rapor-stok-biten" rows={data.stock.soldOut} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={stockColumns(data.stock.periodDays)} empty="Dönemde satılıp stoğu sıfır olan ürün yok." /></div>
  </div>
}

function CostAndProfit({ data }: { data: ReportSales }) {
  const cost = data.cost
  const commission = data.commission
  return <div className="reports-grid">
    <Panel className="is-wide" title="Kapsam" description="Kâr yalnız maliyeti (ve net kâr için ayrıca komisyonu) bilinen satışlar üzerinden hesaplanır.">
      <div className="reports-coverage">
        <Coverage label="Maliyeti Girilmiş Satış" rate={cost.coverage} detail={`${formatNumber(cost.costedQuantity)} / ${formatNumber(cost.keptQuantity)} adet · ${formatCount(cost.costedVariants)} varyant`} />
        <Coverage label="Komisyon Bilgisi Olan Ciro" rate={commission.coverage} detail={`${formatTl(commission.coveredRevenue)} / ${formatTl(data.summary.netRevenue)}`} />
        <Coverage label="Maliyet ve Komisyonu Olan Satış" rate={cost.profitCoverage} detail={`${formatNumber(cost.profitQuantity)} adet`} />
      </div>
      {(cost.coverage ?? 0) < 1 && <p className="reports-note"><UiIcon name="info" size={16} />Maliyeti eksik ürünlerin maliyetini ürün kartındaki Maliyet alanına (varyant tablosunda) girerek kapsamı artırabilirsiniz.</p>}
    </Panel>
    <div className="reports-stat-row is-wide">
      <Stat label="Maliyetli Satış Tutarı" value={cost.costedQuantity > 0 ? formatTl(cost.costedRevenue) : '—'} />
      <Stat label="Toplam Maliyet" value={cost.costedQuantity > 0 ? formatTl(cost.costTotal) : '—'} hint="Güncel maliyet" />
      <Stat label="Brüt Kâr" value={cost.costedQuantity > 0 ? formatTl(cost.grossProfit) : '—'} hint={`Marj ${formatPercent(cost.grossMargin)} · komisyon öncesi`} />
      <Stat label="Tahmini Net Kâr" value={cost.profitQuantity > 0 ? formatTl(cost.netProfit) : '—'} hint={`Marj ${formatPercent(cost.netMargin)} · komisyon sonrası`} />
    </div>
    <div className="reports-stat-row is-wide">
      <Stat label="Tahmini Komisyon" value={commission.coveredRevenue > 0 ? formatTl(commission.estimatedCommission) : '—'} hint={`Etkin oran ${formatPercent(commission.effectiveRate)}`} />
      <Stat label="Tahmini Hakediş" value={commission.coveredRevenue > 0 ? formatTl(commission.estimatedPayout) : '—'} hint="Kesinleşmiş hakediş değildir" />
      <Stat label="Net Kârdaki Komisyon" value={cost.profitQuantity > 0 ? formatTl(cost.profitCommission) : '—'} />
    </div>
    <TrendyolCosts data={data} />
  </div>
}

type CostLine = ReportCosts['lines'][number]

// What Trendyol's account statement charged in the period, line by line: the part tied to the period's orders and the
// part counted by its own date. Sales, discounts and coupons are listed for reference (negative: paid to the seller).
function TrendyolCosts({ data }: { data: ReportSales }) {
  const finance = financeCosts(data)
  if (!finance?.commission || !finance.cargo) return null
  const { commission, cargo } = finance
  return <>
    <div className="reports-stat-row is-wide">
      <Stat label="Komisyon" value={formatTl(commission.combined)} hint={commissionNote(finance)} />
      <Stat label="Hesap Kesimi Olan Sipariş" value={`${formatCount(finance.settledOrders)} / ${formatCount(finance.financeOrders)}`} hint={`Aynı siparişlerin tahmini ${formatTl(commission.estimatedForSettled)}`} />
      <Stat label="Kargo" value={formatTl(cargo.combined)} hint={cargoNote(cargo)} warn={cargo.estimated === null && cargo.unbilledOrders > 0} />
      <Stat label="Sipariş Başına Kargo" value={cargo.averagePerOrder === null ? '—' : formatTl(cargo.averagePerOrder)} hint={`Son 60 günün ${formatCount(cargo.sampleOrders)} faturalı siparişi`} />
    </div>
    <div className="is-wide"><DataTable<CostLine> title="Trendyol Cari Hesap Kalemleri" csvName="rapor-trendyol-masraf" rows={finance.lines} rowKey={row => row.category} initialSort="amount"
      description="Siparişe bağlı kalemler siparişin dönemine, bağlı olmayanlar işlem tarihine göre; eksi tutar satıcıya ödenendir."
      columns={[
        { key: 'label', header: 'Kalem', value: row => row.label, render: row => <span className="reports-cell-label">{row.label}{!row.isCost && <em className="reports-tag">Masraf Değil</em>}</span> },
        { key: 'amount', header: 'Tutar', value: row => row.amount, render: row => formatTl(row.amount), numeric: true },
        { key: 'linked', header: 'Siparişe Bağlı', value: row => row.orderLinked, render: row => formatTl(row.orderLinked), numeric: true },
        { key: 'unlinked', header: 'Tarihe Göre', value: row => row.unlinked, render: row => formatTl(row.unlinked), numeric: true },
        { key: 'records', header: 'Kayıt', value: row => row.records, render: row => formatCount(row.records), numeric: true }
      ]} /></div>
  </>
}

function Coverage({ label, rate, detail }: { label: string; rate: number | null; detail: string }) {
  const value = Math.max(0, Math.min(1, rate ?? 0))
  return <div className="reports-coverage-item">
    <span>{label}</span>
    <strong>{formatPercent(rate)}</strong>
    <span className="reports-bar-track" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(value * 100)}><span style={{ width: `${value * 100}%` }} /></span>
    <small>{detail}</small>
  </div>
}

function Invoices({ data }: { data: ReportSales }) {
  const total = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">{data.invoices.buckets.map(bucket => <Stat key={bucket.key} label={bucket.label} value={formatCount(bucket.packages)} hint={`${formatPercent(total > 0 ? bucket.packages / total : null)} · ${formatTl(bucket.amount)}`} />)}</div>
    <p className="reports-note is-wide"><UiIcon name="info" size={16} />Paketler, panelde kesilmiş fatura, elle seçilen durum ya da pazaryerinin bildirdiği duruma göre ayrılır; bunların hiçbiri yoksa Bilinmiyor sayılır. Ayrıntı için Faturalar sayfası.</p>
    <div className="is-wide"><DataTable title="Platformlara Göre Fatura Durumu" csvName="rapor-fatura" rows={data.invoices.platforms} rowKey={row => row.platformCode} initialSort="invoiced" columns={[
      { key: 'platform', header: 'Platform', value: row => platformName(row.platformCode) },
      { key: 'invoiced', header: 'Faturalı', value: row => row.invoiced, render: row => formatCount(row.invoiced), numeric: true },
      { key: 'notInvoiced', header: 'Faturasız', value: row => row.notInvoiced, render: row => formatCount(row.notInvoiced), numeric: true },
      { key: 'unknown', header: unknownLabel, value: row => row.unknown, render: row => formatCount(row.unknown), numeric: true }
    ]} empty="Bu dönemde paket yok." /></div>
  </div>
}
