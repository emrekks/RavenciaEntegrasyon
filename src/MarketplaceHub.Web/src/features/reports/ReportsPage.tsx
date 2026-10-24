import { Fragment, useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import { flushSync } from 'react-dom'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, UiIcon, type UiIconName } from '../../shared/components'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformMultiSelect } from '../../shared/platform-multi-select'
import { ViewTabsWithMore } from '../../shared/view-tabs'
import {
  bySalesChannel, change, desiSourcesNote, downloadCsv, isMarketplace, isSalesChannel, variantColourAndSize, formatCount, formatDay, formatHours, formatNumber, formatPercent, formatTl, formatTlShort, formatTlWhole, moneyOrMissing, platformColor, platformName, reportQuery, sortRows, toCsv, unknownLabel,
  type Change, type CsvColumn, type ReportCosts, type ReportGroup, type ReportProduct, type ReportReason, type ReportSales, type ReportStockRow
} from './report-format'
import { CommissionTariffsPanel, commissionTariffPlatforms } from '../marketplace/integrations/CommissionTariffsPanel'
import { SalesMap } from './SalesMap'
import { Columns, CountUp, Donut, Progress, StackedBar, useReveal, type ColumnPoint, type Part } from './report-visuals'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { thumbnailUrl } from '../../shared/product-thumbnail'
import './reports.css'

