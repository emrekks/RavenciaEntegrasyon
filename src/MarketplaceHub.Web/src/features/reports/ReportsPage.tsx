import { Fragment, useId, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { flushSync } from 'react-dom'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, UiIcon, type UiIconName } from '../../shared/components'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformMultiSelect } from '../../shared/platform-multi-select'
import { ViewTabsWithMore } from '../../shared/view-tabs'
import {
  change, downloadCsv, formatCount, formatDay, formatHours, formatNumber, formatPercent, formatTl, formatTlShort, formatTlWhole, moneyOrMissing, platformColor, platformName, reportQuery, sortRows, toCsv, unknownLabel,
  type Change, type CsvColumn, type ReportCosts, type ReportGroup, type ReportProduct, type ReportReason, type ReportSales, type ReportStockRow
} from './report-format'
import { CommissionTariffsPanel, commissionTariffPlatforms } from '../marketplace/integrations/CommissionTariffsPanel'
import { SalesMap } from './SalesMap'
import { Columns, CountUp, HalfGauge, InfoTip, Progress, StackedBar, useReveal, type ColumnPoint, type Part } from './report-visuals'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { thumbnailUrl } from '../../shared/product-thumbnail'
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

// The periods one click away above the reports, each worked out from today (weeks start on Monday). Any other period
// is picked in the date picker beside them ("Özel").
function periodPresets() {
  const today = new Date()
  const at = (year: number, month: number, day: number) => dayKey(new Date(year, month, day))
  const y = today.getFullYear(), m = today.getMonth(), d = today.getDate()
  const monday = d - (today.getDay() + 6) % 7
  return [
    { key: 'today', label: 'Bugün', range: { from: at(y, m, d), to: at(y, m, d) } },
    { key: 'yesterday', label: 'Dün', range: { from: at(y, m, d - 1), to: at(y, m, d - 1) } },
    { key: 'week', label: 'Bu Hafta', range: { from: at(y, m, monday), to: at(y, m, d) } },
    { key: 'last-week', label: 'Geçen Hafta', range: { from: at(y, m, monday - 7), to: at(y, m, monday - 1) } },
    { key: 'month', label: 'Bu Ay', range: { from: at(y, m, 1), to: at(y, m, d) } },
    { key: 'last-month', label: 'Geçen Ay', range: { from: at(y, m - 1, 1), to: at(y, m, 0) } },
    { key: '30', label: 'Son 30 Gün', range: lastDays(30) },
    { key: 'year', label: 'Bu Yıl', range: { from: at(y, 0, 1), to: at(y, m, d) } }
  ]
}

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
  const presets = periodPresets()
  // The preset the range matches, if any; another range is a custom one, shown on the picker.
  const activePreset = presets.find(preset => preset.range.from === range.from && preset.range.to === range.to)?.key ?? null
  const [platforms, setPlatforms] = useState<string[] | null>(null)
  const [granularity, setGranularity] = useState('')
  const [includeTest, setIncludeTest] = useState(false)
  const query = reportQuery({ from: range.from, to: range.to, platforms, granularity, includeTest })
  const report = useQuery({ queryKey: ['reports-sales', query], queryFn: () => hubApi<ReportSales>(`/reports/sales?${query}`), staleTime: 60_000, placeholderData: previous => previous })
  const data = report.data
  const platformOptions = useMemo(() => [...new Set((data?.channels ?? []).map(channel => channel.platformCode))].map(code => ({ value: code, label: platformName(code) })), [data?.channels])
  // A tab change cross-fades the body where the browser has view transitions (and motion is welcome); elsewhere it
  // simply switches.
  const go = (nextView: ViewKey, nextSub: string | null = null) => {
    const apply = () => setSearchParams(current => {
      const next = new URLSearchParams(current)
      next.delete('detail')
      if (nextView === 'overview') next.delete('view'); else next.set('view', nextView)
      if (nextSub) next.set('sub', nextSub); else next.delete('sub')
      return next
    }, { replace: true })
    const transition = (document as Document & { startViewTransition?: (update: () => void) => unknown }).startViewTransition
    if (transition && !window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) transition.call(document, () => flushSync(apply))
    else apply()
  }
  // "Tümünü Gör": the area's tab, scrolled to the tabs so the breakdown starts at the top.
  const openDetail = (key: DetailKey) => { go(detailHome[key].view, detailHome[key].sub ?? null); body.current?.scrollIntoView?.({ block: 'start', behavior: 'smooth' }) }

  return <section className="content reports-page">
    <div className="page-heading reports-heading">
      <div><p className="eyebrow">Analiz</p><h1>Raporlar</h1><p className="reports-lead">Satış, iade, iptal, kargo ve kârlılık; platform, şehir ve ürün kırılımıyla, önceki dönemle karşılaştırmalı.</p></div>    </div>
    <div className="reports-filters" role="search" aria-label="Rapor filtreleri">
      <div className="reports-field reports-period-field"><span>Dönem</span><div className="reports-period-row">
        <div className="reports-segmented" role="group" aria-label="Hazır dönemler">{presets.map(preset => <button key={preset.key} type="button" aria-pressed={preset.key === activePreset} onClick={() => setRange(preset.range)}>{preset.label}</button>)}</div>
        <div className={`reports-period-custom${activePreset ? '' : ' is-active'}`}><DateRangePicker from={range.from} to={range.to} onChange={next => { if (next.from && next.to) setRange({ from: next.from, to: next.to }) }} label="Özel dönem" /></div>
      </div></div>
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

// A change is good or bad by its direction; for returns and cancellations, up is bad.
function Delta({ delta, invert = false }: { delta: Change | null | undefined; invert?: boolean }) {
  if (!delta) return <em className="reports-delta is-flat" title="Önceki dönemde veri yok">—</em>
  const tone = delta.direction === 'flat' ? 'flat' : (delta.direction === 'up') !== invert ? 'good' : 'bad'
  return <em className={`reports-delta is-${tone}`} title="Önceki döneme göre">{delta.direction === 'up' ? '▲' : delta.direction === 'down' ? '▼' : '■'} {delta.text}</em>
}

// A panel's explanation is either written under its title or, on the overview, kept behind a "?" beside it.
function Panel({ title, description, info, actions, children, className }: { title: string; description?: string; info?: string; actions?: ReactNode; children: ReactNode; className?: string }) {
  return <section className={`reports-panel${className ? ` ${className}` : ''}`}>
    <header><div>{info ? <span className="reports-panel-title"><h2>{title}</h2><InfoTip label={title} text={info} /></span> : <h2>{title}</h2>}{description && <p>{description}</p>}</div>{actions && <div className="reports-panel-actions">{actions}</div>}</header>
    {children}
  </section>
}

// A panel whose chart plays once, when the panel first comes into view.
function RevealPanel({ title, description, actions, className, children }: { title: string; description?: string; actions?: ReactNode; className?: string; children: (shown: boolean) => ReactNode }) {
  const [ref, shown] = useReveal<HTMLDivElement>()
  return <Panel title={title} description={description} actions={actions} className={className}><div ref={ref} className="reports-reveal-body">{children(shown)}</div></Panel>
}

type Column<T> = { key: string; header: string; value: (row: T) => number | string | null; render?: (row: T) => ReactNode; numeric?: boolean; csv?: (row: T) => string | number | null }

