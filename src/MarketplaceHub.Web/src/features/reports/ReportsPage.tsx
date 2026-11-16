import type { ApiAnswer } from '../../shared/api/api-answer'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, type UiIconName } from '../../shared/components'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { istanbulDateKey } from '../../shared/format-date'
import { previousHasData } from '../../shared/components/ChangePill'
import { useMetricSource } from '../marketplace/use-metric-source'
import { PlatformFilter } from '../../shared/PlatformFilter'
import '../../shared/PanelSelect' // keeps its stylesheet's place in this page's CSS order
import { ViewTabsWithMore } from '../../shared/view-tabs'
import { TabSlidePanel } from '../../shared/tab-slide'
import { bySalesChannel, isSalesChannel, formatPercent, platformName, readReportSales, reportQuery, periodPresets, type ReportSales } from './report-format'
import './AdReport' // keeps its stylesheet's place in this page's CSS order
import { OrderProfitability } from './OrderProfitability'
import './SalesMap' // keeps its stylesheet's place in this page's CSS order
import '../../shared/ui/rr-components' // keeps its stylesheet's place in this page's CSS order
import '../marketplace/shared' // keeps its stylesheet's place in this page's CSS order
import './reports.css'
import { queryKeys } from '../../shared/api/query-keys'
import { MetricSourceContext, NoPreviousDataContext, type OpsSub, type DetailKey, tabLabel, moneyBreakdown, profitSteps } from './reports-page/report-parts'
import { Overview } from './reports-page/OverviewTab'
import { Geography, ProductsView, ProfitView, OperationsView, CustomersView } from './reports-page/ReportViews'
import { StockReport } from './reports-page/StockReport'
import { PayoutsReport } from './reports-page/PayoutsReport'
export { type Kpi, KpiStrip } from './reports-page/report-parts'
export { returnedAtList } from './reports-page/SalesTables'

// The report as tabs: an overview, then one tab per area with its own breakdown (Ürünler and Operasyon have sub-tabs),
// A card's "Tümünü Gör" moves to its area's tab (?view=…&sub=…); an older ?detail=… link
// lands on the same tab.
type ViewKey = 'overview' | 'geo' | 'products' | 'stock' | 'profit' | 'payouts' | 'orders' | 'ops' | 'customers'
const viewKeys: ViewKey[] = ['overview', 'profit', 'payouts', 'orders', 'products', 'stock', 'ops', 'customers', 'geo']

const detailHome: Record<DetailKey, { view: ViewKey; sub?: string }> = {
  platforms: { view: 'profit' }, cost: { view: 'profit' }, cities: { view: 'geo' },
  products: { view: 'products', sub: 'products' }, brands: { view: 'products', sub: 'brands' },
  returns: { view: 'ops', sub: 'returns' }, cancellations: { view: 'ops', sub: 'cancellations' }, cargo: { view: 'ops', sub: 'cargo' }, invoices: { view: 'ops', sub: 'invoices' }, stock: { view: 'stock' },
  time: { view: 'customers' }, customers: { view: 'customers' }
}
const isDetailKey = (value: string | null): value is DetailKey => value !== null && value in detailHome
const isViewKey = (value: string | null): value is ViewKey => value !== null && (viewKeys as string[]).includes(value)

const lastThirtyDays = () => periodPresets().find(preset => preset.key === '30')?.range ?? { from: istanbulDateKey(), to: istanbulDateKey() }