// The weekly commission tariffs the reports read a missing commission from, uploaded here per store: the platforms
// with live stores as tabs above, a store picked when the platform has more than one. A platform whose tariff export
// the panel does not read yet says so.
function CommissionTariffs({ data }: { data: ReportSales }) {
  const live = data.channels.filter(channel => !channel.isTest && isMarketplace(channel.platformCode))
  const platforms = [...new Set(live.map(channel => channel.platformCode))].sort(bySalesChannel)
  const [platform, setPlatform] = useState(() => platforms.find(code => commissionTariffPlatforms.has(code)) ?? platforms[0] ?? '')
  const stores = live.filter(channel => channel.platformCode === platform)
  const [storeId, setStoreId] = useState('')
  const store = stores.find(channel => channel.id === storeId) ?? stores[0]
  return <div className="reports-tariffs">
    {platforms.length > 0 && <div className="reports-segmented reports-tariff-platforms" role="group" aria-label="Pazaryeri">
      {platforms.map(code => <button key={code} type="button" aria-pressed={code === platform} onClick={() => { setPlatform(code); setStoreId('') }}>
        <PlatformSquareMark code={code} name={platformName(code)} />{platformName(code)}
      </button>)}
    </div>}
    {!store ? <p className="reports-empty">Komisyon tarifesi yüklenebilecek bir mağaza yok.</p>
      : !commissionTariffPlatforms.has(store.platformCode) ? <Panel title={`${platformName(store.platformCode)} Komisyon Tarifesi`}>
        <p className="reports-empty">{platformName(store.platformCode)} için komisyon tarifesi dosyası henüz okunmuyor; şimdilik Trendyol’un haftalık tarife dosyası yükleniyor.</p>
      </Panel>
      : <>
        {stores.length > 1 && <label className="reports-field"><span>Mağaza</span><select value={store.id} onChange={event => setStoreId(event.target.value)}>
          {stores.map(channel => <option key={channel.id} value={channel.id}>{channel.displayName}</option>)}
        </select></label>}
        <CommissionTariffsPanel key={store.id} connectionId={store.id} platformName={platformName(store.platformCode)} />
      </>}
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
  const page = useRef<HTMLElement>(null)
  // An explanation opens above its row when the screen has room for it there (under the top bar), else under it, and
  // towards the left when it would run off the right edge.
  useEffect(() => {
    const element = page.current
    if (!element) return
    const place = (event: Event) => {
      const host = event.target instanceof Element ? event.target.closest('.reports-tip-host') : null
      const tip = host?.querySelector<HTMLElement>(':scope > .reports-hover-tip')
      if (!host || !tip) return
      const box = host.getBoundingClientRect()
      host.toggleAttribute('data-tip-below', box.top - 72 < tip.offsetHeight + 8)
      host.toggleAttribute('data-tip-end', box.left + tip.offsetWidth > window.innerWidth - 16)
    }
    element.addEventListener('pointerover', place)
    element.addEventListener('focusin', place)
    return () => { element.removeEventListener('pointerover', place); element.removeEventListener('focusin', place) }
  }, [])
  const [range, setRange] = useState(() => lastDays(30))
  // A custom range being picked: the picker shows the first day picked until the second one is, and the report keeps
  // the last whole range meanwhile.
  const [draft, setDraft] = useState<{ from: string; to: string } | null>(null)
  const presets = periodPresets()
  // The preset the range matches, if any; another range is a custom one, shown on the picker.
  const activePreset = presets.find(preset => preset.range.from === range.from && preset.range.to === range.to)?.key ?? null
  const [platforms, setPlatforms] = useState<string[] | null>(null)
  const [granularity, setGranularity] = useState('')
  const [includeTest, setIncludeTest] = useState(false)
  const query = reportQuery({ from: range.from, to: range.to, platforms, granularity, includeTest })
  const report = useQuery({ queryKey: ['reports-sales', query], queryFn: () => hubApi<ReportSales>(`/reports/sales?${query}`), staleTime: 60_000, placeholderData: previous => previous })
  const data = report.data
  const platformOptions = useMemo(() => [...new Set((data?.channels ?? []).map(channel => channel.platformCode))].filter(isSalesChannel).sort(bySalesChannel).map(code => ({ value: code, label: platformName(code) })), [data?.channels])
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

  return <section ref={page} className="content reports-page">
    <div className="page-heading reports-heading">
      <div><p className="eyebrow">Analiz</p><h1>Raporlar</h1><p className="reports-lead">Satış, iade, iptal, kargo ve kârlılık; platform, şehir ve ürün kırılımıyla, önceki dönemle karşılaştırmalı.</p></div>    </div>
    <div className="reports-filters" role="search" aria-label="Rapor filtreleri">
      <div className="reports-field reports-period-field"><span>Dönem</span><div className="reports-period-row">
        <div className="reports-segmented" role="group" aria-label="Hazır dönemler">{presets.map(preset => <button key={preset.key} type="button" aria-pressed={preset.key === activePreset} onClick={() => { setDraft(null); setRange(preset.range) }}>{preset.label}</button>)}</div>
        <div className={`reports-period-custom${activePreset ? '' : ' is-active'}`}><DateRangePicker from={draft?.from ?? range.from} to={draft ? draft.to : range.to} label="Özel dönem"
          onChange={next => { if (next.from && next.to) { setDraft(null); setRange({ from: next.from, to: next.to }) } else setDraft(next) }} /></div>
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
  const margin = moneyBreakdown(data, profitSteps(data))?.margin ?? null
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
// previous: the previous period's value, shown under the pointer instead of the generic "Önceki döneme göre".
function Delta({ delta, invert = false, previous }: { delta: Change | null | undefined; invert?: boolean; previous?: string }) {
  if (!delta) return <em className="reports-delta is-flat" title="Önceki dönemde veri yok">—</em>
  const tone = delta.direction === 'flat' ? 'flat' : (delta.direction === 'up') !== invert ? 'good' : 'bad'
  return <em className={`reports-delta is-${tone}`} title={previous ? `Önceki dönem ${previous}` : 'Önceki döneme göre'}>{delta.direction === 'up' ? '▲' : delta.direction === 'down' ? '▼' : '■'} {delta.text}</em>
}

// A panel's explanation shows under its title while the pointer or the focus is on the title, as the overview's rows
// do; nothing is written under the title.
function Panel({ title, description, actions, children, className }: { title: string; description?: string; actions?: ReactNode; children: ReactNode; className?: string }) {
  const tip = useId()
  return <section className={`reports-panel${className ? ` ${className}` : ''}`}>
    <header>
      {description ? <div className="reports-tip-host reports-panel-title">
        <h2><button type="button" className="reports-tip-trigger" aria-describedby={tip}>{title}</button></h2>
        <span role="tooltip" id={tip} className="reports-hover-tip"><TipText text={description} /></span>
      </div> : <div className="reports-panel-title"><h2>{title}</h2></div>}
      {actions && <div className="reports-panel-actions">{actions}</div>}
    </header>
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
function DataTable<T>({ title, description, rows, columns, rowKey, unknown, expand, csvName, empty = 'Bu dönemde kayıt yok.', initialSort, footer, toolbar, tools }: {
  title: string; description?: string; rows: T[]; columns: Column<T>[]; rowKey: (row: T) => string; unknown?: (row: T) => boolean
  expand?: (row: T) => ReactNode; csvName: string; empty?: string; initialSort?: string; footer?: ReactNode; toolbar?: ReactNode
  // Controls beside the CSV button in the title row (a search).
  tools?: ReactNode
}) {
  const [sort, setSort] = useState<{ key: string; direction: 'asc' | 'desc' }>(() => ({ key: initialSort ?? '', direction: 'desc' }))
  const [open, setOpen] = useState<string | null>(null)
  const isUnknown = unknown ?? (() => false)
  const column = columns.find(item => item.key === sort.key)
  const sorted = column ? sortRows(rows, column.value, sort.direction, isUnknown) : sortRows(rows, () => 0, 'desc', isUnknown)
  const exportCsv = () => downloadCsv(`${csvName}.csv`, toCsv(sorted, columns.map<CsvColumn<T>>(item => ({ header: item.header, value: item.csv ?? item.value }))))
  return <Panel title={title} description={description} actions={<>{tools}<button type="button" className="secondary reports-csv" onClick={exportCsv} disabled={!rows.length}><UiIcon name="download" size={16} />CSV</button></>}>
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

// An explanation shown under the pointer: its first line (before a line break) says what the figure is, in bold, and
// the lines after it how it is worked out.
// Each sentence of the working goes on a line of its own.
function TipText({ text }: { text: string }) {
  const [what, ...how] = text.split('\n')
  const sentences = (lines: string[]) => lines.flatMap(line => line.split(/(?<=\.)\s+(?=[A-ZÇĞİÖŞÜ0-9₺%])/u)).filter(Boolean)
  if (!how.length) return <>{sentences([what]).map(line => <span key={line}>{line}</span>)}</>
  return <><b>{what}</b>{sentences(how).map(line => <span key={line}>{line}</span>)}</>
}

// Genel Bakış: the period's six figures in one strip, its costs round a donut, the way from the sales to the profit
// beside the period's buckets, then the product, order and advertising figures and the returns and operations. Every
// amount comes from one account (moneyBreakdown), so the cards agree with each other and with the profit tab; a row's
// explanation shows while the pointer or the focus is on it. Each card plays its animation once, when first in view.
function Overview({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const money = moneyBreakdown(data, profitSteps(data))
  return <div className="reports-overview">
    <OverviewFigures data={data} money={money} />
    <CostLinesCard data={data} money={money} />
    <div className="reports-overview-row is-two">
      <ProfitBridge data={data} money={money} onMore={onMore} />
      <PeriodCard data={data} />
    </div>
    <div className="reports-overview-row is-three">
      <MetricCard title="Ürün Metrikleri" icon="products" tone="product" rows={productMetrics(data, money)} onMore={onMore} />
      <MetricCard title="Sipariş Metrikleri" icon="cart" tone="order" rows={orderMetrics(data, money)} onMore={onMore} />
      <MetricCard title="Reklam" icon="target" tone="ads" rows={adMetrics(data, money)} onMore={onMore} />
    </div>
    <div className="reports-overview-row is-two">
      <MetricCard title="İade ve İptal" icon="returns" tone="returns" rows={returnMetrics(data)} onMore={onMore} hints={false} />
      <MetricCard title="Operasyon" icon="truck" tone="ops" rows={operationMetrics(data)} onMore={onMore} />
    </div>
  </div>
}

// A board card: its icon and title, its actions, and its content, which learns when the card first came into view so
// its chart can play then.
function Card({ title, icon, actions, className, children }: { title: string; icon?: UiIconName; actions?: ReactNode; className?: string; children: (shown: boolean) => ReactNode }) {
  const [ref, shown] = useReveal<HTMLElement>()
  return <section ref={ref} className={`reports-card${className ? ` ${className}` : ''}${shown ? ' is-shown' : ''}`}>
    <header>
      <span className="reports-card-title">{icon && <span className="reports-card-icon" aria-hidden="true"><UiIcon name={icon} size={16} /></span>}<h2>{title}</h2></span>
      {actions && <div className="reports-card-actions">{actions}</div>}
    </header>
    {children(shown)}
  </section>
}

type Measure = 'netRevenue' | 'orders' | 'soldQuantity'
const measures: Record<Measure, string> = { netRevenue: 'Net Ciro', orders: 'Sipariş', soldQuantity: 'Satılan Adet' }

// A bucket's name with the Turkish endings its explanations need (its, of it, from it, with it, many).
const bucketNames: Record<ReportSales['granularity'], { title: string; unit: string; every: string; its: string; of: string; from: string; with: string; many: string }> = {
  day: { title: 'Günler', unit: 'Gün', every: 'Günlük', its: 'günü', of: 'günün', from: 'gününden', with: 'günüyle', many: 'günler' },
  week: { title: 'Haftalar', unit: 'Hafta', every: 'Haftalık', its: 'haftası', of: 'haftanın', from: 'haftasından', with: 'haftasıyla', many: 'haftalar' },
  month: { title: 'Aylar', unit: 'Ay', every: 'Aylık', its: 'ayı', of: 'ayın', from: 'ayından', with: 'ayıyla', many: 'aylar' }
}
// The measure as what a bucket has: a day's net revenue ("günün net cirosu").
const measureOf: Record<Measure, string> = { netRevenue: 'net cirosu', orders: 'sipariş sayısı', soldQuantity: 'satılan adedi' }

// The period's buckets as columns of net revenue (or orders or units), the best one marked and the previous period's
// beside each under the pointer; under them the best bucket, the average one and how many beat the previous period's.
function PeriodCard({ data }: { data: ReportSales }) {
  const [measure, setMeasure] = useState<Measure>('netRevenue')
  const money = measure === 'netRevenue'
  const noAmount = money && data.summary.ordersWithAmount === 0
  const format = money ? formatTl : formatNumber
  const names = bucketNames[data.granularity]
  const name = (start: string) => data.granularity === 'month' ? `${start.slice(5, 7)}.${start.slice(0, 4)}` : data.granularity === 'week' ? `${formatDay(start)} haftası` : formatDay(start)
  const points: ColumnPoint[] = data.timeSeries.map((point, index) => ({
    key: point.start, short: data.granularity === 'month' ? `${point.start.slice(5, 7)}.${point.start.slice(2, 4)}` : `${point.start.slice(8, 10)}.${point.start.slice(5, 7)}`,
    title: name(point.start), value: point[measure], previous: data.previousTimeSeries[index]?.[measure] ?? null
  }))
  const best = points.reduce<ColumnPoint | null>((top, point) => top === null || point.value > top.value ? point : top, null)
  const average = points.length ? points.reduce((sum, point) => sum + point.value, 0) / points.length : null
  const previous = data.previousTimeSeries.map(point => point[measure])
  const previousAverage = previous.length ? previous.reduce((sum, value) => sum + value, 0) / previous.length : null
  const compared = points.filter(point => point.previous !== null)
  const ahead = compared.filter(point => point.value > (point.previous ?? 0)).length
  const unit = names.unit.toLocaleLowerCase('tr-TR')
  const tip = useId()
  const hasBest = !noAmount && best !== null && best.value > 0
  // The best day with its weekday; a week or a month by its own name.
  const bestWhen = (point: ColumnPoint) => data.granularity === 'day' ? `${point.title}, ${weekdays[(new Date(`${point.key}T12:00:00`).getDay() + 6) % 7]}` : point.title
  return <Card title={names.title} className="reports-card-period"
    actions={<select aria-label="Grafik ölçüsü" value={measure} onChange={event => setMeasure(event.target.value as Measure)}>{(Object.keys(measures) as Measure[]).map(key => <option key={key} value={key}>{measures[key]}</option>)}</select>}>
    {shown => <>
      {noAmount ? <p className="reports-empty">Bu dönemde tutarı olan sipariş yok.</p>
        : <Columns points={points} format={format} axisFormat={money ? formatTlShort : formatNumber} shown={shown} label={`${measures[measure]} dönem içinde`} />}
      {/* Three tiles: the best bucket's figure and its date, the average against the previous period's, and how many
          buckets beat the same bucket of the previous period, drawn as a share of the period. */}
      <dl className="reports-period-facts">
        <div className="reports-tip-host"><dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-best`}>En İyi {names.unit}</button></dt>
          <dd><strong>{hasBest && best ? format(best.value) : '—'}</strong><small>{hasBest && best ? bestWhen(best) : 'Bu dönemde veri yok'}</small></dd>
          <span role="tooltip" id={`${tip}-best`} className="reports-hover-tip"><TipText text={`Dönemin ${measureOf[measure]} en yüksek ${names.its}.\nGrafikte mor sütunla gösterilir.`} /></span></div>
        <div className="reports-tip-host"><dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-average`}>{names.every} Ortalama</button></dt>
          <dd><strong>{noAmount || average === null ? '—' : format(average)}{!noAmount && average !== null && <Delta delta={change(average, previousAverage)} />}</strong>
            <small>{previousAverage === null ? 'Önceki dönemde veri yok' : `Önceki dönem ${format(previousAverage)}`}</small></dd>
          <span role="tooltip" id={`${tip}-average`} className="reports-hover-tip"><TipText text={`Bir ${names.of} ortalama ${measureOf[measure]}.\nDönem toplamı ÷ ${unit} sayısı; önceki dönemin ortalamasıyla karşılaştırılır.`} /></span></div>
        <div className="reports-tip-host"><dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-ahead`}>Önceki Döneme Göre</button></dt>
          <dd><strong>{noAmount || !compared.length ? '—' : `${ahead} ${unit} önde`}</strong>
            {!noAmount && compared.length > 0 && <Progress value={ahead / compared.length} shown={shown} tone="good" />}
            <small>{noAmount || !compared.length ? 'Önceki dönemde veri yok' : `${compared.length - ahead} ${unit} geride; toplam ${compared.length} ${unit}`}</small></dd>
          <span role="tooltip" id={`${tip}-ahead`} className="reports-hover-tip"><TipText text={`Önceki dönemin aynı sıradaki ${names.from} yüksek geçen ${names.many}.\nDönemin ilk ${names.its} önceki dönemin ilk ${names.with}, ikincisi ikincisiyle karşılaştırılır.`} /></span></div>
      </dl>
    </>}
  </Card>
}

type MoneyPart = Part & { note?: string }
// One account from the sales to the profit, every figure taken from the one before it: the sales, the deductions
// (returns and the marketplaces' commission, cargo and fees), what is left after them, the product cost entered and the
// profit (null until a cost is entered) with its margin over the sales.
type Money = {
  // The net VAT of the Trendyol orders, off the profit after the deductions (null when the statement is not read).
  tax: number | null
  total: number; deductions: MoneyPart[]; deducted: number; left: number; cost: number | null; costCoverage: number | null
  profit: number | null; margin: number | null; leftNote: string; profitNote: string; description: string
  // What the account could not take off: the commission when none is known, and the platforms whose cargo and fees are
  // not read (with Trendyol's statement) or, without it, the cost lines read nowhere.
  gaps: { commission: boolean; platforms: string[]; lines: string[] }
}

const deductionTones: Record<string, string> = { returned: 'returned', commission: 'commission', cargo: 'cargo', fees: 'fees' }

function moneyBreakdown(data: ReportSales, flow: ProfitSteps): Money | null {
  if (!flow.steps) return null
  const now = data.summary
  const finance = financeCosts(data)
  const deductions: MoneyPart[] = flow.steps.filter(step => step.kind === 'minus' && step.value !== null && step.value > 0)
    .map(step => ({ key: step.key, label: step.label, value: step.value ?? 0, tone: deductionTones[step.key] ?? 'fees', note: step.note }))
  const deducted = deductions.reduce((sum, part) => sum + part.value, 0)
  const left = now.grossSales - deducted
  const cost = data.cost.costedQuantity > 0 ? data.cost.costTotal : null
  const tax = finance?.netVat?.net ?? null
  const profit = cost === null ? null : left - (tax ?? 0) - cost
  const unread = finance ? unreadFeeCodes(data) : []
  const commissionKnown = flow.steps.some(step => step.key === 'commission' && step.value !== null)
  const gaps = [
    commissionKnown ? null : 'komisyon bilinmiyor',
    finance ? (unread.length ? `${unread.map(platformName).join(', ')} kargo ve hizmet bedeli` : null) : unreadCostLines.join(', ').toLocaleLowerCase('tr-TR')
  ].filter(Boolean)
  const coverage = data.cost.coverage
  return {
    gaps: { commission: !commissionKnown, platforms: unread, lines: finance ? [] : unreadCostLines },
    total: now.grossSales, deductions, deducted, left, tax, cost, costCoverage: cost === null ? null : coverage, profit,
    margin: profit !== null && now.grossSales > 0 ? profit / now.grossSales : null, description: flow.description,
    leftNote: `Satıştan iade ve pazaryeri kesintileri düşüldükten sonra kalan; ürün maliyeti hariç, tahminidir.${gaps.length ? ` Düşülmeyenler: ${gaps.join('; ')}.` : ''}`,
    profitNote: cost === null ? 'Ürün kartında varyant maliyeti girildiğinde kâr burada görünür.'
      : `Masraflar sonrasından ürün maliyeti düşülerek bulunur; tahminidir.${(coverage ?? 0) < 1 ? ` Satılan adetlerin ${formatPercent(coverage)} kadarının maliyeti girili; kalanının maliyeti düşülmedi.` : ''}`
  }
}

// How much of the cost is entered, in one sentence.
const costCoverageText = (coverage: number | null) => (coverage ?? 0) >= 1 ? 'Satılan tüm ürünlerin maliyeti girili.'
  : `Satılan adetlerin ${formatPercent(coverage)} kadarının maliyeti girili; kalanının maliyeti düşülmedi.`

const signedTl = (value: number) => value < 0 ? `− ${formatTl(-value)}` : formatTl(value)

// A figure that counts up when first seen; a negative one is written with its minus at once (a count never shows a
// value below zero).
function Figure({ value, format, shown }: { value: number; format: (value: number) => string; shown: boolean }) {
  return value < 0 ? <>− {format(-value)}</> : <CountUp value={value} format={format} active={shown} />
}

// A figure of a strip: its value (or a word, as a province's name), its change against the previous period with the
// previous value under it, and how it is worked out, shown under the pointer.
type Kpi = {
  key: string; label: string; value: number | null; format?: (value: number) => string; text?: string; empty?: string
  delta?: Change | null; invert?: boolean; note: string; tip: string; tone?: 'good' | 'bad' | 'warn'
}

// A strip of figures divided by hairlines; the figures count up once when the strip first comes into view. In a strip
// that compares, a figure without a previous value keeps the change's line empty, so every note starts on one line.
// The explanation of the figure under the pointer (or the focus) opens as a card under the strip, level with its cell
// and kept inside the strip's width; each cell keeps its explanation as text for screen readers too.
function KpiStrip({ label, items }: { label: string; items: Kpi[] }) {
  const [ref, shown] = useReveal<HTMLDivElement>()
  const tip = useId()
  const [open, setOpen] = useState<{ key: string; left: number; top: number; width: number } | null>(null)
  const compares = items.some(item => item.delta !== undefined)
  const show = (key: string, cell: HTMLElement) => {
    const strip = cell.parentElement
    if (!strip) return
    const width = Math.min(300, strip.clientWidth)
    setOpen({ key, width, top: strip.offsetTop + cell.offsetTop + cell.offsetHeight + 8, left: Math.max(0, Math.min(cell.offsetLeft, strip.clientWidth - width)) })
  }
  const hide = () => setOpen(null)
  const current = open ? items.find(item => item.key === open.key) : undefined
  return <div className="reports-kpis-wrap">
    <div ref={ref} className={`reports-kpis is-${items.length}`} role="group" aria-label={label}>{items.map(item => {
      const id = `${tip}-${item.key}`
      const empty = item.empty ?? '—'
      const missing = item.value === null && item.text === undefined
      // Whole counts stay whole while they count up; money shows whole liras (the kuruş rounded) in a strip.
      const format = item.format === formatTl ? formatTlWhole : item.format ?? (Number.isInteger(item.value) ? (value: number) => formatNumber(Math.round(value)) : formatNumber)
      return <div key={item.key} className={`${missing && empty !== '—' ? 'is-missing' : ''}${item.text !== undefined ? ' is-text' : ''}${item.tone ? ` is-${item.tone}` : ''}${open?.key === item.key ? ' is-open' : ''}`.trim() || undefined}
        onMouseEnter={event => show(item.key, event.currentTarget)} onMouseLeave={hide}>
        <button type="button" className="reports-tip-trigger reports-kpi-label" aria-describedby={id}
          onFocus={event => event.currentTarget.parentElement && show(item.key, event.currentTarget.parentElement)} onBlur={hide}>{item.label}</button>
        <strong>{item.text ?? (item.value === null ? empty : <Figure value={item.value} format={format} shown={shown} />)}</strong>
        {compares && <span className="reports-kpi-change">{item.delta !== undefined && <Delta delta={item.delta} invert={item.invert} />}</span>}
        <span className="reports-kpi-note">{wholeLiras(item.note)}</span>
        <span id={id} className="reports-sr">{item.tip.replace('\n', ' ')}</span>
      </div>
    })}</div>
    {open && current && <div key={current.key} className="reports-hover-tip is-floating" aria-hidden="true" style={{ top: open.top, left: open.left, width: open.width }}><TipText text={current.tip} /></div>}
  </div>
}

// The amounts written in a strip's note in whole liras, as its figures are ("₺422.394,54" → "₺422.395").
const wholeLiras = (text: string) => text.replace(/₺([\d.]+),(\d{2})/gu, (_, whole: string, kurus: string) => formatTlWhole(Number(whole.replace(/\./gu, '')) + Number(kurus) / 100))

// "Önceki dönem …", or that the previous period has nothing to compare.
const previousText = (value: string | null) => value === null || value === '—' ? 'Önceki dönemde veri yok' : `Önceki dönem ${value}`
const percent = (value: number) => formatPercent(value)

// The previous period's account, worked out as the period's is; null when the server sent none, or when one period
// read Trendyol's statement and the other did not (their profits would not be counted alike).
function previousMoney(data: ReportSales): Money | null {
  const account = data.previousAccount
  if (!account) return null
  const earlier: ReportSales = { ...data, summary: data.previousSummary, commission: account.commission, cost: account.cost, costs: account.costs }
  if ((financeCosts(earlier) === null) !== (financeCosts(data) === null)) return null
  return moneyBreakdown(earlier, profitSteps(earlier))
}

// The period's six figures in one strip: the net revenue, the estimated net profit, its margin over the sales and
// over the product cost, the orders and the units the customers kept, each against the previous period.
function OverviewFigures({ data, money }: { data: ReportSales; money: Money | null }) {
  const now = data.summary
  const then = data.previousSummary
  const earlier = previousMoney(data)
  const noAmount = now.ordersWithAmount === 0
  const profit = money?.profit ?? null
  const onCost = profit !== null && money?.cost ? profit / money.cost : null
  const earlierProfit = earlier?.profit ?? null
  const earlierOnCost = earlierProfit !== null && earlier?.cost ? earlierProfit / earlier.cost : null
  const items: Kpi[] = [
    { key: 'net', label: 'Net Ciro', value: noAmount ? null : now.netRevenue, format: formatTl, empty: 'Tutar yok', delta: noAmount ? null : change(now.netRevenue, then.netRevenue),
      note: previousText(then.ordersWithAmount > 0 ? formatTl(then.netRevenue) : null), tip: 'Satıştan iadeler düştükten sonra kalan ciro.\nİptal edilen siparişler sayılmaz.' },
    { key: 'profit', label: profit !== null && profit < 0 ? 'Tahmini Zarar' : 'Net Kâr', value: profit, format: formatTl, empty: noAmount ? 'Tutar yok' : 'Maliyet girilmemiş',
      delta: change(profit, earlierProfit), note: previousText(earlierProfit === null ? null : signedTl(earlierProfit)),
      tip: profit === null ? 'Ürün maliyeti girilince hesaplanır.\nÜrün kartında varyantların maliyetini girin.' : 'Tüm masraflar ve ürün maliyeti düştükten sonra kalan tahmini kâr.\nSatış − iade − kargo − pazaryeri masrafları − ürün maliyeti.',
      tone: profit === null ? undefined : profit < 0 ? 'bad' : profit > 0 ? 'good' : undefined },
    { key: 'margin', label: 'Kâr Marjı', value: money?.margin ?? null, format: percent, delta: change(money?.margin ?? null, earlier?.margin ?? null, 'rate'),
      note: previousText(formatPercent(earlier?.margin)), tip: 'Satışın yüzde kaçının kâr olarak kaldığı.\nTahmini net kâr ÷ satış tutarı.', tone: (money?.margin ?? 0) < 0 ? 'bad' : undefined },
    { key: 'cost', label: 'Maliyete Göre Kâr', value: onCost, format: percent, delta: change(onCost, earlierOnCost, 'rate'),
      note: previousText(formatPercent(earlierOnCost)), tip: 'Ürüne harcanan her liranın ne kadar kâr getirdiği.\nTahmini net kâr ÷ ürün maliyeti.', tone: (onCost ?? 0) < 0 ? 'bad' : undefined },
    { key: 'orders', label: 'Sipariş', value: now.orders, format: formatCount, delta: change(now.orders, then.orders), note: previousText(formatCount(then.orders)), tip: 'Dönemde gelen sipariş sayısı.\nİptal edilen siparişler sayılmaz.' },
    { key: 'kept', label: 'Net Satılan Adet', value: now.keptQuantity, delta: change(now.keptQuantity, then.keptQuantity), note: previousText(formatNumber(then.keptQuantity)),
      tip: now.soldQuantity > 0 ? `Müşteride kalan ürün adedi.\n${formatNumber(now.soldQuantity)} satılan adetten ${formatNumber(now.returnedQuantity)} iade düşüldü.` : 'Bu dönemde satış yok.' }
  ]
  return <KpiStrip label="Dönemin göstergeleri" items={items} />
}

type OverviewCost = { key: string; label: string; value: number | null; tone: string; note: string }

// Every cost of the period: the product cost and the account's deductions, the fees each on their own line, the
// categories Trendyol's statement has no record of at zero and what is not read as "—". The lines add up to the
// account's deductions plus the product cost; the largest come first.
const costMeaning: Record<string, string> = {
  cost: 'Satılan ürünlerin alış maliyeti.',
  commission: 'Pazaryerinin her satıştan aldığı komisyon.',
  cargo: 'Siparişlerin kargo gönderim ücreti.',
  returned: 'Müşterilerin iade ettiği ürünlerin tutarı; satıştan düşülür.',
  fee: 'Pazaryerlerinin sipariş ya da satış başına aldığı hizmet bedelleri.',
  payment: 'Shopify\'da kartlı ödemenin iyzico ve Shopify kesintisi.',
  stoppage: 'Satıştan kesilen %1 e-ticaret stopajı (vergi kesintisi).',
  ads: 'Trendyol\'daki reklam harcaması.',
  penalty: 'Trendyol\'un kestiği cezalar (geç kargo, iptal gibi).',
  early: 'Hakedişi erken almak için ödenen kesinti.',
  otherDeduction: 'Trendyol\'un türünü belirtmeden kestiği diğer kesinti faturaları.',
  vat: 'Devlete ödenecek KDV farkı.',
  fees: 'Pazaryerlerinin hizmet bedeli, stopaj, reklam ve benzeri kesintileri; Shopify\'da ödeme komisyonu.',
  marketplace: 'Pazaryerinin komisyonu ve hizmet bedelleri.'
}
const explain = (key: string, how: string) => `${costMeaning[key] ?? ''}\n${how}`

function costLines(data: ReportSales, money: Money): OverviewCost[] {
  const finance = financeCosts(data)
  const part = (key: string) => money.deductions.find(item => item.key === key)
  const notRead = 'Bu seçimde hesaplanamadı.'
  const commission = part('commission')
  const cargo = part('cargo')
  const lines: OverviewCost[] = [
    { key: 'cost', label: 'Ürün Maliyeti', value: money.cost, tone: 'cost', note: explain('cost', money.cost === null ? 'Ürün kartında varyantların maliyeti girildiğinde hesaplanır.' : costCoverageText(money.costCoverage)) },
    { key: 'commission', label: commission?.label ?? 'Komisyon', value: commission?.value ?? null, tone: 'commission', note: explain('commission', commission?.note || 'Siparişte komisyon gelmedi, komisyon tarifesi de yüklenmedi.') },
    { key: 'cargo', label: 'Kargo', value: finance ? cargo?.value ?? 0 : null, tone: 'cargo', note: explain('cargo', finance ? cargo?.note || 'Bu dönemde kargo yok.' : notRead) },
    { key: 'returned', label: 'İade Edilen Tutar', value: part('returned')?.value ?? 0, tone: 'returned', note: explain('returned', `Bu dönemde ${formatNumber(data.summary.returnedQuantity)} adet ürün iade edildi.`) },
    // "Diğer Kesinti" (deduction invoices no rule names) and "Ödeme Komisyonu" (Shopify only) are listed only when there is one.
    ...feeCategories.filter(fee => !['OTHER_DEDUCTION', 'PAYMENT_FEE'].includes(fee.category) || finance?.lines.some(item => item.category === fee.category && item.amount !== 0)).map(fee => {
      const line = finance?.lines.find(item => item.category === fee.category)
      return { key: fee.key, label: fee.category === 'EARLY_PAYMENT' ? 'Erken Ödeme Kesintisi' : fee.label, value: finance ? line?.amount ?? 0 : null, tone: fee.key,
        note: explain(fee.key, !finance ? notRead : line ? feeNote(line) : 'Bu dönemde kaydı yok.') }
    }),
    { key: 'vat', label: 'Net KDV', value: finance?.netVat?.net ?? null, tone: 'vat', note: explain('vat', finance?.netVat
      ? `Satışların KDV'si ${formatTl(finance.netVat.salesVat)}; bundan ürün maliyetindeki KDV ${formatTl(finance.netVat.productCostVat)} ve masraflardaki (komisyon, kargo, hizmet bedeli) KDV ${formatTl(finance.netVat.expenseVat)} düşülür. Tahminidir.${finance.netVat.net < 0 ? ' Eksi çıktığı için devreden KDV\'dir ve kârı artırır.' : ''}`
      : notRead) }
  ]
  const known = lines.filter(line => line.value !== null && line.value > 0).sort((a, b) => (b.value ?? 0) - (a.value ?? 0))
  return [...known, ...lines.filter(line => line.value !== null && line.value <= 0), ...lines.filter(line => line.value === null)]
}

// Masraf Kalemleri: every cost round one donut with the total and its share of the sales in the middle; each line
// with its amount and its share of the costs, and how it was read under the pointer.
function CostLinesCard({ data, money }: { data: ReportSales; money: Money | null }) {
  const tip = useId()
  // The cost under the pointer, on the ring or in the list: the ring shows it in its middle instead of the total.
  const [active, setActive] = useState<string | null>(null)
  if (!money) return <Card title="Masraf Kalemleri" className="reports-card-costlines">{() => <p className="reports-empty">Bu dönemde tutarı olan sipariş yok.</p>}</Card>
  const lines = costLines(data, money)
  const total = money.deducted + (money.cost ?? 0)
  const parts: Part[] = lines.filter(line => line.value !== null && line.value > 0).map(line => ({ key: line.key, label: line.label, value: line.value ?? 0, tone: line.tone }))
  const picked = parts.find(part => part.key === active) ?? null
  const pick = (key: string) => parts.some(part => part.key === key) ? setActive(key) : undefined
  return <Card title="Masraf Kalemleri" className="reports-card-costlines">
    {shown => <div className="reports-costs">
      <Donut parts={parts} shown={shown} active={active} onActive={setActive} label={`Masraflar: ${parts.map(part => `${part.label} ${formatPercent(part.value / total)}`).join(', ')}`}>
        {/* A new key each time, so the middle fades in afresh. */}
        <div key={picked?.key ?? 'total'} className="reports-donut-swap" aria-live="polite">{picked ? <>
          <small>{picked.label}</small>
          <strong>{formatTlWhole(picked.value)}</strong>
          <span>masrafların {formatPercent(total > 0 ? picked.value / total : null)} kadarı</span>
        </> : <>
          <small>Toplam masraf</small>
          <strong>{formatTlWhole(total)}</strong>
          <span>satışın {formatPercent(money.total > 0 ? total / money.total : null)} kadarı</span>
        </>}</div>
      </Donut>
      {/* A line's name is a button like the panel's "?": a tap on a phone or Tab on a keyboard opens its explanation too. */}
      <ul className="reports-costlines">{lines.map(line => <li key={line.key} className={`reports-tip-host${line.value === null || line.value === 0 ? ' is-zero' : ''}${line.key === picked?.key ? ' is-active' : ''}`}
        onMouseEnter={() => pick(line.key)} onMouseLeave={() => setActive(null)}>
        <i className={`is-${line.value ? line.tone : 'none'}`} aria-hidden="true" />
        <button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${line.key}`} onFocus={() => pick(line.key)} onBlur={() => setActive(null)}>{line.label}</button>
        <b>{line.value === null ? '—' : signedTl(line.value)}</b>
        <em>{line.value !== null && line.value > 0 && total > 0 ? formatPercent(line.value / total) : ''}</em>
        <span role="tooltip" id={`${tip}-${line.key}`} className="reports-hover-tip"><TipText text={line.note} /></span>
      </li>)}</ul>
    </div>}
  </Card>
}

