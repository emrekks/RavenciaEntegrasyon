import { Fragment, useMemo, useState, type CSSProperties, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, UiIcon } from '../../shared/components'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformMultiSelect } from '../../shared/platform-multi-select'
import { ViewTabsWithMore, viewTabLabel, type ViewTabIconName } from '../../shared/view-tabs'
import {
  change, downloadCsv, formatCount, formatDay, formatHours, formatNumber, formatPercent, formatTl, formatTlShort, moneyOrMissing, platformName, reportQuery, sortRows, toCsv, unknownLabel,
  type Change, type CsvColumn, type ReportGroup, type ReportProduct, type ReportReason, type ReportSales, type ReportStockRow, type ReportTimePoint
} from './report-format'
import './reports.css'

type TabKey = 'overview' | 'platforms' | 'cities' | 'products' | 'brands' | 'returns' | 'cancellations' | 'cargo' | 'time' | 'customers' | 'stock' | 'cost' | 'invoices'
const tabs: Array<{ value: TabKey; label: string; icon: ViewTabIconName }> = [
  { value: 'overview', label: 'Genel Bakış', icon: 'all' },
  { value: 'platforms', label: 'Platformlar', icon: 'connected' },
  { value: 'cities', label: 'Şehirler', icon: 'shipping' },
  { value: 'products', label: 'Ürünler', icon: 'product' },
  { value: 'brands', label: 'Marka & Kategori', icon: 'brand' },
  { value: 'returns', label: 'İadeler', icon: 'return' },
  { value: 'cancellations', label: 'İptaller', icon: 'cancelled' },
  { value: 'cargo', label: 'Kargo', icon: 'shipping' },
  { value: 'time', label: 'Zaman', icon: 'waiting' },
  { value: 'customers', label: 'Müşteriler', icon: 'order' },
  { value: 'stock', label: 'Stok', icon: 'processing' },
  { value: 'cost', label: 'Maliyet ve Kâr', icon: 'price' },
  { value: 'invoices', label: 'Fatura', icon: 'invoice' }
]

function dayKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function lastDays(days: number) { const end = new Date(); const start = new Date(); start.setDate(start.getDate() - days + 1); return { from: dayKey(start), to: dayKey(end) } }

// Raporlar: sales, returns, cancellations, cargo, customers, stock and profit for a chosen period and platforms, each
// compared with the previous period of the same length. Every number comes from the server's report; a value the data
// cannot support is shown as "—" or "Tutar yok" with the reason in the data notes, never as a guess.
export function ReportsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const tab = (tabs.some(item => item.value === searchParams.get('tab')) ? searchParams.get('tab') : 'overview') as TabKey
  const [range, setRange] = useState(() => lastDays(30))
  const [platforms, setPlatforms] = useState<string[] | null>(null)
  const [granularity, setGranularity] = useState('')
  const [includeTest, setIncludeTest] = useState(false)
  const query = reportQuery({ from: range.from, to: range.to, platforms, granularity, includeTest })
  const report = useQuery({ queryKey: ['reports-sales', query], queryFn: () => hubApi<ReportSales>(`/reports/sales?${query}`), staleTime: 60_000, placeholderData: previous => previous })
  const data = report.data
  const platformOptions = useMemo(() => [...new Set((data?.channels ?? []).map(channel => channel.platformCode))].map(code => ({ value: code, label: platformName(code) })), [data?.channels])
  const selectTab = (value: string) => setSearchParams(current => { const next = new URLSearchParams(current); next.set('tab', value); return next }, { replace: true })

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
    {data && <>
      <Kpis data={data} />
      <DataNotes notes={data.dataNotes} />
      <ViewTabsWithMore className="reports-tabs" ariaLabel="Rapor bölümleri" more={[]} value={tab} onChange={selectTab}
        items={tabs.map(item => ({ value: item.value, label: viewTabLabel(item.icon, item.label) }))} />
      <div className="reports-body" role="tabpanel" aria-label={tabs.find(item => item.value === tab)?.label}>
        {tab === 'overview' && <Overview data={data} />}
        {tab === 'platforms' && <Platforms data={data} />}
        {tab === 'cities' && <Cities data={data} />}
        {tab === 'products' && <Products data={data} />}
        {tab === 'brands' && <BrandsAndCategories data={data} />}
        {tab === 'returns' && <Returns data={data} />}
        {tab === 'cancellations' && <Cancellations data={data} />}
        {tab === 'cargo' && <Cargo data={data} />}
        {tab === 'time' && <TimeOfDay data={data} />}
        {tab === 'customers' && <Customers data={data} />}
        {tab === 'stock' && <Stock data={data} />}
        {tab === 'cost' && <CostAndProfit data={data} />}
        {tab === 'invoices' && <Invoices data={data} />}
      </div>
    </>}
  </section>
}