// Raporlar: sales, returns, cancellations, cargo, customers, stock and profit for a chosen period and platforms, each
// compared with the previous period of the same length. Every number comes from the server's report; a value the data
// cannot support is shown as "—" with the reason in the data notes, never as a guess.
export function ReportsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const detailParam = searchParams.get('detail')
  const legacy = isDetailKey(detailParam) ? detailHome[detailParam] : null
  const viewParam = searchParams.get('view')
  // The promotion analysis moved to its own page; an old ?view=tariffs link goes there.
  const navigate = useNavigate()
  useEffect(() => { if (viewParam === 'tariffs') void navigate('/promotions', { replace: true }) }, [viewParam, navigate])
  // Operasyon's Stok sub-tab moved into the Stok tab (owner, 7 Nov 2026): an old ?view=ops&sub=stock link opens it.
  const opsStock = viewParam === 'ops' && searchParams.get('sub') === 'stock'
  const view: ViewKey = legacy?.view ?? (opsStock ? 'stock' : isViewKey(viewParam) ? viewParam : 'overview')
  const sub = legacy ? legacy.sub ?? null : opsStock ? null : searchParams.get('sub')
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
  const [range, setRange] = useState(lastThirtyDays)
  // A custom range being picked: the picker shows the first day picked until the second one is, and the report keeps
  // the last whole range meanwhile.
  const [draft, setDraft] = useState<{ from: string; to: string } | null>(null)
  // The preset clicked last: two presets can cover the same days (on 30 October, Bu Ay and Son 30 Gün), and the one
  // clicked is the one marked.
  const [pickedPreset, setPickedPreset] = useState('30')
  const presets = periodPresets()
  // The preset the range matches, if any; another range is a custom one, shown on the picker.
  const matching = presets.filter(preset => preset.range.from === range.from && preset.range.to === range.to)
  const activePreset = (matching.find(preset => preset.key === pickedPreset) ?? matching[0])?.key ?? null
  const [platforms, setPlatforms] = useState<string[] | null>(null)
  const metricSource = useMetricSource({ platforms })
  const query = reportQuery({ from: range.from, to: range.to, platforms, granularity: '', includeTest: false })
  const report = useQuery({ queryKey: queryKeys.reportsSales(query), queryFn: async ({ signal }) => readReportSales(await hubApi<ApiAnswer<'getReportsSales'>>(`/reports/sales?${query}`, { signal })), staleTime: 60_000, placeholderData: previous => previous })
  const data = report.data
  const platformOptions = useMemo(() => [...new Set((data?.channels ?? []).map(channel => channel.platformCode))].filter(isSalesChannel).sort(bySalesChannel).map(code => ({ value: code, label: platformName(code) })), [data?.channels])
  // A tab change slides the body in from the tab's side and starts the page from its top (shared/tab-slide.tsx).
  const go = (nextView: ViewKey, nextSub: string | null = null) => {
    setSearchParams(current => {
      const next = new URLSearchParams(current)
      next.delete('detail')
      if (nextView === 'overview') next.delete('view'); else next.set('view', nextView)
      if (nextSub) next.set('sub', nextSub); else next.delete('sub')
      return next
    }, { replace: true })
  }
  // "Tümünü Gör": the area's tab, opened like any tab: from the page's top.
  const openDetail = (key: DetailKey) => { go(detailHome[key].view, detailHome[key].sub ?? null) }

  return <MetricSourceContext.Provider value={metricSource}><NoPreviousDataContext.Provider value={data ? !previousHasData(data.previousFrom, data.dataStartsOn) : false}><section ref={page} className="content reports-page">
    <div className="page-heading reports-heading">
      <div><p className="eyebrow">Analiz</p><h1>Raporlar</h1><p className="reports-lead">Satış, iade, iptal, kargo ve kârlılık; platform, şehir ve ürün kırılımıyla, önceki dönemle karşılaştırmalı.</p></div>    </div>
    <div className="reports-filters" role="search" aria-label="Rapor filtreleri">
      <div className="reports-field reports-period-field" role="group" aria-label="Dönem"><div className="reports-period-row">
        <div className="reports-segmented" role="group" aria-label="Hazır dönemler">{presets.map(preset => <button key={preset.key} type="button" aria-pressed={preset.key === activePreset} onClick={() => { setDraft(null); setPickedPreset(preset.key); setRange(preset.range) }}><span>{preset.label}</span></button>)}</div>
        <div className={`reports-period-custom${activePreset ? '' : ' is-active'}`}><DateRangePicker from={draft?.from ?? range.from} to={draft ? draft.to : range.to} label="Özel Dönem"
          onChange={next => { if (next.from && next.to) { setDraft(null); setRange({ from: next.from, to: next.to }) } else setDraft(next) }} /></div>
        {/* The custom period and the platforms at the row's right end; the charts group by day, week or month on their own. */}
        <div className="reports-period-platforms"><PlatformFilter label="Platform" hideLabel options={platformOptions} selected={platforms ?? []} onChange={next => setPlatforms(next.length ? next : null)} /></div>
      </div></div>
      {/* A thin line along the card's top edge runs while a filter change loads: inside the card, so it moves nothing and
          sticks out nowhere. */}
      <span className={`reports-refreshing${report.isFetching && data ? ' is-active' : ''}`} role="status"><span className="sr-only">{report.isFetching && data ? 'Güncelleniyor' : ''}</span></span>
    </div>
    {report.isLoading && <Busy text="Rapor hazırlanıyor…" />}
    {report.isError && !data && <ErrorBox error={report.error} onRetry={() => void report.refetch()} />}
    {data && (() => {
      const tabs = reportTabs(data)
      return <>
        <ViewTabsWithMore className="reports-tabs" ariaLabel="Rapor görünümü" more={[]} value={view} onChange={value => go(value as ViewKey)}
          items={tabs.map(tab => ({ value: tab.value, label: tabLabel(tab.icon, tab.label), count: tab.count, countTone: tab.warn ? 'warn' : undefined }))} />
        <TabSlidePanel active={view} order={tabs.map(tab => tab.value)} className="reports-body" role="tabpanel" aria-label={tabs.find(tab => tab.value === view)?.label}>
          {view === 'overview' ? <Overview data={data} onMore={openDetail} />
            : view === 'geo' ? <Geography data={data} />
            : view === 'products' ? <ProductsView data={data} sub={sub === 'brands' ? 'brands' : 'products'} onSub={next => go('products', next)} />
            : view === 'profit' ? <ProfitView data={data} />
            : view === 'payouts' ? <PayoutsReport platforms={platforms} />
            : view === 'orders' ? <OrderProfitability from={range.from} to={range.to} platforms={platforms} />
            : view === 'stock' ? <StockReport from={range.from} to={range.to} platforms={platforms} sales={data} />
            : view === 'ops' ? <OperationsView data={data} sub={isOpsSub(sub) ? sub : 'returns'} onSub={next => go('ops', next)} />
            : <CustomersView data={data} />}
        </TabSlidePanel>
      </>
    })()}
  </section></NoPreviousDataContext.Provider></MetricSourceContext.Provider>
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
    { value: 'profit', label: 'Kârlılık', icon: 'trend', count: margin === null ? '%0' : formatPercent(margin), warn: margin !== null && margin < 0 },
    { value: 'payouts', label: 'Hakediş', icon: 'wallet' },
    { value: 'orders', label: 'Sipariş Kârlılığı', icon: 'orders' },
    { value: 'products', label: 'Ürünler', icon: 'products' },
    { value: 'stock', label: 'Stok', icon: 'stock' },
    { value: 'ops', label: 'Operasyon', icon: 'truck', count: `İptal ${now.cancelRate === null || now.cancelRate === undefined ? '%0' : formatPercent(now.cancelRate)}`, warn: (now.cancelRate ?? 0) > data.cancellations.warningRate },
    { value: 'customers', label: 'Müşteriler', icon: 'users' },
    { value: 'geo', label: 'Coğrafya', icon: 'globe' }
  ]
}

const isOpsSub = (value: string | null): value is OpsSub => value === 'returns' || value === 'cancellations' || value === 'cargo' || value === 'invoices'