type BridgeRow = { key: string; label: string; from: number; to: number; kind: 'total' | 'minus' | 'end'; note: string }

// Kâr Köprüsü: the sales on one scale, each deduction taken off where the one before it ended (the returns, the cargo,
// the marketplace's commission and fees, the withholding tax), then the product cost, down to the estimated net profit
// (or, with no cost entered, what the deductions leave). The same account as the profit tab; the sales open their
// platforms in a window.
function ProfitBridge({ data, money, onMore }: { data: ReportSales; money: Money | null; onMore: OpenDetail }) {
  const [open, setOpen] = useState(false)
  const tip = useId()
  const more = <MoreButton title="Kâr Köprüsü" onClick={() => onMore('cost')} compact />
  if (!money) return <Card title="Kâr Köprüsü" className="reports-card-bridge" actions={more}>{() => <p className="reports-empty">Bu dönemde tutarı olan sipariş yok.</p>}</Card>
  const finance = financeCosts(data)
  const part = (key: string) => money.deductions.find(item => item.key === key)
  const fees = finance ? feeLines(finance) : []
  const stoppage = fees.find(fee => fee.key === 'stoppage')
  const otherFees = fees.filter(fee => fee.key !== 'stoppage')
  const commission = part('commission')
  const cargo = part('cargo')
  const taken = [
    { key: 'returned', label: 'İade Edilen Tutar', value: part('returned')?.value ?? 0, note: explain('returned', `Bu dönemde ${formatNumber(data.summary.returnedQuantity)} adet ürün iade edildi.`) },
    { key: 'cargo', label: 'Kargo', value: cargo?.value ?? 0, note: explain('cargo', cargo?.note ?? '') },
    { key: 'marketplace', label: 'Pazaryeri Masrafları', value: (commission?.value ?? 0) + otherFees.reduce((sum, fee) => sum + fee.value, 0),
      note: explain('marketplace', [commission ? `${commission.label} ${formatTl(commission.value)}` : null, ...otherFees.map(fee => `${fee.label} ${formatTl(fee.value)}`)].filter(Boolean).join(', ') + '.') },
    { key: 'stoppage', label: 'Stopaj', value: stoppage?.value ?? 0, note: explain('stoppage', 'KDV hariç yurt içi satıştan kesilir; hesap kesimi henüz olmayan siparişlerde tahmini.') },
    { key: 'vat', label: 'Net KDV', value: money.tax ?? 0, note: explain('vat', money.tax !== null && money.tax < 0 ? 'Masraflardaki KDV satışların KDV\'sinden fazla; fark devreden KDV\'dir ve kârı artırır.' : 'Satışların KDV\'sinden ürün maliyetindeki ve masraflardaki KDV düşülür. Tahminidir.') },
    { key: 'cost', label: 'Ürün Maliyeti', value: money.cost ?? 0, note: explain('cost', costCoverageText(money.costCoverage)) }
  ].filter(item => item.value !== 0)
  const rows: BridgeRow[] = [{ key: 'sales', label: 'Satış Tutarı', from: 0, to: money.total, kind: 'total', note: 'Müşterilerin ödediği toplam tutar.\nİptaller hariç, iadeler düşülmeden. Platformlara göre görmek için tıklayın.' }]
  let at = money.total
  for (const item of taken) {
    rows.push({ key: item.key, label: item.label, from: at - item.value, to: at, kind: 'minus', note: item.note })
    at -= item.value
  }
  const result = money.profit ?? money.left
  // What the account could not take off is said on the result as well, not only in the deductions' own notes.
  const leftOut = money.leftNote.split(' Düşülmeyenler: ')[1]
  rows.push({ key: 'result', label: money.profit === null ? 'Masraflar Sonrası' : result < 0 ? 'Tahmini Zarar' : 'Tahmini Net Kâr', from: 0, to: result, kind: 'end',
    note: money.profit === null ? `Masraflar düştükten sonra kalan tutar.\nÜrün maliyeti girilmediği için kâr hesaplanamadı.${leftOut ? ` Düşülmeyenler: ${leftOut}` : ''}`
      : `Tüm masraflar ve ürün maliyeti düştükten sonra kalan tahmini kâr.\nNet marj ${formatPercent(money.margin)}.${(money.costCoverage ?? 1) < 1 ? ` ${costCoverageText(money.costCoverage)}` : ''}${leftOut ? ` Düşülmeyenler: ${leftOut}` : ''}` })
  const low = Math.min(0, ...rows.flatMap(row => [row.from, row.to]))
  const span = Math.max(1, Math.max(money.total, ...rows.flatMap(row => [row.from, row.to])) - low)
  return <Card title="Kâr Köprüsü" className="reports-card-bridge" actions={more}>
    {shown => <>
      <ol className="reports-bridge">{rows.map((row, index) => {
        const start = Math.min(row.from, row.to)
        const width = Math.abs(row.to - row.from) / span * 100
        return <li key={row.key} className={`reports-tip-host is-${row.kind}${row.kind === 'end' && row.to < 0 ? ' is-negative' : ''}`}>
          <span className="reports-bridge-label">{row.key === 'sales'
            ? <button type="button" onClick={() => setOpen(true)} aria-label="Satış tutarı: platformlara göre aç" aria-describedby={`${tip}-${row.key}`}>{row.label}<UiIcon name="chevronRight" size={14} /></button>
            : <button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${row.key}`}>{`${row.kind === 'minus' ? (row.to < row.from ? '+ ' : '− ') : ''}${row.label}`}</button>}</span>
          <span className="reports-bridge-track" aria-hidden="true"><i style={{ left: `${(start - low) / span * 100}%`, width: shown ? `${Math.max(0.5, width)}%` : '0%', transitionDelay: `${index * 80}ms` }} /></span>
          <b>{row.kind === 'minus' ? `${row.to < row.from ? '+' : '−'} ${formatTl(Math.abs(row.to - row.from))}` : signedTl(row.to)}</b>
          <span role="tooltip" id={`${tip}-${row.key}`} className="reports-hover-tip"><TipText text={row.note} /></span>
        </li>
      })}</ol>
      <EstimateGaps money={money} shown={shown} />
      {/* The platforms open over the page, so nothing under the card moves. */}
      <Modal open={open} onClose={() => setOpen(false)} className="reports-journey-modal" title="Satış Tutarı"><SalesDetail data={data} /></Modal>
    </>}
  </Card>
}

// What an estimate leaves out, always written and not only under the pointer: how much of the cost is entered (or
// that none is), whose cargo and fees are not taken off, and an unknown commission. Each row names the gap on the left
// with how to read it, and its measure on the right; nothing is drawn when nothing is left out.
function EstimateGaps({ money, shown }: { money: Money; shown: boolean }) {
  const { gaps } = money
  const partly = money.cost !== null && (money.costCoverage ?? 1) < 1
  if (!partly && money.cost !== null && !gaps.platforms.length && !gaps.lines.length && !gaps.commission) return null
  return <dl className="reports-bridge-gaps" aria-label="Tahminin kapsamı">
    {money.cost === null && <div><dt>Ürün Maliyeti<small>Ürün kartında girilmedi</small></dt><dd><span>Kâr hesaplanamadı</span></dd></div>}
    {partly && <div><dt>Maliyeti Girili<small>Satılan adetlerin payı</small></dt><dd>
      <Progress value={money.costCoverage} shown={shown} /><b>{formatPercent(money.costCoverage)}</b>
    </dd></div>}
    {gaps.platforms.length > 0 && <div><dt>Kargo ve Hizmet Bedeli<small>Düşülmedi</small></dt><dd>
      <span className="reports-bridge-chips">{gaps.platforms.map(code => <span key={code}><PlatformSquareMark code={code} name={platformName(code)} />{platformName(code)}</span>)}</span>
    </dd></div>}
    {gaps.lines.length > 0 && <div><dt>Düşülmeyen<small>Cari hesap okunmadı</small></dt><dd><span>{gaps.lines.join(', ')}</span></dd></div>}
    {gaps.commission && <div><dt>Komisyon<small>Düşülmedi</small></dt><dd><span>Bilinmiyor</span></dd></div>}
  </dl>
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

// The account from the sales to the profit, line by line; the overview's window and the profit tab show the same.
function ProfitLedger({ money }: { money: Money }) {
  const tip = useId()
  const amount = (value: number) => value < 0 ? `− ${formatTl(-value)}` : formatTl(value)
  const share = (value: number) => `Satış tutarının ${money.total > 0 ? formatPercent(Math.abs(value) / money.total) : '—'} kadarı.`
  const row = (key: string, className: string | undefined, label: string, value: string, note: string, extra?: ReactNode) =>
    <div key={key} className={`reports-tip-host${className ? ` ${className}` : ''}`}>
      <dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${key}`}>{label}</button>{extra}</dt>
      <dd>{value}</dd>
      <span role="tooltip" id={`${tip}-${key}`} className="reports-hover-tip"><TipText text={note} /></span>
    </div>
  return <dl className="reports-ledger">
    {row('sales', undefined, 'Satış Tutarı', formatTl(money.total), 'Müşterilerin ödediği toplam tutar.\nİptaller hariç, iadeler düşülmeden.')}
    {money.deductions.map(part => row(part.key, 'is-minus', part.label, `− ${formatTl(part.value)}`, `${costMeaning[part.key] ?? part.label}\n${share(part.value)}`))}
    {row('left', `is-total is-${money.left < 0 ? 'bad' : 'good'}`, 'Masraflar Sonrası', amount(money.left), `Satıştan iade ve pazaryeri masrafları düştükten sonra kalan.\n${share(money.left)}`)}
    {money.tax !== null && row('vat', money.tax < 0 ? undefined : 'is-minus', 'Net KDV', money.tax < 0 ? `+ ${formatTl(-money.tax)}` : `− ${formatTl(money.tax)}`,
      `${costMeaning.vat}\n${share(money.tax)}${money.tax < 0 ? ' Eksi çıktığı için devreden KDV\'dir ve kârı artırır.' : ''}`)}
    {money.cost !== null && row('cost', 'is-minus', 'Ürün Maliyeti', `− ${formatTl(money.cost)}`, `${costMeaning.cost}\n${share(money.cost)}`)}
    {money.profit !== null && row('profit', `is-result is-${money.profit < 0 ? 'bad' : 'good'}`, money.profit < 0 ? 'Tahmini Zarar' : 'Tahmini Net Kâr', amount(money.profit),
      `Tüm masraflar ve ürün maliyeti düştükten sonra kalan tahmini kâr.\n${share(money.profit)} Bu, net marjdır.`, <small>Net marj {formatPercent(money.margin)}</small>)}
  </dl>
}

type Metric = { key: string; label: string; value: string; note: string; delta?: Change | null; invert?: boolean; previous?: string; tone?: 'warn' | 'bad'; detail?: DetailKey }

// A card of figures as rows: the name, and the value with its change when it has one. A row's explanation shows while
// the pointer or the focus is on it (not with hints off), and a row that has its own tab opens it.
function MetricCard({ title, icon, tone, rows, onMore, hints = true }: { title: string; icon: UiIconName; tone: string; rows: Metric[]; onMore: OpenDetail; hints?: boolean }) {
  const tip = useId()
  return <Card title={title} icon={icon} className={`reports-card-metrics is-${tone}`}>
    {() => <ul className="reports-metrics">{rows.map(row => {
      const id = `${tip}-${row.key}`
      const detail = row.detail
      const body = <>
        <span className="reports-metric-label">{row.label}</span>
        <span className="reports-metric-value">{row.delta !== undefined && <Delta delta={row.delta} invert={row.invert} previous={row.previous} />}<strong>{row.value}</strong></span>
      </>
      return <li key={row.key} className={`reports-tip-host${row.tone ? ` is-${row.tone}` : ''}`}>
        {detail ? <button type="button" className="reports-metric" onClick={() => onMore(detail)} aria-describedby={hints ? id : undefined}>{body}</button>
          : <button type="button" className="reports-metric is-static" aria-describedby={hints ? id : undefined}>{body}</button>}
        {hints && <span role="tooltip" id={id} className="reports-hover-tip"><TipText text={row.note} /></span>}
      </li>
    })}</ul>}
  </Card>
}

// Ürün Metrikleri: the average sale price and profit of a unit, the commission rate and the marketplace's discount.
function productMetrics(data: ReportSales, money: Money | null): Metric[] {
  const now = data.summary
  const noAmount = now.ordersWithAmount === 0
  const profit = money?.profit ?? null
  const list = now.listAmount ?? 0
  const discount = now.discountAmount ?? null
  const discountKnown = !noAmount && discount !== null && list > 0
  return [
    { key: 'price', label: 'Adet Başına Satış', value: noAmount || now.soldQuantity === 0 ? '—' : formatTl(now.grossSales / now.soldQuantity), note: `Bir ürünün ortalama satış fiyatı.\nSatış tutarı ÷ satılan adet (${formatNumber(now.soldQuantity)} adet).` },
    { key: 'profit', label: 'Adet Başına Kâr', value: profit === null || now.keptQuantity === 0 ? '—' : signedTl(profit / now.keptQuantity), tone: profit !== null && profit < 0 ? 'bad' : undefined,
      note: profit === null ? 'Satılan her üründen kalan tahmini kâr.\nÜrün maliyeti girilince hesaplanır.' : `Satılan her üründen kalan tahmini kâr.\nTahmini net kâr ÷ net satılan adet (${formatNumber(now.keptQuantity)} adet).` },
    { key: 'commission', label: 'Ortalama Komisyon Oranı', value: formatPercent(data.commission.effectiveRate), note: 'Pazaryerinin satıştan aldığı ortalama pay.\nKomisyon ÷ komisyon oranı bilinen satış.' },
    { key: 'discountTotal', label: 'İndirim Toplamı', value: discountKnown ? formatTl(discount ?? 0) : '—', note: 'Liste fiyatından yapılan indirimlerin toplamı.\nİptaller hariç.' },
    { key: 'discount', label: 'Ortalama İndirim Oranı', value: discountKnown ? formatPercent((discount ?? 0) / list) : '—',
      note: discountKnown ? `Ürünlerin liste fiyatına göre ortalama indirimi.\nListe fiyatı toplamı ${formatTl(list)}, yapılan indirim ${formatTl(discount)}; iptaller hariç.` : 'Ürünlerin liste fiyatına göre ortalama indirimi.\nBu dönemde liste fiyatı okunmadı.' }
  ]
}

// Sipariş Metrikleri: what an order brought in and left, its cargo and its units.
function orderMetrics(data: ReportSales, money: Money | null): Metric[] {
  const now = data.summary
  const before = data.previousSummary
  const noAmount = now.ordersWithAmount === 0
  const profit = money?.profit ?? null
  const finance = financeCosts(data)
  const cargo = finance?.cargo ?? null
  const cargoOrders = finance && cargo ? finance.financeOrders + (cargo.platformOrders ?? 0) : 0
  return [
    // Its change goes in the explanation: a badge beside it would wrap the name and put the card's rows out of line.
    { key: 'basket', label: 'Sipariş Başına Satış', value: noAmount ? '—' : formatTl(now.averageOrderValue),
      note: `Bir siparişin ortalama tutarı.\nSatış tutarı ÷ tutarı olan sipariş. Önceki dönem ${formatTl(before.averageOrderValue)}${noAmount ? '' : ` (${change(now.averageOrderValue, before.averageOrderValue)?.text ?? '—'})`}.` },
    { key: 'profit', label: 'Sipariş Başına Kâr', value: profit === null || now.ordersWithAmount === 0 ? '—' : signedTl(profit / now.ordersWithAmount), tone: profit !== null && profit < 0 ? 'bad' : undefined,
      note: profit === null ? 'Her siparişten kalan tahmini kâr.\nÜrün maliyeti girilince hesaplanır.' : 'Her siparişten kalan tahmini kâr.\nTahmini net kâr ÷ tutarı olan sipariş.' },
    { key: 'cargo', label: 'Sipariş Başına Kargo', value: finance && cargo && cargoOrders > 0 ? formatTl(cargo.combined / cargoOrders) : '—',
      note: finance && cargo ? `Bir siparişin ortalama kargo ücreti.\nKargo ÷ ${formatCount(cargoOrders)} sipariş. ${cargoNote(cargo)}` : 'Bir siparişin ortalama kargo ücreti.\nBu seçimde hesaplanamadı.' },
    { key: 'units', label: 'Sipariş Başına Adet', value: now.orders > 0 ? formatNumber(now.soldQuantity / now.orders) : '—', note: `Bir siparişte ortalama kaç ürün olduğu.\n${formatNumber(now.soldQuantity)} adet ÷ ${formatCount(now.orders)} sipariş.` }
  ]
}

// Reklam: what was spent on advertising and what it weighs against the profit and the revenue; what is not read says so.
function adMetrics(data: ReportSales, money: Money | null): Metric[] {
  const finance = trendyolRead(data) ? financeCosts(data) : null
  const spend = finance ? finance.lines.find(line => line.category === 'ADVERTISING')?.amount ?? 0 : null
  const profit = money?.profit ?? null
  const revenue = data.summary.netRevenue
  return [
    { key: 'spend', label: 'Toplam Reklam Harcaması', value: spend === null ? '—' : formatTl(spend),
      note: spend === null ? 'Dönemde reklama harcanan tutar.\nReklam bedeli yalnız Trendyol cari hesabından okunabiliyor.' : 'Dönemde reklama harcanan tutar.\nTrendyol cari hesabındaki reklam bedelleri, işlem tarihine göre. Brandcenter ve influencer kesintileri ayrı okunmuyor.' },
    { key: 'profit', label: 'Reklam / Kâr', value: spend !== null && profit !== null && profit > 0 ? formatPercent(spend / profit, 2) : '—', note: 'Kârın ne kadarının reklama gittiği.\nReklam harcaması ÷ tahmini net kâr.' },
    { key: 'revenue', label: 'Reklam / Ciro', value: spend !== null && revenue > 0 ? formatPercent(spend / revenue, 2) : '—', note: 'Cironun ne kadarının reklama gittiği.\nReklam harcaması ÷ net ciro.' },
    { key: 'roas', label: 'Reklam Cirosu ve ROAS', value: '—', note: 'Reklamdan gelen satış ve reklamın getirisi.\nTrendyol reklam raporu panele bağlı olmadığı için gösterilemiyor.' }
  ]
}

// İade ve İptal: the return rate and claims, the units sent back, then the cancel rate against its warning line.
function returnMetrics(data: ReportSales): Metric[] {
  const now = data.summary
  const before = data.previousSummary
  const warning = data.cancellations.warningRate
  const cancelled = data.cancellations.cancelledAmount
  return [
    { key: 'rate', label: 'İade Oranı', value: formatPercent(now.returnRate), delta: change(now.returnRate, before.returnRate, 'rate'), invert: true, previous: formatPercent(before.returnRate), detail: 'returns',
      note: `Satılan ürünlerin yüzde kaçının iade edildiği.\nİade edilen adet ÷ satılan adet. Önceki dönem ${formatPercent(before.returnRate)}.` },
    { key: 'claims', label: 'İade Talebi', value: formatCount(now.returnClaims), detail: 'returns', note: 'Müşterilerin açtığı iade talepleri.\nReddedilen ve iptal edilen talepler sayılmaz.' },
    { key: 'units', label: 'İade Edilen Adet', value: `${formatNumber(now.returnedQuantity)} adet`, detail: 'returns', note: 'İade edilen ürün adedi.' },
    { key: 'returned', label: 'İade Edilen Tutar', value: formatTl(now.returnedAmount), detail: 'returns', note: 'İade edilen ürünlerin satış tutarı.\nSatıştan düşülür; Masraf Kalemleri\'nde de gösterilir.' },
    { key: 'cargo', label: 'İade Kargo Zararı', value: '—', note: 'İadelerin kargo maliyeti.\nİade kargosu kargo faturasında ayrı okunamadığı için gösterilemiyor.' },
    { key: 'cancel', label: 'İptal Oranı', value: formatPercent(now.cancelRate), tone: (now.cancelRate ?? 0) > warning ? 'warn' : undefined, detail: 'cancellations',
      note: `Siparişlerin yüzde kaçının iptal edildiği.\n${formatCount(now.cancelledOrders)} sipariş iptal edildi; uyarı eşiği ${formatPercent(warning)}.` },
    { key: 'cancelled', label: 'İptal Edilen Sipariş', value: formatCount(now.cancelledOrders), detail: 'cancellations', note: 'İptal edilen sipariş sayısı.' },
    { key: 'amount', label: 'İptal Edilen Tutar', value: cancelled === undefined ? '—' : formatTl(cancelled), detail: 'cancellations', note: 'İptal edilen ürünlerin tutarı.\nListe fiyatıyla; satış tutarına girmez.' }
  ]
}

// Operasyon: how fast and how late the packages left and with whom, what waits to be invoiced or restocked, and the
// customers who came back; each row opens its tab.
function operationMetrics(data: ReportSales): Metric[] {
  const ship = weightedHours(data.cargo, row => row.averageHoursToShip, row => row.measuredShip)
  const late = data.cargo.reduce((sum, row) => sum + row.lateShipments, 0)
  const measuredLate = data.cargo.reduce((sum, row) => sum + row.measuredLate, 0)
  const carrier = [...data.cargo].filter(row => row.known).sort((a, b) => b.packages - a.packages)[0]
  const low = data.stock.bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const packages = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const notInvoiced = data.invoices.buckets.find(bucket => bucket.key === 'NOT_INVOICED')?.packages ?? 0
  return [
    { key: 'ship', label: 'Kargoya Verme Süresi', value: formatHours(ship.hours), detail: 'cargo', note: `Siparişin kargoya verilmesine kadar geçen ortalama süre.\n${ship.measured > 0 ? `${formatCount(ship.measured)} pakette ölçüldü.` : 'Ölçülemedi.'}` },
    { key: 'late', label: 'Geciken Kargo', value: formatCount(late), tone: late > 0 ? 'warn' : undefined, detail: 'cargo',
      note: `Süresinde kargoya verilmeyen paket sayısı.\n${measuredLate > 0 ? `${formatCount(measuredLate)} pakette ölçüldü.` : 'Ölçülemedi.'}` },
    { key: 'carrier', label: 'Kargo Firması', value: carrier?.label ?? '—', detail: 'cargo', note: `En çok paket taşıyan kargo firması.\n${carrier ? `Paketlerin ${formatPercent(carrier.share)} kadarı.` : 'Kargo firması okunamadı.'}` },
    { key: 'invoice', label: 'Faturasız Paket', value: formatCount(notInvoiced), tone: notInvoiced > 0 ? 'warn' : undefined, detail: 'invoices', note: `Faturası henüz kesilmemiş paketler.\n${formatCount(packages)} paket içinde.` },
    { key: 'stock', label: 'Stok Riski', value: `${formatCount(low)} ürün`, tone: low > 0 ? 'warn' : undefined, detail: 'stock', note: 'Stoğu bir haftadan az yetecek çok satan ürünler.\nGünlük satış hızına göre kalan gün 7\'den az.' },
    { key: 'repeat', label: 'Tekrar Alan Müşteri', value: formatPercent(data.customers.repeatRate), detail: 'customers',
      note: `Dönemde birden fazla sipariş veren müşterilerin oranı.\n${formatCount(data.customers.uniqueCustomers)} tekil müşteriden ${formatCount(data.customers.repeatCustomers)} kişi.` }
  ]
}


// Coğrafya: the sales map with its breakdown (its own figures over the map), the provinces ranked by sales and by
// returns, and the full city table.
function Geography({ data }: { data: ReportSales }) {
  return <div className="reports-summary">
    <SalesMap data={data} />
    <Cities data={data} />
  </div>
}

// Ürünler: the best sellers, the most returned, brands and categories as bars, then their tables under two sub-tabs.
function ProductsView({ data, sub, onSub }: { data: ReportSales; sub: ProductSub; onSub: (sub: ProductSub) => void }) {
  const now = data.summary
  const then = data.previousSummary
  const unit = (summary: ReportSales['summary']) => summary.ordersWithAmount > 0 && summary.soldQuantity > 0 ? summary.grossSales / summary.soldQuantity : null
  const selling = (groups: ReportGroup[]) => groups.filter(group => group.known && group.soldQuantity > 0).length
  const items: Kpi[] = [
    { key: 'sold', label: 'Satılan Adet', value: now.soldQuantity, delta: change(now.soldQuantity, then.soldQuantity), note: previousText(formatNumber(then.soldQuantity)), tip: 'Dönemde satılan ürün adedi.\nİptal edilen adetler sayılmaz; iadeler düşülmeden.' },
    { key: 'returned', label: 'İade Edilen Adet', value: now.returnedQuantity, delta: change(now.returnedQuantity, then.returnedQuantity), invert: true,
      note: previousText(formatNumber(then.returnedQuantity)), tip: 'Müşterilerin geri gönderdiği ürün adedi.\nKabul edilen iadeler sayılır.' },
    { key: 'rate', label: 'İade Oranı', value: now.returnRate, format: percent, delta: change(now.returnRate, then.returnRate, 'rate'), invert: true,
      note: previousText(formatPercent(then.returnRate)), tip: 'Satılan ürünlerin yüzde kaçının iade edildiği.\nİade edilen adet ÷ satılan adet.' },
    { key: 'price', label: 'Adet Başına Satış', value: unit(now), format: formatTl, delta: change(unit(now), unit(then)), note: previousText(formatTl(unit(then))), tip: 'Bir ürünün ortalama satış fiyatı.\nSatış tutarı ÷ satılan adet.' },
    { key: 'brands', label: 'Satan Marka', value: selling(data.brands), format: formatCount, note: `${formatCount(selling(data.categories))} kategoride`, tip: 'Dönemde satışı olan marka sayısı.\nÜrün kartında markası girili ürünlerden sayılır.' }
  ]
  return <div className="reports-summary">
    <KpiStrip label="Ürünlerin göstergeleri" items={items} />
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

// Kârlılık: the commission and how much of the sales carry a cost and a commission, each against the previous period;
// the profit account, Trendyol's real costs, and the platforms with their commission. The commission is the account's
// (Trendyol's statement where it was read), so it is the one the profit account under the strip takes off.
function ProfitView({ data }: { data: ReportSales }) {
  const commission = data.commission
  const cost = data.cost
  const earlier = data.previousAccount
  const charged = moneyBreakdown(data, profitSteps(data))?.deductions.find(part => part.key === 'commission') ?? null
  const chargedBefore = previousMoney(data)?.deductions.find(part => part.key === 'commission') ?? null
  const items: Kpi[] = [
    { key: 'commission', label: charged?.label ?? 'Komisyon', value: charged?.value ?? null, format: formatTl, delta: change(charged?.value, chargedBefore?.value), invert: true,
      note: previousText(chargedBefore ? formatTl(chargedBefore.value) : null), tip: `Pazaryerinin her satıştan aldığı komisyon.\n${charged?.note || 'Siparişte komisyon gelmedi, komisyon tarifesi de yüklenmedi.'}` },
    { key: 'rate', label: 'Komisyon Oranı', value: commission.effectiveRate, format: percent, delta: change(commission.effectiveRate, earlier?.commission.effectiveRate, 'rate'), invert: true,
      note: previousText(formatPercent(earlier?.commission.effectiveRate)), tip: 'Pazaryerinin satıştan aldığı ortalama pay.\nTahmini komisyon ÷ komisyon oranı bilinen satış.' },
    { key: 'costed', label: 'Maliyeti Girili Satış', value: cost.coverage, format: percent, delta: change(cost.coverage, earlier?.cost.coverage, 'rate'),
      note: previousText(formatPercent(earlier?.cost.coverage)),
      tip: `Maliyeti girili ürünlerin satıştaki payı.
${formatNumber(cost.keptQuantity)} adetten ${formatNumber(cost.costedQuantity)} adedin maliyeti girili (${formatCount(cost.costedVariants)} varyant). Eksik maliyeti ürün kartındaki Maliyet alanına girin.` },
    { key: 'covered', label: 'Komisyon Kapsamı', value: commission.coverage, format: percent, delta: change(commission.coverage, earlier?.commission.coverage, 'rate'),
      note: previousText(formatPercent(earlier?.commission.coverage)), tip: `Komisyon oranı bilinen cironun payı.
${formatTl(data.summary.netRevenue)} cironun ${formatTl(commission.coveredRevenue)} kadarının oranı biliniyor.` },
    { key: 'both', label: 'Kâr Kapsamı', value: cost.profitCoverage, format: percent, delta: change(cost.profitCoverage, earlier?.cost.profitCoverage, 'rate'),
      note: previousText(formatPercent(earlier?.cost.profitCoverage)), tip: `Hem maliyeti hem komisyonu bilinen satışların payı.
${formatNumber(cost.profitQuantity)} adet; kâr bunlar üzerinden en doğru hesaplanır.` }
  ]
  return <div className="reports-summary">
    <KpiStrip label="Kârlılığın göstergeleri" items={items} />
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
function financeCosts(data: ReportSales): ReportCosts | null { return data.costs?.commission && data.costs.cargo ? data.costs : null }
// Whether Trendyol's statement was read for the period (its cargo and fees are then real or estimated from it).
function trendyolRead(data: ReportSales) { return data.costs?.source === 'trendyol-finance' }

const feeCategories = [
  { category: 'SERVICE_FEE', key: 'fee', label: 'Hizmet Bedeli' },
  { category: 'PAYMENT_FEE', key: 'payment', label: 'Ödeme Komisyonu' },
  { category: 'STOPPAGE', key: 'stoppage', label: 'Stopaj' },
  { category: 'ADVERTISING', key: 'ads', label: 'Reklam' },
  { category: 'PENALTY', key: 'penalty', label: 'Ceza' },
  { category: 'EARLY_PAYMENT', key: 'early', label: 'Erken Ödeme' },
  // A deduction invoice of a kind no rule names: still charged to the seller.
  { category: 'OTHER_DEDUCTION', key: 'otherDeduction', label: 'Diğer Kesinti' }
]

function feeLines(costs: ReportCosts) {
  return feeCategories.flatMap(fee => {
    const line = costs.lines.find(item => item.category === fee.category)
    return line && line.amount !== 0 ? [{ ...fee, value: line.amount }] : []
  })
}

// A fee line's note: how many records were read and how much was estimated for the orders not settled yet.
function feeNote(line: ReportCosts['lines'][number]) {
  const read = line.records > 0 ? `Trendyol cari hesabındaki ${formatCount(line.records)} kayıttan, işlem tarihine göre.` : null
  const estimated = line.estimated ? `${formatTl(line.estimated)} tahmini: Trendyol'da hesap kesimi olmayan siparişler, diğer platformlarda 2026 tarifeleri.` : null
  return [read, estimated].filter(Boolean).join(' ') || 'Bu dönemde kaydı yok.'
}

function commissionNote(costs: ReportCosts) {
  const commission = costs.commission
  if (!commission) return ''
  return commission.realOrders > 0
    ? `${formatCount(commission.realOrders)} siparişin komisyonu Trendyol hesap kesiminden kesin: ${formatTl(commission.real)}. Henüz hesap kesimi olmayanlarınki tahmini: ${formatTl(commission.estimatedForUnsettled)}.`
    : 'Hesap kesimi henüz yok; tümü siparişteki komisyon oranından tahmin edildi.'
}

function cargoNote(cargo: NonNullable<ReportCosts['cargo']>) {
  const platforms = cargo.platformOrders ? ` Diğer platformların ${formatCount(cargo.platformOrders)} siparişi kargo firması ve desiye göre 2026 tarifelerinden: ${formatTl(cargo.platformEstimated ?? 0)}.${desiSourcesNote(cargo.desiSources)}` : ''
  if (cargo.billedOrders === 0 && cargo.unbilledOrders === 0) return platforms.trim()
  const billed = `Trendyol'da ${formatCount(cargo.billedOrders)} siparişin kargo faturası geldi: ${formatTl(cargo.real)}.`
  if (cargo.unbilledOrders === 0) return billed + platforms
  return cargo.estimated === null
    ? `${billed} Faturası henüz gelmeyen ${formatCount(cargo.unbilledOrders)} siparişin kargosu tahmin edilemedi.${platforms}`
    : `${billed} Faturası henüz gelmeyen ${formatCount(cargo.unbilledOrders)} sipariş, mağazanın kargo tarifesiyle tahmin edildi: ${formatTl(cargo.estimated - (cargo.platformEstimated ?? 0))}.${platforms}`
}

// Platforms with orders in the period whose cargo and fees are not counted: Trendyol when its statement was not read
// (Hepsiburada, n11, Pazarama, PTT AVM and Shopify are costed from their tariffs).
const tariffPlatforms = ['HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY']
function unreadFeeCodes(data: ReportSales) {
  return data.platforms.filter(group => group.known && group.orders > 0 && !tariffPlatforms.includes(group.key) && !(group.key === 'TRENDYOL' && trendyolRead(data))).map(group => group.key)
}
function unreadFeePlatforms(data: ReportSales) { return unreadFeeCodes(data).map(platformName) }

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
    ? `${trendyolRead(data) ? 'Trendyol\'da komisyon ve kargo gerçek tutarla, henüz kesinleşmeyenler tahmini; kesintiler cari hesaptan.' : 'Komisyon siparişteki orandan tahmini.'} Diğer platformların kargo ve kesintileri 2026 tarifelerinden tahmini.${unread.length ? ` ${unread.join(', ')} kargo ve hizmet bedeli düşülmedi.` : ''}`
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
        note: `${commissionNote(finance)}${partialCommission ? ' Komisyon oranı bilinmeyen satışlardan komisyon düşülmedi.' : ''}` },
      { key: 'cargo', label: 'Kargo', value: finance.cargo.combined, kind: 'minus', note: cargoNote(finance.cargo) },
      { key: 'fees', label: 'Kesintiler', value: feeTotal, kind: 'minus',
        note: fees.length ? `${fees.map(fee => fee.label).join(', ')} · Trendyol cari hesabından ve diğer platformların tarifelerinden` : 'Bu dönemde kesinti yok' },
      { key: 'payout', label: 'Masraflar Düşülmüş', value: now.netRevenue - charged, kind: 'total',
        note: finance.cargo.estimated === null && finance.cargo.unbilledOrders > 0 ? 'Faturası gelmeyen kargo eksik; ürün maliyeti hariç' : 'Gerçek ve tahmini kalemlerle; ürün maliyeti hariç' }
    )
  } else steps.push(
    { key: 'commission', label: 'Tahmini Komisyon', value: hasCommission ? commission.estimatedCommission : null, kind: 'minus', missing: 'Komisyon bilinmiyor',
      note: hasCommission ? `Siparişteki ya da yüklenen tarifedeki orandan tahmin edildi; satışların ${formatPercent(commission.coverage)} kadarının oranı biliniyor.${partialCommission ? ' Kalanından komisyon düşülmedi.' : ''}` : 'Siparişte komisyon gelmedi, komisyon tarifesi de yüklenmedi.' },
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

// The marketplaces' account statements (one picked at a time), then the platforms' summary, each across the tab.
function Platforms({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><StatementLines data={data} /></div>
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
      <DataTable title="İllere Göre Satış ve İade" empty={term ? 'Aramaya uyan il yok.' : undefined}
        tools={<label className="reports-search"><UiIcon name="search" size={16} /><input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="İl ara" aria-label="İl ara" /></label>} description={`Toplam ${formatNumber(total)} adet; adresinde il okunamayan siparişler "Bilinmiyor" satırında. Satırı açınca ilçeler ve platformlar.`}
        csvName="rapor-sehirler" rows={rows} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold" columns={groupColumns(labelColumn('İl'))}
        expand={row => <div className="reports-split">
          <DataTable title={`${row.label} İlçeleri`} csvName={`rapor-${row.key.toLowerCase()}-ilceler`} rows={row.children} rowKey={child => child.key} unknown={child => !child.known} initialSort="sold" columns={groupColumns(labelColumn('İlçe'))} />
          <DataTable title={`${row.label} Platformları`} csvName={`rapor-${row.key.toLowerCase()}-platformlar`} rows={row.split} rowKey={child => child.key} initialSort="sold" columns={groupColumns(labelColumn('Platform', child => platformName(child.label)))} />
        </div>} />
    </div>
  </div>
}

// A variant's options as the catalogue names them (Beden: M · Renk: Siyah); empty when the line has none.
function variantText(product: ReportProduct) { const { colour, size } = variantColourAndSize(product.options); return [colour, size].filter(Boolean).join(' · ') }

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
    { key: 'colour', header: 'Renk', value: row => variantColourAndSize(row.options).colour, render: row => variantColourAndSize(row.options).colour ?? '—' },
    { key: 'size', header: 'Beden', value: row => variantColourAndSize(row.options).size, render: row => variantColourAndSize(row.options).size ?? '—' },
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

// İadeler: four figures against the previous period, the reasons beside the provinces they came from, then every
// province's returns.
function Returns({ data }: { data: ReportSales }) {
  const byCity = data.cities.filter(city => city.returnedQuantity > 0 || city.returnClaims > 0)
  const now = data.summary
  const then = data.previousSummary
  const items: Kpi[] = [
    { key: 'claims', label: 'İade Talebi', value: now.returnClaims, format: formatCount, delta: change(now.returnClaims, then.returnClaims), invert: true,
      note: previousText(formatCount(then.returnClaims)), tip: 'Müşterilerin açtığı iade talepleri.\nReddedilen ve iptal edilen talepler sayılmaz.' },
    { key: 'units', label: 'İade Adedi', value: now.returnedQuantity, delta: change(now.returnedQuantity, then.returnedQuantity), invert: true,
      note: previousText(formatNumber(then.returnedQuantity)), tip: 'Müşterilerin geri gönderdiği ürün adedi.\nKabul edilen iadeler sayılır.' },
    { key: 'rate', label: 'İade Oranı', value: now.returnRate, format: percent, delta: change(now.returnRate, then.returnRate, 'rate'), invert: true,
      note: previousText(formatPercent(then.returnRate)), tip: 'Satılan ürünlerin yüzde kaçının iade edildiği.\nİade edilen adet ÷ satılan adet.' },
    { key: 'amount', label: 'İade Tutarı', value: now.ordersWithAmount > 0 ? now.returnedAmount : null, format: formatTl, empty: 'Tutar yok',
      delta: now.ordersWithAmount > 0 ? change(now.returnedAmount, then.returnedAmount) : null, invert: true,
      note: previousText(then.ordersWithAmount > 0 ? formatTl(then.returnedAmount) : null), tip: 'İade edilen ürünlerin tutarı.\nSatış tutarından düşülür.' }
  ]
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="İadelerin göstergeleri" items={items} /></div>
    <ReasonsCard title="İade Nedenleri" description="Pazaryerinin bildirdiği nedene göre; neden yoksa Bilinmiyor." reasons={data.returnReasons} csvName="rapor-iade-nedenleri" empty="Bu dönemde iade talebi yok." />
    <Panel title="İade Gelen İller" description="İade adedine göre; yanında iade oranı." className="reports-list">
      <Bars label="İllere göre iade" format={formatNumber} items={topGroups(data.cities, group => group.returnedQuantity, group => formatPercent(group.returnRate))} />
    </Panel>
    <div className="is-wide"><DataTable title="İllere Göre İadeler" csvName="rapor-iade-sehirler" rows={byCity} rowKey={row => row.key} unknown={row => !row.known} initialSort="returned" columns={groupColumns(labelColumn('İl'))} empty="Bu dönemde iade yok." /></div>
  </div>
}

function Cancellations({ data }: { data: ReportSales }) {
  const cancellations = data.cancellations
  const then = data.previousSummary
  const warn = (rate: number | null) => (rate ?? 0) > cancellations.warningRate
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="İptallerin göstergeleri" items={[
      { key: 'orders', label: 'İptal Edilen Sipariş', value: cancellations.cancelledOrders, format: formatCount, delta: change(cancellations.cancelledOrders, then.cancelledOrders), invert: true,
        note: previousText(formatCount(then.cancelledOrders)), tip: 'İptal edilen sipariş sayısı.\nTamamı iptal edilen siparişler sayılır.' },
      { key: 'rate', label: 'İptal Oranı', value: cancellations.cancelRate, format: percent, delta: change(cancellations.cancelRate, then.cancelRate, 'rate'), invert: true,
        note: previousText(formatPercent(then.cancelRate)), tip: `Siparişlerin yüzde kaçının iptal edildiği.
İptal ÷ (sipariş + iptal). Uyarı eşiği ${formatPercent(cancellations.warningRate)}.`, tone: warn(cancellations.cancelRate) ? 'warn' : undefined },
      { key: 'units', label: 'İptal Edilen Adet', value: cancellations.cancelledQuantity, note: 'Kısmi iptaller dahil', tip: 'İptal edilen ürün adedi.\nTamamen iptal edilen siparişler ve kısmi iptaller birlikte.' },
      { key: 'amount', label: 'İptal Edilen Tutar', value: cancellations.cancelledAmount ?? null, format: formatTl, note: 'Satış tutarına girmez', tip: 'İptal edilen ürünlerin tutarı.\nListe fiyatıyla; satış tutarına girmez.' }
    ]} /></div>
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
  const carrier = [...data.cargo].filter(row => row.known).sort((a, b) => b.packages - a.packages)[0]
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="Kargonun göstergeleri" items={[
      { key: 'packages', label: 'Paket', value: packages, format: formatCount, note: `${formatCount(data.cargo.filter(row => row.known).length)} kargo firması`, tip: 'Kargoya verilen paket sayısı.\nDönemin siparişlerinin paketleri.' },
      { key: 'carrier', label: 'Kargo Firması', value: null, text: carrier?.label, note: carrier ? `Paketlerin ${formatPercent(carrier.share)} kadarı` : 'Okunamadı', tip: 'En çok paket taşıyan kargo firması.\nPaket sayısına göre.' },
      { key: 'ship', label: 'Kargo Teslim Alma', value: ship.hours, format: formatHours, note: measured(ship.measured), tip: 'Siparişin kargo firmasına teslim edilmesine kadar geçen ortalama süre.\nKargo firmasının paketi okuttuğu ana kadar; yaklaşık.' },
      { key: 'deliver', label: 'Teslimat', value: deliver.hours, format: formatHours, note: measured(deliver.measured), tip: 'Paketin müşteriye ulaşma süresi.\nKargoya verilişten teslime kadar ortalama; yaklaşık.' }
    ]} /></div>
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
    <div className="is-wide"><KpiStrip label="Müşterilerin göstergeleri" items={[
      { key: 'unique', label: 'Tekil Müşteri', value: customers.uniqueCustomers, format: formatCount, note: `${formatCount(customers.identifiedOrders)} siparişten`, tip: 'Dönemde sipariş veren farklı müşteri sayısı.\nMüşteri numarası, e-posta ya da telefondan tanınır.' },
      { key: 'repeat', label: 'Tekrar Alan Müşteri', value: customers.repeatCustomers, format: formatCount, note: 'Dönemde birden çok sipariş', tip: 'Dönemde birden fazla sipariş veren müşteri sayısı.\nAynı müşterinin ikinci ve sonraki siparişleri.' },
      { key: 'rate', label: 'Tekrar Alım Oranı', value: customers.repeatRate, format: percent, note: 'Tekrar alan / tekil müşteri', tip: 'Müşterilerin yüzde kaçının tekrar aldığı.\nTekrar alan müşteri ÷ tekil müşteri.' },
      { key: 'orders', label: 'Müşteri Başına Sipariş', value: customers.ordersPerCustomer, note: 'Ortalama', tip: 'Bir müşterinin ortalama sipariş sayısı.\nKimliği okunan sipariş ÷ tekil müşteri.' },
      { key: 'unknown', label: 'Kimliği Okunamayan', value: customers.unidentifiedOrders, format: formatCount, note: 'Müşteri sayılarına girmez', tip: 'Müşterisi tanınamayan siparişler.\nMüşteri numarası, e-postası ya da telefonu gelmedi; müşteri sayılarına girmez.' }
    ]} /></div>
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
    <div className="is-wide"><KpiStrip label="Stoğun göstergeleri" items={[
      { key: 'low', label: "Kalan Günü 7'den Az", value: low, format: formatCount, note: `En çok satan ${formatCount(data.stock.bestSellers.length)} ürün içinde`, tip: 'Stoğu bir haftadan az yetecek çok satan ürünler.\nDönemin günlük satış hızına göre.', tone: low > 0 ? 'warn' : undefined },
      { key: 'out', label: 'Satıp Stoğu Biten', value: data.stock.soldOut.length, format: formatCount, note: 'Mevcut stoğu sıfır', tip: 'Satılıp stoğu biten ürünler.\nAna depoda kullanılabilir stoğu kalmadı.', tone: data.stock.soldOut.length > 0 ? 'warn' : undefined },
      { key: 'unknown', label: 'Stoğu Bilinmeyen', value: unknownStock, format: formatCount, note: 'En çok satanlar içinde', tip: 'Stoğu bilinmeyen çok satan ürünler.\nPanelde stok kaydı yok.' }
    ]} /></div>
    <div className="is-wide"><DataTable title="En Çok Satanların Stoğu" description="Ana depodaki kullanılabilir stok (şu an) ve dönemin günlük satışına göre kalan gün; 7 günden az olanlar işaretli." csvName="rapor-stok" rows={data.stock.bestSellers} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={stockColumns(data.stock.periodDays)} /></div>
    <div className="is-wide"><DataTable title="Satıp Stoğu Biten Ürünler" csvName="rapor-stok-biten" rows={data.stock.soldOut} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={stockColumns(data.stock.periodDays)} empty="Dönemde satılıp stoğu sıfır olan ürün yok." /></div>
  </div>
}

// The profit account line by line beside Trendyol's commission and cargo (the strip above holds the coverage and the
// estimated commission).
function CostAndProfit({ data }: { data: ReportSales }) {
  const money = moneyBreakdown(data, profitSteps(data))
  return <div className="reports-grid">
    <Panel title="Kâr Hesabı" description="Genel Bakış'taki hesabın satır satır hâli: satıştan iade ve pazaryeri kesintileri, sonra ürün maliyeti düşülür. Bir satırın üzerine gelince satış tutarına oranı görünür." className="reports-ledger-panel">
      {money ? <><ProfitLedger money={money} /><EstimateGaps money={money} shown /></> : <p className="reports-empty">Bu dönemde tutarı olan sipariş yok.</p>}
    </Panel>
    <div className="reports-stack">
      <Ledger title="Trendyol Komisyon ve Kargo" description="Trendyol cari hesabından: kesinleşen komisyon ve kargo faturası, kalanı tahmini." rows={trendyolLedger(data)}
        empty="Trendyol cari hesabı bu seçimde okunmadı." />
      <Panel title="Platformlara Göre Net Ciro" description="Her platformun satıştan iadeler düştükten sonra kalan cirosu.">
        {/* A platform without amounts is listed in words, not drawn as a ₺0 bar. */}
        <Bars label="Platformlara göre net ciro" format={formatTl} items={data.platforms.filter(group => group.ordersWithAmount > 0).map(group => ({ key: group.key, label: platformName(group.label), value: group.netRevenue }))} />
        <NoAmountNote platforms={data.platforms} />
      </Panel>
    </div>
  </div>
}

type LedgerRow = { label: string; value: string; hint?: string; kind?: 'minus' | 'total' | 'result'; tone?: 'good' | 'bad' }

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
  if (!trendyolRead(data) || !finance?.commission || !finance.cargo) return []
  const { commission, cargo } = finance
  return [
    // One short line under each; the full account of each is on the overview's rows under the pointer.
    { label: 'Trendyol Komisyonu', value: formatTl(commission.combined), hint: commission.realOrders > 0 ? `${formatTl(commission.real)} kesin, ${formatTl(commission.estimatedForUnsettled)} tahmini` : 'Tümü tahmini' },
    { label: 'Hesap Kesimi Olan Sipariş', value: `${formatCount(finance.settledOrders)} / ${formatCount(finance.financeOrders)}`, hint: `Bu siparişlerin tahmini ${formatTl(commission.estimatedForSettled)}` },
    { label: 'Trendyol Kargo', value: formatTl(cargo.combined - (cargo.platformEstimated ?? 0)), hint: cargo.unbilledOrders === 0 ? `${formatCount(cargo.billedOrders)} sipariş faturalı` : `${formatCount(cargo.billedOrders)} sipariş faturalı, ${formatCount(cargo.unbilledOrders)} sipariş tahmini` },
    { label: 'Sipariş Başına Kargo', value: cargo.averagePerOrder === null ? '—' : formatTl(cargo.averagePerOrder), hint: 'Son 30 günde en sık kesilen tutar' }
  ]
}

// A marketplace's account statement line by line, the marketplace picked above the table. Only Trendyol's statement is
// read so far; another marketplace shows that in words instead of an empty table.
function StatementLines({ data }: { data: ReportSales }) {
  const finance = financeCosts(data)
  const read = trendyolRead(data) && Boolean(finance?.commission && finance.cargo)
  const platforms = data.platforms.filter(group => group.known && group.orders > 0).map(group => group.key)
  const [picked, setPicked] = useState(() => read && platforms.includes('TRENDYOL') ? 'TRENDYOL' : platforms[0] ?? 'TRENDYOL')
  const selected = platforms.includes(picked) ? picked : platforms[0] ?? picked
  // The costs A to Z, then what is not a cost (sales, returns, discounts) A to Z under them.
  const rows = selected === 'TRENDYOL' && read && finance ? [...finance.lines].sort((a, b) => Number(!a.isCost) - Number(!b.isCost) || a.label.localeCompare(b.label, 'tr')) : []
  const toolbar = platforms.length > 1 && <div className="reports-segmented" role="group" aria-label="Pazaryeri">
    {platforms.map(code => <button key={code} type="button" aria-pressed={selected === code} onClick={() => setPicked(code)}>{platformName(code)}</button>)}
  </div>
  return <DataTable<CostLine> key={selected} title={`${platformName(selected)} Cari Hesap Kalemleri`} csvName={`rapor-${selected.toLowerCase()}-cari`} rows={rows} rowKey={row => row.category} toolbar={toolbar}
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

// Fatura: the packages by invoice state as one bar beside each platform's counts.
function Invoices({ data }: { data: ReportSales }) {
  const total = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const tone = (key: string) => key === 'INVOICED' ? 'left' : key === 'NOT_INVOICED' ? 'returned' : 'fees'
  const parts: Part[] = data.invoices.buckets.map(bucket => ({ key: bucket.key, label: bucket.label, value: bucket.packages, tone: tone(bucket.key) }))
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="Faturanın göstergeleri" items={data.invoices.buckets.map(bucket => ({ key: bucket.key, label: bucket.label, value: bucket.packages, format: formatCount,
      note: `Paketlerin ${formatPercent(total > 0 ? bucket.packages / total : null)} kadarı`, tip: `Bu fatura durumundaki paketler.
Toplam tutarları ${formatTl(bucket.amount)}.`, tone: bucket.key === 'NOT_INVOICED' && bucket.packages > 0 ? 'warn' as const : undefined }))} /></div>
    <RevealPanel title="Fatura Durumu" description="Panelde kesilmiş fatura, elle seçilen durum ya da pazaryerinin bildirdiği duruma göre; hiçbiri yoksa Bilinmiyor. Ayrıntı Faturalar sayfasında.">
      {shown => total > 0 ? <>
        <StackedBar parts={parts} total={total} shown={shown} label={`Fatura durumu: ${parts.map(part => `${part.label} ${formatCount(part.value)}`).join(', ')}`} />
        <ul className="reports-legend-row">{parts.map(part => <li key={part.key}><i className={`is-${part.tone}`} aria-hidden="true" />{part.label}<b>{formatPercent(part.value / total)}</b></li>)}</ul>
      </> : <p className="reports-empty">Bu dönemde paket yok.</p>}
    </RevealPanel>
    <div><DataTable title="Platformlara Göre Fatura Durumu" csvName="rapor-fatura" rows={data.invoices.platforms} rowKey={row => row.platformCode} initialSort="invoiced"
      footer={data.invoices.platforms.length > 1 && <tr><td>Toplam</td>{(['invoiced', 'notInvoiced', 'unknown'] as const).map(key => <td key={key} className="is-num">{formatCount(data.invoices.platforms.reduce((sum, row) => sum + row[key], 0))}</td>)}</tr>}
      columns={[
      { key: 'platform', header: 'Platform', value: row => platformName(row.platformCode) },
      { key: 'invoiced', header: 'Faturalı', value: row => row.invoiced, render: row => formatCount(row.invoiced), numeric: true },
      { key: 'notInvoiced', header: 'Faturasız', value: row => row.notInvoiced, render: row => formatCount(row.notInvoiced), numeric: true },
      { key: 'unknown', header: unknownLabel, value: row => row.unknown, render: row => formatCount(row.unknown), numeric: true }
    ]} empty="Bu dönemde paket yok." /></div>
  </div>
}