function Kpis({ data }: { data: ReportSales }) {
  const now = data.summary
  const before = data.previousSummary
  const noAmount = now.ordersWithAmount === 0
  return <div className="reports-kpis">
    <Kpi label="Net Ciro" value={noAmount ? 'Tutar yok' : formatTl(now.netRevenue)} delta={noAmount ? null : change(now.netRevenue, before.netRevenue)} hint="İadeler düşülmüş" />
    <Kpi label="Satış Tutarı" value={noAmount ? 'Tutar yok' : formatTl(now.grossSales)} delta={noAmount ? null : change(now.grossSales, before.grossSales)} hint="İade öncesi" />
    <Kpi label="Sipariş" value={formatCount(now.orders)} delta={change(now.orders, before.orders)} hint="İptaller hariç" />
    <Kpi label="Satılan Adet" value={formatNumber(now.soldQuantity)} delta={change(now.soldQuantity, before.soldQuantity)} hint={`${formatNumber(now.keptQuantity)} adet elde kaldı`} />
    <Kpi label="Ortalama Sipariş" value={formatTl(now.averageOrderValue)} delta={change(now.averageOrderValue, before.averageOrderValue)} hint="Tutarı olan siparişler" />
    <Kpi label="İade Oranı" value={formatPercent(now.returnRate)} delta={change(now.returnRate, before.returnRate, 'rate')} invert hint={`${formatNumber(now.returnedQuantity)} adet iade`} />
    <Kpi label="İptal Oranı" value={formatPercent(now.cancelRate)} delta={change(now.cancelRate, before.cancelRate, 'rate')} invert hint={`${formatCount(now.cancelledOrders)} sipariş`}
      warn={(now.cancelRate ?? 0) > data.cancellations.warningRate} />
    <Kpi label="Tahmini Komisyon" value={data.commission.coveredRevenue > 0 ? formatTl(data.commission.estimatedCommission) : '—'} hint={`Kapsam ${formatPercent(data.commission.coverage)}`} />
  </div>
}

// A change is good or bad by its direction; for returns and cancellations, up is bad.
function Kpi({ label, value, delta, hint, invert = false, warn = false }: { label: string; value: string; delta?: Change | null; hint?: string; invert?: boolean; warn?: boolean }) {
  const tone = !delta || delta.direction === 'flat' ? 'flat' : (delta.direction === 'up') !== invert ? 'good' : 'bad'
  return <article className={`reports-kpi${warn ? ' is-warn' : ''}`}>
    <small>{label}</small>
    <strong>{value}</strong>
    <span className="reports-kpi-foot">
      {delta ? <em className={`reports-delta is-${tone}`} title="Önceki döneme göre">{delta.direction === 'up' ? '▲' : delta.direction === 'down' ? '▼' : '■'} {delta.text}</em> : <em className="reports-delta is-flat" title="Önceki dönemde veri yok">—</em>}
      {hint && <span>{hint}</span>}
    </span>
  </article>
}

function DataNotes({ notes }: { notes: string[] }) {
  if (!notes.length) return null
  return <details className="reports-notes">
    <summary><UiIcon name="info" size={16} />Veri Notları <span>{notes.length}</span><small>Rakamların nasıl hesaplandığı ve eksik bilgiler</small></summary>
    <ul>{notes.map(note => <li key={note}>{note}</li>)}</ul>
  </details>
}