// A table with sortable columns, the unknown bucket last and muted, an optional row expansion and its CSV export.
function DataTable<T>({ title, description, rows, columns, rowKey, unknown, expand, csvName, empty = 'Bu dönemde kayıt yok.', initialSort, footer, toolbar }: {
  title: string; description?: string; rows: T[]; columns: Column<T>[]; rowKey: (row: T) => string; unknown?: (row: T) => boolean
  expand?: (row: T) => ReactNode; csvName: string; empty?: string; initialSort?: string; footer?: ReactNode; toolbar?: ReactNode
}) {
  const [sort, setSort] = useState<{ key: string; direction: 'asc' | 'desc' }>(() => ({ key: initialSort ?? '', direction: 'desc' }))
  const [open, setOpen] = useState<string | null>(null)
  const isUnknown = unknown ?? (() => false)
  const column = columns.find(item => item.key === sort.key)
  const sorted = column ? sortRows(rows, column.value, sort.direction, isUnknown) : sortRows(rows, () => 0, 'desc', isUnknown)
  const exportCsv = () => downloadCsv(`${csvName}.csv`, toCsv(sorted, columns.map<CsvColumn<T>>(item => ({ header: item.header, value: item.csv ?? item.value }))))
  return <Panel title={title} description={description} actions={<button type="button" className="secondary reports-csv" onClick={exportCsv} disabled={!rows.length}><UiIcon name="download" size={16} />CSV</button>}>
    {toolbar}
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

// Horizontal bars with the value written out; the unknown bucket is muted. The bars fill once, one after another, when
// the list first comes into view.
function Bars({ items, format, label }: { items: Array<{ key: string; label: string; value: number; muted?: boolean; note?: string }>; format: (value: number) => string; label: string }) {
  const [ref, shown] = useReveal<HTMLUListElement>()
  const max = Math.max(0, ...items.map(item => item.value))
  if (!items.length || max <= 0) return <p className="reports-empty">Gösterilecek değer yok.</p>
  return <ul ref={ref} className="reports-bars" aria-label={label}>{items.map((item, index) => <li key={item.key} className={item.muted ? 'is-unknown' : undefined}>
    <span className="reports-bar-label" title={item.label}>{item.label}</span>
    <span className="reports-bar-track" aria-hidden="true"><span style={{ width: shown ? `${Math.max(2, item.value / max * 100)}%` : '0%', transitionDelay: `${index * 60}ms` }} /></span>
    <span className="reports-bar-value">{format(item.value)}{item.note && <small>{item.note}</small>}</span>
  </li>)}</ul>
}

type OpenDetail = (key: DetailKey) => void

// Genel Bakış as a calm finance board: the revenue columns with the period's other figures beside them, then the way
// from the sales to the profit, whose steps open their own breakdowns; the period and the operations run down the side.
// Each card plays its animation once, when it first comes into view.
function Overview({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const flow = profitSteps(data)
  const money = moneyBreakdown(data, flow)
  return <div className="reports-board">
    <div className="reports-board-main">
      <RevenueCard data={data} />
      <ProfitJourney data={data} flow={flow} money={money} onMore={onMore} />
    </div>
    <aside className="reports-board-side" aria-label="Dönem özeti ve operasyon">
      <PeriodSummaryCard data={data} />
      <OperationsCard data={data} onMore={onMore} />
    </aside>
  </div>
}

// A board card: its title (with the "?" when it has an explanation), its actions, and its content, which learns when
// the card first came into view so its chart can play then.
function Card({ title, info, actions, className, children }: { title: string; info?: string; actions?: ReactNode; className?: string; children: (shown: boolean) => ReactNode }) {
  const [ref, shown] = useReveal<HTMLElement>()
  return <section ref={ref} className={`reports-card${className ? ` ${className}` : ''}${shown ? ' is-shown' : ''}`}>
    <header>
      <span className="reports-card-title"><h2>{title}</h2>{info && <InfoTip label={title} text={info} />}</span>
      {actions && <div className="reports-card-actions">{actions}</div>}
    </header>
    {children(shown)}
  </section>
}

type Measure = 'netRevenue' | 'orders' | 'soldQuantity'
const measures: Record<Measure, string> = { netRevenue: 'Net Ciro', orders: 'Sipariş', soldQuantity: 'Satılan Adet' }

// The period's main figure (net revenue, or orders or units) and its buckets as columns; the best bucket stands out
// and the one under the pointer shows its value beside the previous period's.
function RevenueCard({ data }: { data: ReportSales }) {
  const [measure, setMeasure] = useState<Measure>('netRevenue')
  const now = data.summary
  const before = data.previousSummary
  const money = measure === 'netRevenue'
  const noAmount = money && now.ordersWithAmount === 0
  const format = money ? formatTl : formatNumber
  const name = (start: string) => data.granularity === 'month' ? `${start.slice(5, 7)}.${start.slice(0, 4)}` : data.granularity === 'week' ? `${formatDay(start)} haftası` : formatDay(start)
  const points: ColumnPoint[] = data.timeSeries.map((point, index) => ({
    key: point.start, short: data.granularity === 'month' ? `${point.start.slice(5, 7)}.${point.start.slice(2, 4)}` : `${point.start.slice(8, 10)}.${point.start.slice(5, 7)}`,
    title: name(point.start), value: point[measure], previous: data.previousTimeSeries[index]?.[measure] ?? null
  }))
  return <Card title={measures[measure]} className="reports-card-revenue"
    info={`${data.granularity === 'day' ? 'Günlük' : data.granularity === 'week' ? 'Haftalık' : 'Aylık'} toplamlar; en iyi dilim vurgulu. Net ciro, satış tutarından iade edilen tutar düşülerek bulunur.`}
    actions={<select aria-label="Grafik ölçüsü" value={measure} onChange={event => setMeasure(event.target.value as Measure)}>{(Object.keys(measures) as Measure[]).map(key => <option key={key} value={key}>{measures[key]}</option>)}</select>}>
    {shown => <>
      <div className="reports-card-figure">
        <strong>{noAmount ? 'Tutar yok' : <CountUp value={now[measure]} format={format} active={shown} />}</strong>
        <Delta delta={noAmount ? null : change(now[measure], before[measure])} />
        <span>Önceki dönem {money && before.ordersWithAmount === 0 ? '—' : format(before[measure])}</span>
      </div>
      <Columns points={points} format={format} axisFormat={money ? formatTlShort : formatNumber} shown={shown} label={`${measures[measure]} dönem içinde`} />
    </>}
  </Card>
}

type MoneyPart = Part & { note?: string }
type Money = { total: number; parts: MoneyPart[]; charged: number; left: number; leftNote: string; description: string }

// Where the sales went, from the profit steps: returns, the marketplaces' deductions as read, the entered product cost
// and what is left. What is left is an estimate; its note names every line that could not be taken off.
function moneyBreakdown(data: ReportSales, flow: ProfitSteps): Money | null {
  if (!flow.steps) return null
  const now = data.summary
  const finance = financeCosts(data)
  const tones: Record<string, string> = { returned: 'returned', commission: 'commission', cargo: 'cargo', fees: 'fees' }
  const parts: MoneyPart[] = flow.steps.filter(step => step.kind === 'minus' && step.value !== null && step.value > 0)
    .map(step => ({ key: step.key, label: step.label, value: step.value ?? 0, tone: tones[step.key] ?? 'fees', note: step.note }))
  const cost = data.cost.costedQuantity > 0 ? data.cost.costTotal : null
  if (cost !== null && cost > 0) parts.push({ key: 'cost', label: 'Ürün Maliyeti', value: cost, tone: 'cost', note: `Maliyeti girilmiş ${formatNumber(data.cost.costedQuantity)} adet için güncel maliyet; kapsam ${formatPercent(data.cost.coverage)}.` })
  const payout = flow.steps.find(step => step.key === 'payout')?.value ?? null
  const charged = parts.reduce((sum, part) => sum + part.value, 0)
  const left = (payout ?? now.netRevenue) - (cost ?? 0)
  const unread = finance ? unreadFeePlatforms(data) : []
  const gaps = [
    payout === null ? 'komisyon bilinmiyor' : null,
    finance ? (unread.length ? `${unread.join(', ')} kargo ve hizmet bedeli` : null) : unreadCostLines.join(', ').toLocaleLowerCase('tr-TR'),
    cost === null ? 'ürün maliyeti girilmemiş' : (data.cost.coverage ?? 0) < 1 ? 'maliyeti girilmemiş ürünlerin maliyeti' : null
  ].filter(Boolean)
  return { total: now.grossSales, parts, charged, left, description: flow.description,
    leftNote: `Satıştan iade, pazaryeri kesintileri ve ürün maliyeti düşüldükten sonra kalan; tahminidir.${gaps.length ? ` Düşülmeyenler: ${gaps.join('; ')}.` : ''}` }
}

type JourneyStep = 'sales' | 'costs' | 'left' | 'ratios' | 'profit'
const journeyTitles: Record<JourneyStep, string> = { sales: 'Satış Tutarı', costs: 'Masraflar', left: 'Masraflar Sonrası', ratios: 'Kâr Oranları', profit: 'Tahmini Net Kâr' }

// From the sales to the profit in five steps, read left to right: the sales, what the costs took (their parts as one
// bar), what is left after them, the ratios, and the estimated net profit at the end. Each step is a button that opens
// its breakdown under the row (the platforms, the cost lines, what was not taken off, the margin, the profit's basis),
// so the overview needs no separate cards for them. The steps come in one after another when first in view.
function ProfitJourney({ data, flow, money, onMore }: { data: ReportSales; flow: ProfitSteps; money: Money | null; onMore: OpenDetail }) {
  const [open, setOpen] = useState<JourneyStep | null>(null)
  const detailId = useId()
  const more = <MoreButton title="Satıştan Kâra" onClick={() => onMore('cost')} compact />
  if (!money) return <Card title="Satıştan Kâra" className="reports-card-journey" actions={more}>{() => <p className="reports-empty">Bu dönemde tutarı olan sipariş yok.</p>}</Card>
  const profit = flow.steps?.find(step => step.key === 'profit') ?? null
  const profitValue = profit?.value ?? null
  const net = data.cost.profitQuantity > 0
  const margin = profitValue === null ? null : net ? data.cost.netMargin : data.cost.grossMargin
  const share = (value: number) => money.total > 0 ? value / money.total : null
  const toggle = (key: JourneyStep) => setOpen(current => current === key ? null : key)
  const button = (key: JourneyStep, label: string) => ({ type: 'button' as const, onClick: () => toggle(key), 'aria-expanded': open === key, 'aria-controls': detailId, 'aria-label': `${label}: ayrıntıyı ${open === key ? 'kapat' : 'aç'}` })
  const step = (index: number, key: JourneyStep, className: string) => ({ style: { '--step': index } as CSSProperties, className: `${className}${open === key ? ' is-open' : ''}`.trim() || undefined })
  return <Card title="Satıştan Kâra" className="reports-card-journey" info={`${money.description} Bir adıma tıklayınca ayrıntısı açılır.`} actions={more}>
    {shown => <>
      <ol className="reports-journey">
        <li {...step(0, 'sales', '')}><button {...button('sales', 'Satış tutarı')}>
          <small>Satış Tutarı</small>
          <strong><CountUp value={money.total} format={formatTl} active={shown} /></strong>
          <span>İptaller hariç, iadeler düşülmeden</span>
        </button></li>
        <li {...step(1, 'costs', 'is-minus')}><button {...button('costs', 'Masraflar')}>
          <small>Masraflar</small>
          <strong>− <CountUp value={money.charged} format={formatTl} active={shown} /></strong>
          <SalesShare from={0} to={share(money.charged) ?? 0} tone="bad" shown={shown} label={`Satışın ${formatPercent(share(money.charged))} kadarı masraf`} />
          <span>Satışın {formatPercent(share(money.charged))} kadarı</span>
          <ul className="reports-journey-parts">{[...money.parts].sort((a, b) => b.value - a.value).slice(0, 3).map(part =>
            <li key={part.key}><i className={`is-${part.tone}`} aria-hidden="true" /><span>{part.label}</span><b>{formatTlWhole(part.value)}<em>{formatPercent(share(part.value))}</em></b></li>)}</ul>
        </button></li>
        <li {...step(2, 'left', `is-equals${money.left < 0 ? ' is-negative' : ''}`)}><button {...button('left', 'Masraflar sonrası')}>
          <small>{money.left < 0 ? 'Açık' : 'Masraflar Sonrası'}</small>
          <strong>{money.left < 0 ? `− ${formatTl(-money.left)}` : <CountUp value={money.left} format={formatTl} active={shown} />}</strong>
          {money.left < 0
            ? <SalesShare from={0} to={1} tone="bad" shown={shown} label="Masraflar satışın tamamını aştı" />
            : <SalesShare from={share(money.charged) ?? 0} to={1} tone="good" shown={shown} label={`Satışın ${formatPercent(share(money.left))} kadarı kaldı`} />}
          <span>{money.left < 0 ? 'Masraflar satışı aştı' : `Satışın ${formatPercent(share(money.left))} kadarı kaldı`}</span>
        </button></li>
        <li {...step(3, 'ratios', 'is-ratios')}><button {...button('ratios', 'Kâr oranları')}>
          <small>Kâr Oranları</small>
          <span className="reports-journey-ratios">
            <span className={margin !== null && margin < 0 ? 'is-bad' : undefined}><em>{net ? 'Net marj' : 'Brüt marj'}</em><b>{formatPercent(margin)}</b></span>
            <span><em>Masraf / satış</em><b>{formatPercent(share(money.charged))}</b></span>
            <span><em>İade / satış</em><b>{formatPercent(share(data.summary.returnedAmount))}</b></span>
          </span>
        </button></li>
        <li {...step(4, 'profit', `is-result${profitValue !== null && profitValue < 0 ? ' is-negative' : ''}${profitValue === null ? ' is-missing' : ''}`)}><button {...button('profit', profit?.label ?? 'Tahmini net kâr')}>
          <small>{profit?.label ?? 'Tahmini Net Kâr'}</small>
          <strong>{profitValue === null ? (profit?.missing ?? 'Maliyet girilmemiş') : <CountUp value={profitValue} format={formatTl} active={shown} />}</strong>
          <span>{profitValue === null ? 'Ürün maliyeti girilince görünür' : `Maliyeti bilinen satış üzerinden · kapsam ${formatPercent(net ? data.cost.profitCoverage : data.cost.coverage)}`}</span>
        </button></li>
      </ol>
      <p className="reports-journey-hint">Ayrıntısını görmek için bir adıma tıklayın.</p>
      {/* The breakdown opens over the page, so nothing under the card moves. */}
      <Modal open={open !== null} onClose={() => setOpen(null)} className="reports-journey-modal" title={open ? journeyTitles[open] : ''}>
        <div id={detailId}>{open === null ? null
          : open === 'sales' ? <SalesDetail data={data} />
          : open === 'costs' ? <CostsDetail money={money} />
          : open === 'left' ? <LeftDetail money={money} />
          : open === 'ratios' ? <RatiosDetail data={data} money={money} margin={margin} net={net} />
          : <ProfitDetail data={data} profit={profit} net={net} onMore={onMore} />}</div>
      </Modal>
    </>}
  </Card>
}

// The sales as one bar, the part from `from` to `to` (shares of the sales) filled: the costs from the left in red, what
// is left after them from there on in green, so the two steps read as one bar split in two. Fills once when first seen.
function SalesShare({ from, to, tone, shown, label }: { from: number; to: number; tone: 'good' | 'bad'; shown: boolean; label: string }) {
  const start = Math.max(0, Math.min(1, from)) * 100
  const end = Math.max(start, Math.min(1, to) * 100)
  return <span className={`reports-sales-share is-${tone}`} role="img" aria-label={label}>
    <i style={{ left: `${start}%`, width: shown ? `${end - start}%` : '0%' }} />
  </span>
}

// Satış Tutarı's breakdown: each platform's sales with its share, the total under them.
function SalesDetail({ data }: { data: ReportSales }) {
  const rows = data.platforms.filter(group => group.ordersWithAmount > 0 && group.grossSales > 0).sort((a, b) => b.grossSales - a.grossSales)
  const total = rows.reduce((sum, group) => sum + group.grossSales, 0)
  return <div className="reports-journey-pane">
    <h3>Platformlara göre satış</h3>
    {rows.length === 0 ? <p className="reports-empty">Bu dönemde tutarı olan satış yok.</p> : <ul className="reports-platform-list is-columns">{rows.map(group => <li key={group.key}>
      <PlatformSquareMark code={group.key} name={platformName(group.label)} />
      <span>{platformName(group.label)}<small>{formatPercent(group.grossSales / total)} · {formatCount(group.orders)} sipariş</small></span>
      <strong>{formatTl(group.grossSales)}</strong>
      <Progress value={group.grossSales / total} shown tone={platformColor(group.key)} />
    </li>)}</ul>}
    <NoAmountNote platforms={data.platforms} />
  </div>
}

// Masraflar's breakdown: every cost line with its amount, its share of the sales and how it was read.
function CostsDetail({ money }: { money: Money }) {
  return <div className="reports-journey-pane">
    <h3>Masraf kalemleri</h3>
    <ul className="reports-cost-list is-detailed">{money.parts.map(part => <li key={part.key}>
      <i className={`is-${part.tone}`} aria-hidden="true" />
      <span>{part.label}{part.note && <small>{part.note}</small>}</span>
      <strong>{formatTl(part.value)}</strong>
      <small>{formatPercent(money.total > 0 ? part.value / money.total : null)}</small>
    </li>)}</ul>
  </div>
}

// Masraflar Sonrası's breakdown: the subtraction written out and what it could not take off.
function LeftDetail({ money }: { money: Money }) {
  return <div className="reports-journey-pane">
    <h3>Nasıl hesaplandı</h3>
    <dl className="reports-ledger">
      <div><dt>Satış tutarı</dt><dd>{formatTl(money.total)}</dd></div>
      <div className="is-minus"><dt>Masraflar</dt><dd>− {formatTl(money.charged)}</dd></div>
      <div className="is-total"><dt>{money.left < 0 ? 'Açık' : 'Masraflar sonrası'}</dt><dd>{money.left < 0 ? `− ${formatTl(-money.left)}` : formatTl(money.left)}</dd></div>
    </dl>
    <p className="reports-note"><UiIcon name="info" size={16} />{money.leftNote}</p>
  </div>
}

// Kâr Oranları's breakdown: the margin on a gauge, and each ratio with what it divides.
function RatiosDetail({ data, money, margin, net }: { data: ReportSales; money: Money; margin: number | null; net: boolean }) {
  const share = (value: number) => money.total > 0 ? value / money.total : null
  return <div className="reports-journey-pane is-split">
    <div className="reports-gauge-wrap">
      <HalfGauge value={margin} shown tone={margin !== null && margin < 0 ? 'bad' : 'good'} label={`Marj ${formatPercent(margin)}`} />
      <strong>{formatPercent(margin)}</strong>
      <small>{net ? 'Net marj' : 'Brüt marj'}</small>
    </div>
    <dl className="reports-ledger">
      <div><dt>{net ? 'Net marj' : 'Brüt marj'}<small>{net ? 'Tahmini net kâr / maliyeti ve komisyonu bilinen satış' : 'Tahmini brüt kâr / maliyeti bilinen satış; komisyon öncesi'}</small></dt><dd>{formatPercent(margin)}</dd></div>
      <div><dt>Masraf / satış<small>İade, pazaryeri kesintileri ve ürün maliyeti / satış tutarı</small></dt><dd>{formatPercent(share(money.charged))}</dd></div>
      <div><dt>İade / satış<small>İade edilen tutar / satış tutarı</small></dt><dd>{formatPercent(share(data.summary.returnedAmount))}</dd></div>
    </dl>
  </div>
}

// Tahmini Net Kâr's breakdown: what it is computed over, how much of the sales that covers, and the way to the account.
function ProfitDetail({ data, profit, net, onMore }: { data: ReportSales; profit: FlowStep | null; net: boolean; onMore: OpenDetail }) {
  const coverage = net ? data.cost.profitCoverage : data.cost.coverage
  return <div className="reports-journey-pane">
    <h3>Kârın dayanağı</h3>
    <p className="reports-journey-text">{profit?.note ?? 'Ürün kartında varyant maliyeti girildiğinde kâr burada görünür.'}</p>
    <div className="reports-coverage-bar">
      <span>Maliyeti bilinen satış<b>{formatPercent(coverage)}</b></span>
      <Progress value={coverage} shown />
    </div>
    <button type="button" className="secondary reports-journey-link" onClick={() => onMore('cost')}>Kârlılık sekmesinde aç<UiIcon name="arrowRight" size={14} /></button>
  </div>
}

// Dönem Özeti, as the marketplaces' own reports open: the gross sale, the cancellations and returns taken off in units
// and money, and the net sale; the bar splits the gross units the same way and fills once when first seen. Under it the
// orders, the average basket and the return rate with their change, then the average sale price and commission rate.
function PeriodSummaryCard({ data }: { data: ReportSales }) {
  const now = data.summary
  const before = data.previousSummary
  const noAmount = now.ordersWithAmount === 0
  const cancelledUnits = data.cancellations.cancelledQuantity
  const cancelledAmount = data.cancellations.cancelledAmount ?? null
  const grossUnits = now.soldQuantity + cancelledUnits
  const money = (value: number | null) => noAmount || value === null ? '—' : formatTl(value)
  const rows = [
    { key: 'gross', label: 'Brüt Satış', units: grossUnits, amount: cancelledAmount === null ? null : now.grossSales + cancelledAmount, minus: false },
    { key: 'cancel', label: 'İptaller', units: cancelledUnits, amount: cancelledAmount, minus: true },
    { key: 'return', label: 'İadeler', units: now.returnedQuantity, amount: now.returnedAmount, minus: true },
    { key: 'net', label: 'Net Satış', units: now.keptQuantity, amount: now.netRevenue, minus: false }
  ]
  const part = (units: number) => grossUnits > 0 ? units / grossUnits * 100 : 0
  const figures = [
    { key: 'orders', label: 'Sipariş', value: now.orders as number | null, format: formatCount, delta: change(now.orders, before.orders), invert: false },
    { key: 'basket', label: 'Ortalama Sepet', value: noAmount ? null : now.averageOrderValue, format: formatTl, delta: noAmount ? null : change(now.averageOrderValue, before.averageOrderValue), invert: false },
    { key: 'returns', label: 'İade Oranı', value: now.returnRate as number | null, format: (value: number) => formatPercent(value), delta: change(now.returnRate, before.returnRate, 'rate'), invert: true }
  ]
  return <Card title="Dönem Özeti" className="reports-card-summary" info="Brüt satış: iptaller dahil sipariş edilen adet ve tutar. İptal tutarı ürünlerin liste fiyatıyla, iade tutarı ödenen tutarla hesaplanır; net satış kalan adet ve tutardır. Sipariş iptaller hariçtir; ortalama sepet satış tutarı / tutarı olan sipariş; iade oranı iade adedi / satılan adettir. Değişimler önceki döneme göredir.">
    {shown => <>
      <div className="reports-summary-bar" role="img" aria-label={`Brüt ${formatNumber(grossUnits)} adedin ${formatNumber(now.keptQuantity)} adedi net satış, ${formatNumber(now.returnedQuantity)} adedi iade, ${formatNumber(cancelledUnits)} adedi iptal`}>
        {[['net', now.keptQuantity], ['return', now.returnedQuantity], ['cancel', cancelledUnits]].map(([key, units]) => <i key={key} className={`is-${key}`} style={{ width: shown ? `${part(units as number)}%` : '0%' }} />)}
      </div>
      <dl className="reports-summary-rows">{rows.map(row => <div key={row.key} className={`is-${row.key}`}>
        <dt><i aria-hidden="true" />{row.label}</dt>
        <dd><strong>{row.minus && row.units > 0 ? '−' : ''}{formatNumber(row.units)} adet</strong><span>{row.minus && row.amount ? '− ' : ''}{money(row.amount)}</span></dd>
      </div>)}</dl>
      <div className="reports-summary-metrics">
        {figures.map(figure => <div key={figure.key} className={`is-${figure.key}`}>
          <small>{figure.label}</small>
          <span><strong>{figure.value === null ? 'Tutar yok' : <CountUp value={figure.value} format={figure.format} active={shown} />}</strong><Delta delta={figure.delta} invert={figure.invert} /></span>
        </div>)}
        <div><small>Ort. Satış Fiyatı</small><strong>{noAmount || now.soldQuantity === 0 ? '—' : formatTl(now.grossSales / now.soldQuantity)}</strong></div>
        <div><small>Ort. Komisyon Oranı</small><strong>{formatPercent(data.commission.effectiveRate)}</strong></div>
      </div>
    </>}
  </Card>
}

type Operation = { key: string; label: string; icon: UiIconName; value: string; hint: string; tone: 'ok' | 'warn' | 'calm'; detail: DetailKey }

// The operations worth a look, as a list like an account's history: each row opens its tab.
function OperationsCard({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const now = data.summary
  const warning = data.cancellations.warningRate
  const ship = weightedHours(data.cargo, row => row.averageHoursToShip, row => row.measuredShip)
  const low = data.stock.bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const packages = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const notInvoiced = data.invoices.buckets.find(bucket => bucket.key === 'NOT_INVOICED')?.packages ?? 0
  const rows: Operation[] = [
    { key: 'cancel', label: 'İptal Oranı', icon: 'close', value: formatPercent(now.cancelRate), hint: `${formatCount(now.cancelledOrders)} sipariş · eşik ${formatPercent(warning)}`, tone: (now.cancelRate ?? 0) > warning ? 'warn' : 'ok', detail: 'cancellations' },
    { key: 'returns', label: 'İade Talebi', icon: 'returns', value: formatCount(now.returnClaims), hint: `${formatNumber(now.returnedQuantity)} adet`, tone: 'calm', detail: 'returns' },
    { key: 'cargo', label: 'Kargoya Verme', icon: 'truck', value: formatHours(ship.hours), hint: ship.measured > 0 ? `${formatCount(ship.measured)} pakette ölçüldü` : 'Ölçülemedi', tone: 'calm', detail: 'cargo' },
    { key: 'stock', label: 'Stok Riski', icon: 'stock', value: formatCount(low), hint: '7 günden az stoğu kalan', tone: low > 0 ? 'warn' : 'ok', detail: 'stock' },
    { key: 'invoice', label: 'Faturasız Paket', icon: 'invoice', value: formatCount(notInvoiced), hint: `${formatCount(packages)} paket içinde`, tone: notInvoiced > 0 ? 'warn' : 'ok', detail: 'invoices' },
    { key: 'customers', label: 'Tekrar Alım', icon: 'users', value: formatPercent(data.customers.repeatRate), hint: `${formatCount(data.customers.uniqueCustomers)} tekil müşteri`, tone: 'calm', detail: 'customers' }
  ]
  return <Card title="Operasyon" className="reports-card-operations" info="Her satır kendi sekmesini açar.">
    {() => <ul className="reports-operations">{rows.map(row => <li key={row.key}>
      <button type="button" className={`is-${row.tone}`} onClick={() => onMore(row.detail)} aria-label={`${row.label}: ${row.value}. Tümünü Gör`}>
        <span className="reports-operation-icon" aria-hidden="true"><UiIcon name={row.icon} size={16} /></span>
        <span className="reports-operation-text">{row.label}<small>{row.hint}</small></span>
        <strong>{row.value}</strong>
      </button>
    </li>)}</ul>}
  </Card>
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
      <Panel title="Yükselen Ürünler" description="Önceki döneme göre satılan adedi en çok artanlar." className="reports-list">
        <Movers items={data.rising ?? []} tone="up" empty="Önceki döneme göre satışı artan ürün yok." />
      </Panel>
      <Panel title="Düşen Ürünler" description="Önceki döneme göre satılan adedi en çok azalanlar." className="reports-list">
        <Movers items={data.falling ?? []} tone="down" empty="Önceki döneme göre satışı azalan ürün yok." />
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
      {sub === 'returns' && <Returns data={data} />}
      {sub === 'cancellations' && <Cancellations data={data} />}
      {sub === 'cargo' && <Cargo data={data} />}
      {sub === 'invoices' && <Invoices data={data} />}
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

const unreadCostLines = ['Kargo Faturası', 'Hizmet Bedeli', 'Stopaj', 'Reklam', 'Ceza']

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

// The marketplaces' account statements (one picked at a time) beside the platforms' net revenue, then their summary.
function Platforms({ data }: { data: ReportSales }) {
  return <div className="reports-grid is-ledger">
    <StatementLines data={data} />
    <Panel title="Platformlara Göre Net Ciro" description="Komisyon ve hakediş tahminidir; kapsam, komisyon bilgisi olan cironun payıdır.">
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

// A variant's options as the catalogue names them (Beden: M · Renk: Siyah); empty when the line has none.
function variantText(product: ReportProduct) { return (product.options ?? []).map(option => `${option.name}: ${option.value}`).join(' · ') }

// The product's picture, or a placeholder of its first letter.
function ProductImage({ product }: { product: ReportProduct }) {
  return product.imageUrl ? <img src={thumbnailUrl(product.imageUrl)} alt="" loading="lazy" className="reports-product-image" /> : <i className="reports-product-image" aria-hidden="true">{product.title.slice(0, 1)}</i>
}

// The product's picture beside its name.
function ProductCell({ product }: { product: ReportProduct }) {
  return <span className="reports-product"><ProductImage product={product} /><strong>{product.title}</strong></span>
}

const unitPrice = (row: ReportProduct) => row.soldQuantity > 0 ? row.salesAmount / row.soldQuantity : null
const commissionRate = (row: ReportProduct) => row.commissionCoveredAmount > 0 ? row.estimatedCommission / row.commissionCoveredAmount : null
const soldChange = (row: ReportProduct) => row.previousSoldQuantity === undefined ? null : change(row.soldQuantity, row.previousSoldQuantity ?? 0)

// The products that moved most against the previous period: each with its picture, the units before and now as two
// bars on one scale (before muted, now in colour) and the change. The bars fill once when the list first comes into view.
function Movers({ items, tone, empty }: { items: ReportProduct[]; tone: 'up' | 'down'; empty: string }) {
  const [ref, shown] = useReveal<HTMLUListElement>()
  if (!items.length) return <p className="reports-empty">{empty}</p>
  const rows = items.slice(0, 8)
  const max = Math.max(1, ...rows.flatMap(item => [item.soldQuantity, item.previousSoldQuantity ?? 0]))
  return <ul ref={ref} className={`reports-movers is-${tone}`} aria-label={tone === 'up' ? 'Yükselen ürünler' : 'Düşen ürünler'}>{rows.map((item, index) => {
    const before = item.previousSoldQuantity ?? 0
    const diff = item.soldQuantity - before
    const detail = [item.modelCode, variantText(item)].filter(Boolean).join(' · ') || item.sku
    return <li key={item.variantId ?? item.sku}>
      <ProductImage product={item} />
      <span className="reports-mover-name"><strong title={item.title}>{item.title}</strong><small title={detail}>{detail}</small></span>
      <span className="reports-mover-bars" aria-hidden="true">
        <i className="is-before" style={{ width: shown ? `${before / max * 100}%` : '0%', transitionDelay: `${index * 50}ms` }} />
        <i className="is-now" style={{ width: shown ? `${item.soldQuantity / max * 100}%` : '0%', transitionDelay: `${index * 50 + 120}ms` }} />
      </span>
      <span className="reports-mover-change"><b>{diff > 0 ? '+' : '−'}{formatNumber(Math.abs(diff))}</b><small>{formatNumber(before)} → {formatNumber(item.soldQuantity)} adet</small></span>
    </li>
  })}</ul>
}

function productColumns(): Column<ReportProduct>[] {
  return [
    { key: 'title', header: 'Ürün', value: row => row.title, render: row => <ProductCell product={row} />, csv: row => row.title },
    { key: 'model', header: 'Model Kodu', value: row => row.modelCode ?? null, render: row => row.modelCode ?? '—' },
    { key: 'sku', header: 'Stok Kodu', value: row => row.sku },
    { key: 'options', header: 'Varyant', value: row => variantText(row) || null, render: row => variantText(row) || '—' },
    { key: 'brand', header: 'Marka', value: row => row.brand },
    { key: 'sold', header: 'Satılan Adet', value: row => row.soldQuantity, render: row => formatNumber(row.soldQuantity), numeric: true },
    { key: 'returned', header: 'İade Adedi', value: row => row.returnedQuantity, render: row => formatNumber(row.returnedQuantity), numeric: true },
    { key: 'returnRate', header: 'İade Oranı', value: row => row.returnRate, render: row => formatPercent(row.returnRate), numeric: true },
    { key: 'kept', header: 'Net Adet', value: row => row.soldQuantity - row.returnedQuantity, render: row => formatNumber(row.soldQuantity - row.returnedQuantity), numeric: true },
    { key: 'sales', header: 'Satış Tutarı', value: row => row.salesAmount, render: row => formatTl(row.salesAmount), numeric: true },
    { key: 'netAmount', header: 'Net Ciro', value: row => row.netAmount ?? null, render: row => formatTl(row.netAmount), numeric: true },
    { key: 'price', header: 'Ort. Satış Fiyatı', value: unitPrice, render: row => formatTl(unitPrice(row)), numeric: true },
    { key: 'commission', header: 'Tahmini Komisyon', value: row => row.commissionCoveredAmount > 0 ? row.estimatedCommission : null, render: row => row.commissionCoveredAmount > 0 ? formatTl(row.estimatedCommission) : unknownLabel, numeric: true },
    { key: 'commissionRate', header: 'Komisyon Oranı', value: commissionRate, render: row => formatPercent(commissionRate(row)), numeric: true },
    { key: 'change', header: 'Önceki Döneme Göre', value: row => row.previousSoldQuantity === undefined ? null : row.soldQuantity - (row.previousSoldQuantity ?? 0),
      render: row => <span className="reports-cell-change">{formatNumber(row.previousSoldQuantity)} → {formatNumber(row.soldQuantity)}{row.previousSoldQuantity === 0 && row.soldQuantity > 0 ? <em className="reports-delta is-good">Yeni</em> : <Delta delta={soldChange(row)} />}</span>, csv: row => row.previousSoldQuantity ?? null },
    { key: 'available', header: 'Güncel Stok', value: row => row.available ?? null, render: row => formatNumber(row.available), numeric: true }
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

// Reasons as bars with their share, the unknown last; the full list goes out as CSV instead of a three-column table.
function ReasonsCard({ title, description, reasons, csvName, empty }: { title: string; description: string; reasons: ReportReason[]; csvName: string; empty: string }) {
  const total = reasons.reduce((sum, reason) => sum + reason.count, 0)
  const exportCsv = () => downloadCsv(`${csvName}.csv`, toCsv(reasons, reasonColumns(total, 'Neden').map<CsvColumn<ReportReason>>(column => ({ header: column.header, value: column.value }))))
  return <Panel title={title} description={description} className="reports-list"
    actions={<button type="button" className="secondary reports-csv" onClick={exportCsv} disabled={!reasons.length}><UiIcon name="download" size={16} />CSV</button>}>
    {total > 0 ? <Bars label={title} format={formatCount} items={topReasons(reasons, 8).map(item => ({ ...item, note: formatPercent(item.value / total) }))} /> : <p className="reports-empty">{empty}</p>}
  </Panel>
}

// İadeler: four figures, the reasons beside the provinces they came from, then every province's returns.
function Returns({ data }: { data: ReportSales }) {
  const byCity = data.cities.filter(city => city.returnedQuantity > 0 || city.returnClaims > 0)
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="İade Talebi" value={formatCount(data.summary.returnClaims)} hint="Reddedilen ve iptal edilenler hariç" />
      <Stat label="İade Adedi" value={formatNumber(data.summary.returnedQuantity)} hint="Kabul edilen iadeler" />
      <Stat label="İade Oranı" value={formatPercent(data.summary.returnRate)} hint="İade / satılan adet" />
      <Stat label="İade Tutarı" value={data.summary.ordersWithAmount > 0 ? formatTl(data.summary.returnedAmount) : 'Tutar yok'} hint="Satıştan düşülen" />
    </div>
    <ReasonsCard title="İade Nedenleri" description="Pazaryerinin bildirdiği nedene göre; neden yoksa Bilinmiyor." reasons={data.returnReasons} csvName="rapor-iade-nedenleri" empty="Bu dönemde iade talebi yok." />
    <Panel title="İade Gelen İller" description="İade adedine göre; yanında iade oranı." className="reports-list">
      <Bars label="İllere göre iade" format={formatNumber} items={topGroups(data.cities, group => group.returnedQuantity, group => formatPercent(group.returnRate))} />
    </Panel>
    <div className="is-wide"><DataTable title="İllere Göre İadeler" csvName="rapor-iade-sehirler" rows={byCity} rowKey={row => row.key} unknown={row => !row.known} initialSort="returned" columns={groupColumns(labelColumn('İl'))} empty="Bu dönemde iade yok." /></div>
  </div>
}

function Stat({ label, value, hint, warn = false }: { label: string; value: string; hint?: string; warn?: boolean }) {
  return <article className={`reports-kpi is-compact${warn ? ' is-warn' : ''}`}><small>{label}</small><strong>{value}</strong>{hint && <span className="reports-kpi-foot"><span>{hint}</span></span>}</article>
}

function Cancellations({ data }: { data: ReportSales }) {
  const cancellations = data.cancellations
  const warn = (rate: number | null) => (rate ?? 0) > cancellations.warningRate
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="İptal Edilen Sipariş" value={formatCount(cancellations.cancelledOrders)} />
      <Stat label="İptal Edilen Adet" value={formatNumber(cancellations.cancelledQuantity)} hint="Kısmi iptaller dahil" />
      <Stat label="İptal Oranı" value={formatPercent(cancellations.cancelRate)} warn={warn(cancellations.cancelRate)} hint={`Uyarı eşiği ${formatPercent(cancellations.warningRate)}`} />
    </div>
    {warn(cancellations.cancelRate) && <p className="reports-warning is-wide" role="alert"><UiIcon name="alert" size={16} />İptal oranı {formatPercent(cancellations.warningRate)} eşiğinin üstünde; Trendyol satıcı puanı yönergesi bu oranı izler.</p>}
    <ReasonsCard title="İptal Nedenleri" description="Siparişte gelen iptal nedenine göre; neden gelmeyenler Bilinmiyor." reasons={cancellations.reasons} csvName="rapor-iptal-nedenleri" empty="Bu dönemde iptal yok." />
    <Panel title="Platformlara Göre İptal Oranı" description={`Uyarı eşiği ${formatPercent(cancellations.warningRate)}.`} className="reports-list">
      <Bars label="Platformlara göre iptal oranı" format={value => formatPercent(value)} items={cancellations.platforms.filter(row => row.cancelRate !== null)
        .map(row => ({ key: row.key, label: platformName(row.label), value: row.cancelRate ?? 0, note: `${formatCount(row.cancelledOrders)} iptal` }))} />
    </Panel>
    <div className="is-wide"><DataTable title="Platformlara Göre İptaller" csvName="rapor-iptaller" rows={cancellations.platforms} rowKey={row => row.key} initialSort="cancelled" columns={[
      { key: 'label', header: 'Platform', value: row => platformName(row.label) },
      { key: 'orders', header: 'Toplam Sipariş', value: row => row.orders, render: row => formatCount(row.orders), numeric: true },
      { key: 'cancelled', header: 'İptal', value: row => row.cancelledOrders, render: row => formatCount(row.cancelledOrders), numeric: true },
      { key: 'quantity', header: 'İptal Adedi', value: row => row.cancelledQuantity, render: row => formatNumber(row.cancelledQuantity), numeric: true },
      { key: 'rate', header: 'İptal Oranı', value: row => row.cancelRate, render: row => <span className={warn(row.cancelRate) ? 'reports-badge is-warn' : undefined}>{formatPercent(row.cancelRate)}</span>, numeric: true }
    ]} empty="Bu dönemde iptal yok." /></div>
  </div>
}

// Kargo: three figures over every carrier, the carriers' shares, then the full table.
function Cargo({ data }: { data: ReportSales }) {
  const packages = data.cargo.reduce((sum, row) => sum + row.packages, 0)
  const ship = weightedHours(data.cargo, row => row.averageHoursToShip, row => row.measuredShip)
  const deliver = weightedHours(data.cargo, row => row.averageHoursToDeliver, row => row.measuredDeliver)
  const measured = (count: number) => count > 0 ? `${formatCount(count)} pakette ölçüldü` : 'Ölçülemedi'
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="Paket" value={formatCount(packages)} hint={`${formatCount(data.cargo.filter(row => row.known).length)} kargo firması`} />
      <Stat label="Kargo Teslim Alma" value={formatHours(ship.hours)} hint={measured(ship.measured)} />
      <Stat label="Teslimat" value={formatHours(deliver.hours)} hint={measured(deliver.measured)} />
    </div>
    <Panel title="Kargo Firmalarının Payı" description="Paket sayısına göre." className="reports-list is-wide">
      <Bars label="Kargo firmalarının payı" format={value => formatPercent(value)} items={[...data.cargo].sort((a, b) => b.packages - a.packages)
        .map(row => ({ key: row.label, label: row.label, value: row.share ?? 0, note: `${formatCount(row.packages)} paket`, muted: !row.known }))} />
    </Panel>
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

const hourRange = (hour: number) => `${String(hour).padStart(2, '0')}:00–${String(hour + 1).padStart(2, '0')}:00`

// When the orders come, in one card: pick the platforms (none picked: all of them) and a weekday (none: the whole
// week), and the columns show those orders hour by hour, the busiest hour marked. Each weekday button carries its
// count and a small bar of it, so the week reads at a glance. The full 7 × 24 grid still goes out as CSV.
function TimeOfDay({ data }: { data: ReportSales }) {
  const sources = data.platformHeatmaps?.length ? data.platformHeatmaps : null
  const [picked, setPicked] = useState<string[]>([])
  const [day, setDay] = useState<number | null>(null)
  const chosen = sources && picked.length ? sources.filter(source => picked.includes(source.platformCode)) : null
  const grid = chosen ? Array.from({ length: 7 }, (_, weekday) => Array.from({ length: 24 }, (_, hour) => chosen.reduce((sum, source) => sum + (source.heatmap[weekday]?.[hour] ?? 0), 0))) : data.heatmap
  const byDay = grid.map(row => row.reduce((sum, value) => sum + value, 0))
  const byHour = Array.from({ length: 24 }, (_, hour) => day === null ? grid.reduce((sum, row) => sum + (row[hour] ?? 0), 0) : grid[day]?.[hour] ?? 0)
  const week = byDay.reduce((sum, value) => sum + value, 0)
  const total = byHour.reduce((sum, value) => sum + value, 0)
  const busiestDay = byDay.indexOf(Math.max(...byDay))
  const busiestHour = byHour.indexOf(Math.max(...byHour))
  const dayMax = Math.max(1, ...byDay)
  const toggle = (code: string) => setPicked(current => current.includes(code) ? current.filter(item => item !== code) : [...current, code])
  const csv = () => downloadCsv('rapor-saat-gun.csv', toCsv(grid.map((row, weekday) => ({ weekday, row })), [
    { header: 'Gün', value: item => weekdays[item.weekday] }, ...Array.from({ length: 24 }, (_, hour): CsvColumn<{ weekday: number; row: number[] }> => ({ header: `${String(hour).padStart(2, '0')}:00`, value: item => item.row[hour] ?? 0 }))
  ]))
  const hours: ColumnPoint[] = byHour.map((value, hour) => ({ key: String(hour), short: String(hour).padStart(2, '0'), title: `${day === null ? '' : `${weekdays[day]} `}${hourRange(hour)}`, value, previous: null }))
  const scope = `${chosen ? chosen.map(source => platformName(source.platformCode)).join(' + ') : 'Tüm platformlar'} · ${day === null ? 'tüm hafta' : weekdays[day]}`
  return <div className="reports-grid">
    <RevealPanel title="Gün ve Saate Göre Siparişler" description="İstanbul saatiyle; iptaller hariç. Platform ve gün seçerek daraltın." className="is-wide reports-time-panel"
      actions={<button type="button" className="secondary reports-csv" onClick={csv}><UiIcon name="download" size={16} />CSV</button>}>
      {shown => <>
        {sources && sources.length > 1 && <div className="reports-segmented" role="group" aria-label="Platform">
          <button type="button" aria-pressed={picked.length === 0} onClick={() => setPicked([])}>Tüm Platformlar</button>
          {sources.map(source => <button key={source.platformCode} type="button" aria-pressed={picked.includes(source.platformCode)} onClick={() => toggle(source.platformCode)}>{platformName(source.platformCode)}</button>)}
        </div>}
        <div className="reports-time-days" role="group" aria-label="Gün">
          <button type="button" aria-pressed={day === null} onClick={() => setDay(null)} className="is-all"><span>Tüm Hafta</span><strong>{formatCount(week)}</strong></button>
          {byDay.map((value, weekday) => <button key={weekdays[weekday]} type="button" aria-pressed={day === weekday} onClick={() => setDay(current => current === weekday ? null : weekday)}
            className={weekday === busiestDay && value > 0 ? 'is-busiest' : undefined} title={`${weekdays[weekday]} · ${formatCount(value)} sipariş`}>
            <span>{weekdays[weekday]}</span><strong>{formatCount(value)}</strong><i aria-hidden="true"><b style={{ width: `${value / dayMax * 100}%` }} /></i>
          </button>)}
        </div>
        <p className="reports-time-summary"><span>{scope}</span><b>{formatCount(total)} sipariş</b>{total > 0 && <em>En yoğun saat {hourRange(busiestHour)} · {formatCount(byHour[busiestHour])} sipariş</em>}</p>
        {total > 0 ? <Columns points={hours} format={value => `${formatCount(value)} sipariş`} axisFormat={formatCount} shown={shown} label={`${scope}: saatlere göre siparişler`} /> : <p className="reports-empty">Bu seçimde sipariş yok.</p>}
      </>}
    </RevealPanel>
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
    <RevealPanel title="Bir Kez Alan ve Tekrar Alan" className="is-wide" description="Dönemde birden çok sipariş veren müşteriler tekrar alan sayılır.">
      {shown => customers.uniqueCustomers > 0 ? <>
        <StackedBar shown={shown} total={customers.uniqueCustomers} label={`Bir kez alan ${formatCount(customers.uniqueCustomers - customers.repeatCustomers)}, tekrar alan ${formatCount(customers.repeatCustomers)}`}
          parts={[{ key: 'repeat', label: 'Tekrar alan', value: customers.repeatCustomers, tone: 'left' }, { key: 'once', label: 'Bir kez alan', value: customers.uniqueCustomers - customers.repeatCustomers, tone: 'fees' }]} />
        <ul className="reports-legend-row">
          <li><i className="is-left" aria-hidden="true" />Tekrar alan<b>{formatCount(customers.repeatCustomers)}</b></li>
          <li><i className="is-fees" aria-hidden="true" />Bir kez alan<b>{formatCount(customers.uniqueCustomers - customers.repeatCustomers)}</b></li>
        </ul>
      </> : <p className="reports-empty">Kimliği okunan müşteri yok.</p>}
    </RevealPanel>
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
  const low = data.stock.bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const unknownStock = data.stock.bestSellers.filter(row => row.available === null).length
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="Kalan Günü 7'den Az" value={formatCount(low)} hint={`En çok satan ${formatCount(data.stock.bestSellers.length)} ürün içinde`} warn={low > 0} />
      <Stat label="Satıp Stoğu Biten" value={formatCount(data.stock.soldOut.length)} hint="Mevcut stoğu sıfır" warn={data.stock.soldOut.length > 0} />
      <Stat label="Stoğu Bilinmeyen" value={formatCount(unknownStock)} hint="En çok satanlar içinde" />
    </div>
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
    <Ledger title="Kâr Hesabı" description="Maliyeti bilinen satışlardan, adım adım net kâra." rows={cost.costedQuantity === 0 ? [] : [
      { label: 'Maliyetli satış tutarı', value: formatTl(cost.costedRevenue), hint: `${formatNumber(cost.costedQuantity)} adet` },
      { label: 'Ürün maliyeti', value: formatTl(cost.costTotal), hint: 'Güncel maliyet', kind: 'minus' },
      { label: 'Brüt kâr', value: formatTl(cost.grossProfit), hint: `Marj ${formatPercent(cost.grossMargin)} · komisyon öncesi`, kind: 'total', tone: signTone(cost.grossProfit) },
      ...(cost.profitQuantity > 0 ? [
        { label: 'Komisyon', value: formatTl(cost.profitCommission), hint: 'Kârı hesaplanan satışlarınki', kind: 'minus' as const },
        { label: 'Tahmini net kâr', value: formatTl(cost.netProfit), hint: `Marj ${formatPercent(cost.netMargin)} · ${formatNumber(cost.profitQuantity)} adet üzerinden`, kind: 'result' as const, tone: signTone(cost.netProfit) }
      ] : [])
    ]} empty="Ürün kartında varyant maliyeti girildiğinde kâr burada görünür." />
    <Ledger title="Komisyon ve Hakediş" description="Tahminidir; kesinleşen tutar pazaryerinin hesap kesimindedir." rows={[
      { label: 'Tahmini komisyon', value: commission.coveredRevenue > 0 ? formatTl(commission.estimatedCommission) : '—', hint: `Etkin oran ${formatPercent(commission.effectiveRate)}` },
      { label: 'Tahmini hakediş', value: commission.coveredRevenue > 0 ? formatTl(commission.estimatedPayout) : '—', hint: 'Kesinleşmiş hakediş değildir', kind: 'total', tone: commission.coveredRevenue > 0 ? signTone(commission.estimatedPayout) : undefined },
      ...trendyolLedger(data)
    ]} empty="Komisyon bilgisi yok." />
  </div>
}

type LedgerRow = { label: string; value: string; hint?: string; kind?: 'minus' | 'total' | 'result'; tone?: 'good' | 'bad' }

// The tone of a result: green at or above zero, red below.
const signTone = (value: number): 'good' | 'bad' => value < 0 ? 'bad' : 'good'

// A small account: rows one under another, deductions in red with a minus, subtotals and the result ruled above and
// coloured by their sign.
function Ledger({ title, description, rows, empty }: { title: string; description: string; rows: LedgerRow[]; empty: string }) {
  return <Panel title={title} description={description} className="reports-ledger-panel">
    {rows.length === 0 ? <p className="reports-empty">{empty}</p> : <dl className="reports-ledger">{rows.map(row => <div key={row.label} className={[row.kind && `is-${row.kind}`, row.tone && `is-${row.tone}`].filter(Boolean).join(' ') || undefined}>
      <dt>{row.label}{row.hint && <small>{row.hint}</small>}</dt>
      <dd>{row.kind === 'minus' ? `− ${row.value}` : row.value}</dd>
    </div>)}</dl>}
  </Panel>
}

// Trendyol's statement for the period, as rows of the commission account (none without the statement).
function trendyolLedger(data: ReportSales): LedgerRow[] {
  const finance = financeCosts(data)
  if (!finance?.commission || !finance.cargo) return []
  const { commission, cargo } = finance
  return [
    { label: 'Trendyol komisyonu', value: formatTl(commission.combined), hint: commissionNote(finance) },
    { label: 'Hesap kesimi olan sipariş', value: `${formatCount(finance.settledOrders)} / ${formatCount(finance.financeOrders)}`, hint: `Aynı siparişlerin tahmini ${formatTl(commission.estimatedForSettled)}` },
    { label: 'Trendyol kargo', value: formatTl(cargo.combined), hint: cargoNote(cargo) },
    { label: 'Sipariş başına kargo', value: cargo.averagePerOrder === null ? '—' : formatTl(cargo.averagePerOrder), hint: `Son 60 günün ${formatCount(cargo.sampleOrders)} faturalı siparişi` }
  ]
}

// A marketplace's account statement line by line, the marketplace picked above the table. Only Trendyol's statement is
// read so far; another marketplace shows that in words instead of an empty table.
function StatementLines({ data }: { data: ReportSales }) {
  const finance = financeCosts(data)
  const read = Boolean(finance?.commission && finance.cargo)
  const platforms = data.platforms.filter(group => group.known && group.orders > 0).map(group => group.key)
  const [picked, setPicked] = useState(() => read && platforms.includes('TRENDYOL') ? 'TRENDYOL' : platforms[0] ?? 'TRENDYOL')
  const selected = platforms.includes(picked) ? picked : platforms[0] ?? picked
  const rows = selected === 'TRENDYOL' && read && finance ? finance.lines : []
  const toolbar = platforms.length > 1 && <div className="reports-segmented" role="group" aria-label="Pazaryeri">
    {platforms.map(code => <button key={code} type="button" aria-pressed={selected === code} onClick={() => setPicked(code)}>{platformName(code)}</button>)}
  </div>
  return <DataTable<CostLine> key={selected} title={`${platformName(selected)} Cari Hesap Kalemleri`} csvName={`rapor-${selected.toLowerCase()}-cari`} rows={rows} rowKey={row => row.category} initialSort="amount" toolbar={toolbar}
    empty={`${platformName(selected)} cari hesabı henüz okunmuyor; şimdilik yalnız Trendyol'un hesap ekstresi okunuyor.`}
    description="Siparişe bağlı kalemler siparişin dönemine, bağlı olmayanlar işlem tarihine göre; eksi tutar satıcıya ödenendir."
    columns={[
      { key: 'label', header: 'Kalem', value: row => row.label, render: row => <span className="reports-cell-label">{row.label}{!row.isCost && <em className="reports-tag">Masraf Değil</em>}</span> },
      { key: 'amount', header: 'Tutar', value: row => row.amount, render: row => formatTl(row.amount), numeric: true },
      { key: 'linked', header: 'Siparişe Bağlı', value: row => row.orderLinked, render: row => formatTl(row.orderLinked), numeric: true },
      { key: 'unlinked', header: 'Tarihe Göre', value: row => row.unlinked, render: row => formatTl(row.unlinked), numeric: true },
      { key: 'records', header: 'Kayıt', value: row => row.records, render: row => formatCount(row.records), numeric: true }
    ]} />
}

type CostLine = ReportCosts['lines'][number]

// A coverage share with its bar, which fills once when it first comes into view.
function Coverage({ label, rate, detail }: { label: string; rate: number | null; detail: string }) {
  const [ref, shown] = useReveal<HTMLDivElement>()
  const value = Math.max(0, Math.min(1, rate ?? 0))
  return <div ref={ref} className="reports-coverage-item">
    <span>{label}</span>
    <strong>{formatPercent(rate)}</strong>
    <span className="reports-bar-track" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(value * 100)}><span style={{ width: shown ? `${value * 100}%` : '0%' }} /></span>
    <small>{detail}</small>
  </div>
}

// Fatura: the packages by invoice state as one bar beside each platform's counts.
function Invoices({ data }: { data: ReportSales }) {
  const total = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const tone = (key: string) => key === 'INVOICED' ? 'left' : key === 'NOT_INVOICED' ? 'returned' : 'fees'
  const parts: Part[] = data.invoices.buckets.map(bucket => ({ key: bucket.key, label: bucket.label, value: bucket.packages, tone: tone(bucket.key) }))
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">{data.invoices.buckets.map(bucket => <Stat key={bucket.key} label={bucket.label} value={formatCount(bucket.packages)} hint={`${formatPercent(total > 0 ? bucket.packages / total : null)} · ${formatTl(bucket.amount)}`} warn={bucket.key === 'NOT_INVOICED' && bucket.packages > 0} />)}</div>
    <RevealPanel title="Fatura Durumu" description="Panelde kesilmiş fatura, elle seçilen durum ya da pazaryerinin bildirdiği duruma göre; hiçbiri yoksa Bilinmiyor. Ayrıntı Faturalar sayfasında.">
      {shown => total > 0 ? <>
        <StackedBar parts={parts} total={total} shown={shown} label={`Fatura durumu: ${parts.map(part => `${part.label} ${formatCount(part.value)}`).join(', ')}`} />
        <ul className="reports-legend-row">{parts.map(part => <li key={part.key}><i className={`is-${part.tone}`} aria-hidden="true" />{part.label}<b>{formatPercent(part.value / total)}</b></li>)}</ul>
      </> : <p className="reports-empty">Bu dönemde paket yok.</p>}
    </RevealPanel>
    <div><DataTable title="Platformlara Göre Fatura Durumu" csvName="rapor-fatura" rows={data.invoices.platforms} rowKey={row => row.platformCode} initialSort="invoiced" columns={[
      { key: 'platform', header: 'Platform', value: row => platformName(row.platformCode) },
      { key: 'invoiced', header: 'Faturalı', value: row => row.invoiced, render: row => formatCount(row.invoiced), numeric: true },
      { key: 'notInvoiced', header: 'Faturasız', value: row => row.notInvoiced, render: row => formatCount(row.notInvoiced), numeric: true },
      { key: 'unknown', header: unknownLabel, value: row => row.unknown, render: row => formatCount(row.unknown), numeric: true }
    ]} empty="Bu dönemde paket yok." /></div>
  </div>
}