function Panel({ title, description, actions, children, className }: { title: string; description?: string; actions?: ReactNode; children: ReactNode; className?: string }) {
  return <section className={`reports-panel${className ? ` ${className}` : ''}`}>
    <header><div><h2>{title}</h2>{description && <p>{description}</p>}</div>{actions && <div className="reports-panel-actions">{actions}</div>}</header>
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

// The period's buckets as a line, the previous period's (bucket by bucket) as a dashed line behind it.
function TrendChart({ points, previous, metric, showPrevious, granularity }: { points: ReportTimePoint[]; previous: ReportTimePoint[]; metric: Metric; showPrevious: boolean; granularity: string }) {
  const width = 760
  const height = 240
  const pad = { top: 16, right: 16, bottom: 34, left: 64 }
  const values = points.map(point => point[metric])
  const previousValues = previous.slice(0, points.length).map(point => point[metric])
  const max = Math.max(1, ...values, ...(showPrevious ? previousValues : []))
  const x = (index: number) => pad.left + (points.length <= 1 ? (width - pad.left - pad.right) / 2 : index * (width - pad.left - pad.right) / (points.length - 1))
  const y = (value: number) => pad.top + (height - pad.top - pad.bottom) * (1 - value / max)
  const path = (series: number[]) => series.map((value, index) => `${index === 0 ? 'M' : 'L'}${x(index).toFixed(1)},${y(value).toFixed(1)}`).join(' ')
  const format = (value: number) => metric === 'netRevenue' ? formatTlShort(value) : formatNumber(value)
  const labelEvery = Math.max(1, Math.ceil(points.length / 8))
  const bucketLabel = (start: string) => { const [, month, day] = start.split('-'); return granularity === 'month' ? `${month}.${start.slice(0, 4)}` : `${day}.${month}` }
  const ticks = [0, 0.25, 0.5, 0.75, 1].map(step => max * step)
  if (!points.length) return <p className="reports-empty">Bu dönemde veri yok.</p>
  return <svg className="reports-chart" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${metricLabels[metric]} zaman serisi`}>
    <title>{`${metricLabels[metric]}: ${points.map(point => `${bucketLabel(point.start)} ${format(point[metric])}`).join(', ')}`}</title>
    {ticks.map(tick => <g key={tick} className="reports-chart-grid"><line x1={pad.left} x2={width - pad.right} y1={y(tick)} y2={y(tick)} /><text x={pad.left - 8} y={y(tick) + 4} textAnchor="end">{format(tick)}</text></g>)}
    {showPrevious && previousValues.length > 0 && <path className="reports-chart-previous" d={path(previousValues)} />}
    <path className="reports-chart-area" d={`${path(values)} L${x(values.length - 1).toFixed(1)},${y(0)} L${x(0).toFixed(1)},${y(0)} Z`} />
    <path className="reports-chart-line" d={path(values)} />
    {points.map((point, index) => <g key={point.start}>
      <circle className="reports-chart-dot" cx={x(index)} cy={y(point[metric])} r={points.length > 40 ? 0 : 3}><title>{`${bucketLabel(point.start)}: ${format(point[metric])}${showPrevious && previousValues[index] !== undefined ? ` · önceki ${format(previousValues[index])}` : ''}`}</title></circle>
      {index % labelEvery === 0 && <text className="reports-chart-x" x={x(index)} y={height - 12} textAnchor="middle">{bucketLabel(point.start)}</text>}
    </g>)}
  </svg>
}

function Overview({ data }: { data: ReportSales }) {
  const [metric, setMetric] = useState<Metric>('netRevenue')
  const [showPrevious, setShowPrevious] = useState(true)
  const known = data.cities.filter(city => city.known)
  return <div className="reports-grid">
    <Panel className="is-wide" title="Dönem Grafiği" description={`${data.granularity === 'day' ? 'Günlük' : data.granularity === 'week' ? 'Haftalık' : 'Aylık'} toplamlar; kesikli çizgi önceki dönemin aynı sıradaki dilimi.`}
      actions={<><select aria-label="Grafik ölçüsü" value={metric} onChange={event => setMetric(event.target.value as Metric)}>{(Object.keys(metricLabels) as Metric[]).map(key => <option key={key} value={key}>{metricLabels[key]}</option>)}</select>
        <label className="reports-toggle"><input type="checkbox" checked={showPrevious} onChange={event => setShowPrevious(event.target.checked)} /><span>Önceki Dönem</span></label></>}>
      <TrendChart points={data.timeSeries} previous={data.previousTimeSeries} metric={metric} showPrevious={showPrevious} granularity={data.granularity} />
    </Panel>
    <Panel title="Platform Payı" description="Net ciroya göre. Tutar göndermeyen platformların payı hesaplanamaz; adetleri altta.">
      <Bars label="Platform payı" format={value => formatPercent(value)} items={data.platforms.filter(group => group.ordersWithAmount > 0)
        .map(group => ({ key: group.key, label: platformName(group.label), value: data.summary.netRevenue > 0 ? group.netRevenue / data.summary.netRevenue : 0, note: formatTl(group.netRevenue) }))} />
      {data.platforms.some(group => group.ordersWithAmount === 0) && <p className="reports-note">{data.platforms.filter(group => group.ordersWithAmount === 0)
        .map(group => `${platformName(group.label)}: tutar yok · ${formatNumber(group.soldQuantity)} adet, ${formatCount(group.orders)} sipariş`).join(' — ')}</p>}
    </Panel>
    <Panel title="En Çok Satılan Şehirler" description="Satılan adede göre ilk 10 il.">
      <Bars label="Şehirlere göre satış" format={formatNumber} items={known.slice(0, 10).map(city => ({ key: city.key, label: city.label, value: city.soldQuantity }))} />
    </Panel>
  </div>
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
      <Bars label="Platformlara göre net ciro" format={formatTl} items={data.platforms.map(group => ({ key: group.key, label: platformName(group.label), value: group.netRevenue, note: group.ordersWithAmount === 0 ? 'Tutar yok' : undefined }))} />
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
    <div className="is-wide"><DataTable title="Kargo Firmaları" description="Süreler pazaryerinin bildirdiği durum zamanlarından hesaplanır (yaklaşık); ölçülen paket sayısı yanında. Geç kargo: son kargolama zamanından sonra kargoya verilen." csvName="rapor-kargo"
      rows={data.cargo} rowKey={row => row.label} unknown={row => !row.known} initialSort="packages" columns={[
        { key: 'label', header: 'Kargo Firması', value: row => row.label },
        { key: 'packages', header: 'Paket', value: row => row.packages, render: row => formatCount(row.packages), numeric: true },
        { key: 'share', header: 'Pay', value: row => row.share, render: row => formatPercent(row.share), numeric: true },
        { key: 'ship', header: 'Kargoya Verme', value: row => row.averageHoursToShip, render: row => <Measured value={formatHours(row.averageHoursToShip)} count={row.measuredShip} />, numeric: true, csv: row => row.averageHoursToShip },
        { key: 'deliver', header: 'Teslimat', value: row => row.averageHoursToDeliver, render: row => <Measured value={formatHours(row.averageHoursToDeliver)} count={row.measuredDeliver} />, numeric: true, csv: row => row.averageHoursToDeliver },
        { key: 'late', header: 'Geç Kargo', value: row => row.lateShipments, render: row => <Measured value={formatCount(row.lateShipments)} count={row.measuredLate} />, numeric: true },
        { key: 'lateRate', header: 'Geç Oranı', value: row => row.lateRate, render: row => formatPercent(row.lateRate), numeric: true }
      ]} empty="Bu dönemde paket yok." /></div>
  </div>
}

function Measured({ value, count }: { value: string; count: number }) {
  return <span className="reports-measured">{value}<small>{count > 0 ? `${formatCount(count)} pakette ölçüldü` : 'ölçülemedi'}</small></span>
}

const weekdays = ['Pazartesi', 'Salı', 'Çarşamba', 'Perşembe', 'Cuma', 'Cumartesi', 'Pazar']

function TimeOfDay({ data }: { data: ReportSales }) {
  const max = Math.max(1, ...data.heatmap.flat())
  const byDay = data.heatmap.map(row => row.reduce((sum, value) => sum + value, 0))
  const byHour = Array.from({ length: 24 }, (_, hour) => data.heatmap.reduce((sum, row) => sum + (row[hour] ?? 0), 0))
  const peakHour = byHour.indexOf(Math.max(...byHour))
  const peakDay = byDay.indexOf(Math.max(...byDay))
  const csv = () => downloadCsv('rapor-saat-gun.csv', toCsv(data.heatmap.map((row, day) => ({ day, row })), [
    { header: 'Gün', value: item => weekdays[item.day] }, ...Array.from({ length: 24 }, (_, hour): CsvColumn<{ day: number; row: number[] }> => ({ header: `${String(hour).padStart(2, '0')}:00`, value: item => item.row[hour] ?? 0 }))
  ]))
  return <div className="reports-grid">
    <div className="reports-stat-row is-wide">
      <Stat label="En Yoğun Gün" value={data.summary.orders > 0 ? weekdays[peakDay] : '—'} hint={data.summary.orders > 0 ? `${formatCount(byDay[peakDay])} sipariş` : undefined} />
      <Stat label="En Yoğun Saat" value={data.summary.orders > 0 ? `${String(peakHour).padStart(2, '0')}:00–${String(peakHour + 1).padStart(2, '0')}:00` : '—'} hint={data.summary.orders > 0 ? `${formatCount(byHour[peakHour])} sipariş` : undefined} />
    </div>
    <Panel className="is-wide" title="Gün ve Saate Göre Siparişler" description="İstanbul saatiyle; iptaller hariç. Koyu hücre daha çok sipariş." actions={<button type="button" className="secondary reports-csv" onClick={csv}><UiIcon name="download" size={16} />CSV</button>}>
      <div className="reports-heatmap-wrap"><table className="reports-heatmap">
        <thead><tr><th scope="col"><span className="sr-only">Gün</span></th>{Array.from({ length: 24 }, (_, hour) => <th key={hour} scope="col">{hour % 3 === 0 ? String(hour).padStart(2, '0') : ''}</th>)}</tr></thead>
        <tbody>{data.heatmap.map((row, day) => <tr key={weekdays[day]}><th scope="row">{weekdays[day].slice(0, 3)}</th>{row.map((value, hour) =>
          <td key={hour} style={{ '--heat': value / max } as CSSProperties} title={`${weekdays[day]} ${String(hour).padStart(2, '0')}:00 · ${formatCount(value)} sipariş`}><span className="sr-only">{value}</span></td>)}</tr>)}</tbody>
      </table></div>
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
  </div>
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
