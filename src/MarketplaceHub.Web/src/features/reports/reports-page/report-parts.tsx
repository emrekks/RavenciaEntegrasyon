import { Fragment, createContext, useContext, useEffect, useId, useState, type ReactNode } from 'react'
import { CardBackgroundIcon, EmptyState, UiIcon, type CardIconName, type UiIconName } from '../../../shared/components'
import { ChangePill } from '../../../shared/components/ChangePill'
import { ViewTabsWithMore } from '../../../shared/view-tabs'
import { noValue, desiSourcesNote, downloadCsv, formatCount, formatNumber, formatPercent, formatTl, formatTlWhole, platformName, sortRows, toCsv, profitCost, type Change, type CsvColumn, type ReportCosts, type ReportGroup, type ReportSales } from '../report-format'
import { CountUp, useReveal, type Part } from '../report-visuals'

// Whether an active connection (of the chosen platforms) feeds the report's figures: without one every main value is a dash
// (shared rule, shared/metric-value.ts). Undefined while the connections are not read.
export const MetricSourceContext = createContext<boolean | undefined>(undefined)
// Whether the previous period starts before the chosen stores' first order (no data source for part of it): every change
// pill then reads a neutral "—" ("Önceki dönem için veri yok") instead of comparing with a half-empty period.
export const NoPreviousDataContext = createContext(false)

export type OpsSub = 'returns' | 'cancellations' | 'cargo' | 'invoices'
export type DetailKey = 'platforms' | 'cities' | 'products' | 'brands' | 'returns' | 'cancellations' | 'cargo' | 'time' | 'customers' | 'stock' | 'cost' | 'invoices'

// A tab's label: its icon and the text, which keeps the width of its selected weight (as viewTabLabel does).
export const tabLabel = (icon: UiIconName, text: string) => <><UiIcon name={icon} size={15} className="rv-view-tab-icon" /><span className="rv-tab-text" data-text={text}>{text}</span></>

// A tab's own sub-tabs, in the panel's tab style.
export function SubTabs<T extends string>({ label, value, items, onChange }: { label: string; value: T; items: Array<{ value: T; label: string; icon: UiIconName; count?: string; warn?: boolean }>; onChange: (value: T) => void }) {
  return <ViewTabsWithMore className="reports-subtabs" ariaLabel={label} more={[]} value={value} onChange={next => onChange(next as T)}
    items={items.map(item => ({ value: item.value, label: tabLabel(item.icon, item.label), count: item.count, countTone: item.warn ? 'warn' : undefined }))} />
}

// A change is good or bad by its direction; for returns and cancellations, up is bad.
// previous: the previous period's value, shown under the pointer instead of the generic "Önceki döneme göre".
export function Delta({ delta, invert = false, previous }: { delta: Change | null | undefined; invert?: boolean; previous?: string }) {
  const noData = useContext(NoPreviousDataContext)
  // A change pill with nothing to compare reads a flat "%0" (a tab badge or pill is a counter, not a card value).
  return <ChangePill className="reports-delta" change={delta} invert={invert} noData={noData} title={previous ? `Önceki dönem: ${previous}` : 'Önceki döneme göre değişim'} />
}

// A panel's explanation shows under its title while the pointer or the focus is on the title, as the overview's rows
// do; nothing is written under the title.
export function Panel({ title, description, actions, children, className }: { title: string; description?: string; actions?: ReactNode; children: ReactNode; className?: string }) {
  const tip = useId()
  return <section className={`reports-panel${className ? ` ${className}` : ''}`}>
    <header>
      {description ? <div className="reports-tip-host reports-panel-title">
        <h2><button type="button" className="reports-tip-trigger" aria-describedby={tip}>{title}<TipMark /></button></h2>
        <span role="tooltip" id={tip} className="reports-hover-tip"><TipText title={title} text={description} /></span>
      </div> : <div className="reports-panel-title"><h2>{title}</h2></div>}
      {actions && <div className="reports-panel-actions">{actions}</div>}
    </header>
    {children}
  </section>
}

// A panel whose chart plays once, when the panel first comes into view.
export function RevealPanel({ title, description, actions, className, children }: { title: string; description?: string; actions?: ReactNode; className?: string; children: (shown: boolean) => ReactNode }) {
  const [ref, shown] = useReveal<HTMLDivElement>()
  return <Panel title={title} description={description} actions={actions} className={className}><div ref={ref} className="reports-reveal-body">{children(shown)}</div></Panel>
}

export type Column<T> = { key: string; header: string; value: (row: T) => number | string | null; render?: (row: T) => ReactNode; numeric?: boolean; csv?: (row: T) => string | number | null }

// A table with sortable columns, the unknown bucket last and muted, an optional row expansion and its CSV export.
export function DataTable<T>({ title, description, rows, columns, rowKey, unknown, expand, csvName, empty = 'Bu dönemde kayıt yok.', initialSort, footer, toolbar, tools }: {
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
    {rows.length === 0 ? <EmptyState icon="chart" title="Veri Yok">{empty}</EmptyState> : <div className="reports-table-wrap"><table className="reports-table">
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
// A value that is not known (null) gets no bar and "—", never a 0% that reads as measured.
export function Bars({ items, format, label }: { items: Array<{ key: string; label: string; value: number | null; muted?: boolean; note?: string }>; format: (value: number) => string; label: string }) {
  const [ref, shown] = useReveal<HTMLUListElement>()
  const max = Math.max(0, ...items.map(item => item.value ?? 0))
  if (!items.length || max <= 0) return <EmptyState icon="chart" title="Veri Yok">Gösterilecek değer yok.</EmptyState>
  return <ul ref={ref} className="reports-bars" aria-label={label}>{items.map((item, index) => <li key={item.key} className={item.muted ? 'is-unknown' : undefined}>
    <span className="reports-bar-label" title={item.label}>{item.label}</span>
    <span className="reports-bar-track" aria-hidden="true"><span style={{ width: shown && item.value !== null ? `${Math.max(2, item.value / max * 100)}%` : '0%', transitionDelay: `${index * 60}ms` }} /></span>
    <span className="reports-bar-value">{item.value === null ? '—' : format(item.value)}{item.note && <small>{item.note}</small>}</span>
  </li>)}</ul>
}

export type OpenDetail = (key: DetailKey) => void

// An explanation shown under the pointer, in the owner's style (9 Nov 2026): the figure's name in bold, one short
// sentence saying what it is (a second only where needed), then, where the figure compares, "Önceki dönem: …" on a
// line of its own. Each line of the text (split at a line break) is a line of the explanation.
// The round "?" beside a name: its explanation opens while the pointer is on it (or the name has the keyboard focus).
// Report figures explain an amount or a count: an "i" mark (a "?" is for settings that need a decision).
export const TipMark = () => <UiIcon name="infoCircle" size={14} className="reports-qmark" />

export function TipText({ title, text }: { title: string; text: string }) {
  return <><b>{title}</b>{text.split('\n').filter(Boolean).map(line => <span key={line}>{line}</span>)}</>
}

// The previous period's figure as the explanation's last line.
// A previous figure of nothing ("0", "₺0,00", "%0,0") is no data either.
const isNothing = (value: string) => /^(₺|%)?0([,.]0+)?$/u.test(value.trim())
// zero: the figure's own zero ("₺0", "%0"); a sub-line with no data reads zero, only a card's main value is a dash.
export const previousLine = (value: string | null, zero = '0') => value === null || value === '—' || isNothing(value) ? `Önceki dönem: ${zero}` : `Önceki dönem: ${value}`

// A board card: its icon and title, its actions, and its content, which learns when the card first came into view so
// its chart can play then.
// tip: what the card shows, opened from an "i" beside its title.
export function Card({ title, icon, actions, className, tip, children }: { title: string; icon?: UiIconName; actions?: ReactNode; className?: string; tip?: string; children: (shown: boolean) => ReactNode }) {
  const [ref, shown] = useReveal<HTMLElement>()
  const tipId = useId()
  return <section ref={ref} className={`reports-card${className ? ` ${className}` : ''}${shown ? ' is-shown' : ''}`}>
    <header>
      <span className={`reports-card-title${tip ? ' reports-tip-host' : ''}`}>{icon && <span className="reports-card-icon" aria-hidden="true"><UiIcon name={icon} size={16} /></span>}
        {tip ? <><h2><button type="button" className="reports-tip-trigger" aria-describedby={tipId}>{title}<TipMark /></button></h2>
          <span role="tooltip" id={tipId} className="reports-hover-tip"><TipText title={title} text={tip} /></span></> : <h2>{title}</h2>}</span>
      {actions && <div className="reports-card-actions">{actions}</div>}
    </header>
    {children(shown)}
  </section>
}

type MoneyPart = Part & { note?: string }
// One account from the sales to the profit, every figure taken from the one before it: the sales, the deductions
// (returns and the marketplaces' commission, cargo and fees), what is left after them, the product cost entered and the
// profit (null until a cost is entered) with its margin over the sales.
export type Money = {
  // The net VAT of the Trendyol orders, off the profit after the deductions (null when the statement is not read).
  tax: number | null
  // The deductions of the period tied to no order of it (Siparişe Bağlı Olmayan Kesintiler: advertising, penalties, the
  // fee invoices not split over their orders yet), from Trendyol's statement; taken off the profit after the product
  // cost (owner, 5 Nov 2026: "Net Kâr yazan yerde tüm masraflar çıkmış olmalıydı"). 0 when there are none.
  unlinked: number
  total: number; deductions: MoneyPart[]; deducted: number; left: number; cost: number | null; costCoverage: number | null
  // The units the customers kept whose product cost is not entered. The deductions are taken off every sale, so a profit
  // with part of the cost missing would be too high by that cost: the profit (and every rate from it) is shown only when
  // at least 95 % of the kept units carry a cost, else "Maliyet Eksik" (profitCost; owner, 13 Nov 2026). Between 95 and
  // 100 % `cost` holds the known cost plus `estimated`, the missing units' cost priced by their share of the sales, and
  // every profit figure is marked "Tahmini" with `estimate`, the sentence that says so ('' when nothing is estimated).
  uncosted: number; estimated: number; estimate: string
  profit: number | null; margin: number | null; description: string
  // What the account could not take off: the commission when none is known, and the platforms whose cargo and fees are
  // not read (with Trendyol's statement) or, without it, the cost lines read nowhere.
  gaps: { commission: boolean; platforms: string[]; lines: string[] }
}

const deductionTones: Record<string, string> = { returned: 'returned', commission: 'commission', cargo: 'cargo', fees: 'fees' }

export function moneyBreakdown(data: ReportSales, flow: ProfitSteps): Money | null {
  if (!flow.steps) return null
  const now = data.summary
  const finance = financeCosts(data)
  // Each category is counted once, at its one figure (costFigure): the commission, cargo and per-sale fees charged in
  // the period tied to none of its orders go into their own deduction (Masraf Kalemleri lists them in their own lines,
  // 7 Nov 2026: the bridge's Stopaj once left out the 448 TL of Masraf Kalemleri's), and the kinds no order carries
  // (advertising, penalties) into Reklam ve Diğer Kesintiler, with those of the period's orders.
  const folded = (step: FlowStep) => !finance ? step.value ?? 0
    : step.key === 'commission' ? (step.value ?? 0) + apartAmount(data, 'COMMISSION')
      : step.key === 'cargo' ? (step.value ?? 0) + apartAmount(data, 'CARGO')
        : step.key === 'fees' ? figureOf(data, chargeFeeCategories) : step.value ?? 0
  // Every deduction that is not zero: one below zero (a fee credited back) adds to what is left, as on the bridge.
  const deductions: MoneyPart[] = flow.steps.filter(step => step.kind === 'minus' && step.value !== null)
    .map(step => ({ key: step.key, label: step.label, value: folded(step), tone: deductionTones[step.key] ?? 'fees', note: step.note }))
    .filter(part => part.value !== 0)
  const deducted = deductions.reduce((sum, part) => sum + part.value, 0)
  const left = now.grossSales - deducted
  const productCost = profitCost(data.cost)
  const cost = data.cost.costedQuantity > 0 ? productCost.usable ? productCost.total : productCost.known : null
  const tax = finance?.netVat?.net ?? null
  const unlinked = finance ? figureOf(data, orderlessCategories) : unlinkedDeductions(trendyolRead(data) ? data.costs : null).total
  const uncosted = cost === null ? 0 : productCost.missing
  const estimated = productCost.usable ? productCost.estimated : 0
  const profit = cost === null || !productCost.usable ? null : left - (tax ?? 0) - cost - unlinked
  const estimate = profit !== null && uncosted > 0
    ? `${formatNumber(uncosted)} adedin maliyeti eksik (${formatPercent(data.cost.keptQuantity > 0 ? uncosted / data.cost.keptQuantity : null)}); kâr, eksik adetlerin maliyeti satış payına göre tahmin edilerek hesaplandı (${formatTl(estimated)}).`
    : ''
  const unread = finance ? unreadFeeCodes(data) : []
  const commissionKnown = flow.steps.some(step => step.key === 'commission' && step.value !== null)
  const coverage = data.cost.coverage
  return {
    gaps: { commission: !commissionKnown, platforms: unread, lines: finance ? [] : unreadCostLines },
    total: now.grossSales, deductions, deducted, left, tax, unlinked, cost, costCoverage: cost === null ? null : coverage, uncosted, estimated, estimate, profit,
    // The margin is over Net Ciro (sales less returns, owner 14 Nov 2026): the profit is earned on the kept sales only.
    margin: profit !== null && now.netRevenue > 0 ? profit / now.netRevenue : null, description: flow.description
  }
}

// Why the profit is not shown: no cost entered at all, or under 95 % of the sold units with one (Money.uncosted).
// An unknown profit is always "—" (noValue); the reason is the card's sub-line (null when the period has no amount at all).
export const profitReason = (money: Money | null, noAmount: boolean) => noAmount ? null : (money?.uncosted ?? 0) > 0 ? 'Maliyet Eksik' : 'Maliyet girilmemiş'
export const missingCostTip = (money: Money | null) => (money?.uncosted ?? 0) > 0
  ? `Satılan ${formatNumber(money?.uncosted ?? 0)} adedin ürün maliyeti girilmemiş (${formatPercent(money?.costCoverage === null || money?.costCoverage === undefined ? null : 1 - money.costCoverage)}). Kesintiler bütün satışlardan düşüldüğü için eksik maliyetle hesaplanan kâr olduğundan yüksek çıkardı; satılan adetlerin en az %95'inin maliyeti girilince hesaplanır. Maliyeti ürün kartından girin.`
  : noCostTip

// The profit's title: "Tahmini" when part of its cost is estimated (Money.estimate), as a loss always is.
export const profitLabel = (money: Money | null) => (money?.profit ?? 0) < 0 ? 'Tahmini Zarar' : money?.estimate ? 'Tahmini Net Kâr' : 'Net Kâr'
// The estimate's sentence after a profit figure's tip, on a new line ('' when nothing is estimated).
export const estimateLine = (money: Money | null) => money?.estimate ? `\n${money.estimate}` : ''

export const signedTl = (value: number) => value < 0 ? `− ${formatTl(-value)}` : formatTl(value)

// A figure that counts up when first seen; a negative one is written with its minus at once (a count never shows a
// value below zero).
function Figure({ value, format, shown }: { value: number; format: (value: number) => string; shown: boolean }) {
  return value < 0 ? <>− {format(-value)}</> : <CountUp value={value} format={format} active={shown} />
}

// A figure of a strip: its value (or a word, as a province's name), its change against the previous period with the
// previous value under it, and how it is worked out, shown under the pointer.
export type Kpi = {
  key: string; label: string; value: number | null; format?: (value: number) => string; text?: string
  delta?: Change | null; invert?: boolean; note?: string; tip?: string; tone?: 'good' | 'bad' | 'warn'; icon?: CardIconName
  // Rows the explanation lists under it (the figure per platform); the card then opens it on hover, not only its label.
  split?: Array<{ key: string; label: string; value: string }>
}

// The icon drawn into a figure's background, from what it measures (its label), unless the figure names one.
const kpiIcons: Array<[RegExp, CardIconName]> = [
  [/fatura/u, 'invoice'], [/müşteri/u, 'users'], [/iptal/u, 'packageX'], [/iade/u, 'returns'], [/kargo|teslim|paket/u, 'truck'],
  [/stoksuz|biten|ölü/u, 'shirtX'], [/kırığı|eksik/u, 'alertTriangle'], [/stok|envanter|sermaye|varyant/u, 'stock'],
  [/oran|marj|roas|kapsam|komisyon|indirim|maliyete göre/u, 'percent'], [/kâr|zarar/u, 'wallet'], [/marka/u, 'tag'], [/adet/u, 'shirt'], [/sipariş/u, 'orders'],
  [/ciro|tutar|satış|maliyet|masraf|kdv|stopaj|kesinti|ceza|bedel|ödeme|reklam|harcama|değer/u, 'lira']
]
const kpiIcon = (item: Kpi): CardIconName => item.icon ?? kpiIcons.find(([pattern]) => pattern.test(item.label.toLocaleLowerCase('tr')))?.[1] ?? 'grid'

// A strip of figures divided by hairlines (a figure without a note shows its label and value only); the figures count
// up once when the strip first comes into view. In a strip that compares, a figure without a previous value keeps the
// change's line empty, so every note starts on one line.
// The explanation of the figure under the pointer (or the focus) opens as a card under the strip, level with its cell
// and kept inside the strip's width; each cell keeps its explanation as text for screen readers too.
// marks: each label carries the round question mark and the explanation opens from the label only (Hakediş).
// aligned: the cells share their rows (subgrid), so a label that wraps to two lines keeps every figure level.
// long: a strip whose labels need more room per box than the others' (Hakediş, Kârlılık, Stok, Sipariş Kârlılığı): it
// goes to rows of three while its box would be too narrow for a label on one line, then two and one, as Genel Bakış's.
// stack: one box a row on a phone only (a label too long for half a phone's width, Müşteriler).
export function KpiStrip({ label, items, marks = true, aligned = false, long = false, stack = false }: { label: string; items: Kpi[]; marks?: boolean; aligned?: boolean; long?: boolean; stack?: boolean }) {
  const hasSource = useContext(MetricSourceContext)
  const [ref, shown] = useReveal<HTMLDivElement>()
  const tip = useId()
  const [open, setOpen] = useState<{ key: string; left: number; top: number; width: number } | null>(null)
  const compares = items.some(item => item.delta !== undefined)
  const [peek, setPeek] = useState<string | null>(null)
  const previousNote = (item: Kpi) => item.delta !== undefined && item.note?.startsWith('Önceki dönem') === true
  const show = (key: string, cell: HTMLElement) => {
    const strip = cell.parentElement
    if (!strip) return
    const width = Math.min(300, strip.clientWidth)
    setOpen({ key, width, top: strip.offsetTop + cell.offsetTop + cell.offsetHeight + 8, left: Math.max(0, Math.min(cell.offsetLeft, strip.clientWidth - width)) })
  }
  const hide = () => setOpen(null)
  // On a touch screen a tip opens only from the (i) mark itself (a tap on the card, the label or a figure never opens it); a tap on the mark
  // again, a tap anywhere else, a scroll or Esc closes it, and only one is ever open.
  const touch = () => typeof window !== 'undefined' && window.matchMedia?.('(hover: none)').matches === true
  useEffect(() => {
    if (!open) return
    const away = (event: Event) => { if (!(event.target instanceof Element) || !event.target.closest('.reports-qmark')) setOpen(null) }
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(null) }
    document.addEventListener('pointerdown', away, true); window.addEventListener('scroll', hide, true); document.addEventListener('keydown', key)
    return () => { document.removeEventListener('pointerdown', away, true); window.removeEventListener('scroll', hide, true); document.removeEventListener('keydown', key) }
  }, [open])
  const current = open ? items.find(item => item.key === open.key) : undefined
  return <div className="reports-kpis-wrap">
    <div ref={ref} className={`reports-kpis is-${items.length}${aligned ? ' is-aligned' : ''}${long ? ' is-long' : ''}${stack ? ' is-stack' : ''}`} role="group" aria-label={label}>{items.map(item => {
      const id = `${tip}-${item.key}`
      // Whole counts stay whole while they count up; money shows whole liras (the kuruş rounded) in a strip.
      const format = item.format === formatTl ? formatTlWhole : item.format ?? (Number.isInteger(item.value) ? (value: number) => formatNumber(Math.round(value)) : formatNumber)
      return <div key={item.key} className={`${item.text !== undefined ? 'is-text' : ''}${item.tone ? ` is-${item.tone}` : ''}${open?.key === item.key ? ' is-open' : ''}`.trim() || undefined}
        onMouseEnter={marks && !item.split?.length ? undefined : event => { if (!touch()) show(item.key, event.currentTarget) }} onMouseLeave={hide}>
        <CardBackgroundIcon name={kpiIcon(item)} tone={item.tone === 'good' ? 'ok' : item.tone} />
        <button type="button" className="reports-tip-trigger reports-kpi-label" aria-describedby={item.tip ? id : undefined}
          onMouseEnter={marks && item.tip ? event => { if (!touch() && event.currentTarget.parentElement) show(item.key, event.currentTarget.parentElement) } : undefined}
          onClick={event => { if (!touch() || !item.tip) return; const cell = event.currentTarget.parentElement; if (!cell || !(event.target instanceof Element) || !event.target.closest('.reports-qmark')) return; if (open?.key === item.key) hide(); else show(item.key, cell) }}
          // With a "?" mark the tip belongs to the label: it closes as the pointer leaves it, not only the card.
          onMouseLeave={marks && !item.split?.length ? hide : undefined}
          onFocus={item.tip ? event => { if (!touch() && event.currentTarget.parentElement) show(item.key, event.currentTarget.parentElement) } : undefined} onBlur={() => { if (!touch()) hide() }}>{item.label}{marks && item.tip && <TipMark />}</button>
        <strong>{hasSource === false ? noValue : item.text ?? (item.value === null ? noValue : <Figure value={item.value} format={format} shown={shown} />)}</strong>
        {/* The previous period's figure opens under the change chip on a tap, instead of always taking a line. */}
        {compares && <span className="reports-kpi-change">{item.delta !== undefined && (previousNote(item) ? <button type="button" className="reports-kpi-delta-toggle" aria-expanded={peek === item.key} onClick={() => setPeek(peek === item.key ? null : item.key)}><Delta delta={item.delta} invert={item.invert} /></button> : <Delta delta={item.delta} invert={item.invert} />)}</span>}
        {item.note !== undefined && (!previousNote(item) || peek === item.key) && <span className="reports-kpi-note">{wholeLiras(item.note)}</span>}
        {item.tip && <span id={id} className="reports-sr">{item.tip.replaceAll('\n', ' ')}</span>}
      </div>
    })}</div>
    {open && current && <div key={current.key} className="reports-hover-tip is-floating" aria-hidden="true" style={{ top: open.top, left: open.left, width: open.width }}>{current.tip && <TipText title={current.label} text={current.tip} />}{current.split && current.split.length > 0 && <dl className="reports-tip-split">{current.split.map(row => <div key={row.key}><dt>{row.label}</dt><dd>{row.value}</dd></div>)}</dl>}</div>}
  </div>
}

// The amounts written in a strip's note in whole liras, as its figures are ("₺422.394,54" → "₺422.395").
export const wholeLiras = (text: string) => text.replace(/₺([\d.]+),(\d{2})/gu, (_, whole: string, kurus: string) => formatTlWhole(Number(whole.replace(/\./gu, '')) + Number(kurus) / 100))

// "Önceki dönem …", or that the previous period has nothing to compare.
export const previousText = (value: string | null, zero?: string) => previousLine(value, zero)
export const percent = (value: number) => formatPercent(value)

// The previous period's account, worked out as the period's is; null when the server sent none, or when one period
// read Trendyol's statement and the other did not (their profits would not be counted alike).
export function previousMoney(data: ReportSales): Money | null {
  const account = data.previousAccount
  if (!account) return null
  const earlier: ReportSales = { ...data, summary: data.previousSummary, commission: account.commission, cost: account.cost, costs: account.costs }
  if ((financeCosts(earlier) === null) !== (financeCosts(data) === null)) return null
  return moneyBreakdown(earlier, profitSteps(earlier))
}

// Every cost of the period: the product cost and the account's deductions, the fees each on their own line, the
// categories Trendyol's statement has no record of at zero and what is not read as "—". The lines add up to the
// account's deductions plus the product cost; the largest come first.
export const costMeaning: Record<string, string> = {
  cost: 'Satılan ürünlerin alış maliyeti; ürün kartında girilen maliyet KDV dahil kabul edilir.',
  commission: 'Pazaryerlerinin satışlardan aldığı komisyon; kaydı olmayan siparişlerde tahmini.',
  cargo: 'Siparişlerin ve iadelerin kargo ücreti; faturası gelmeyenlerde tahmini.',
  returned: 'Müşterilerin iade ettiği ürünlerin tutarı; satıştan düşülür.',
  fee: 'Pazaryerlerinin sipariş ya da satış başına aldığı hizmet bedeli; kesinleşmeyenlerde tahmini.',
  payment: 'Shopify\'da kartlı ödemelerin iyzico ve Shopify kesintisi.',
  stoppage: 'Satıştan kesilen %1 e-ticaret stopajı; kesinleşmeyenlerde tahmini.',
  ads: 'Trendyol\'daki reklam harcaması; bir siparişe bağlı değildir.',
  penalty: 'Trendyol\'un kestiği cezalar (geç kargo, iptal gibi).',
  early: 'Hakedişi erken almak için ödenen kesinti.',
  otherDeduction: 'Trendyol\'un türünü belirtmeden kestiği kesinti faturaları.',
  vat: 'Satışların KDV\'sinden ürün ve masraf KDV\'si düşülünce kalan, devlete ödenecek tahmini KDV. Ürün kartındaki maliyet KDV dahil kabul edilir; KDV hariç girildiyse Net KDV ve Net Kâr olduğundan yüksek çıkar.',
  fees: 'Pazaryerlerinin hizmet bedeli ve stopaj kesintileri; Shopify\'da ödeme komisyonu.',
  marketplace: 'Pazaryerlerinin komisyonu ve hizmet bedelleri.'
}

// A line's explanation: what it is, then a short sentence more only where one is needed (what is left out of it).
export const explain = (key: string, more = '') => `${costMeaning[key] ?? ''}${more ? ` ${more}` : ''}`
// What a figure leaves out, in a few words: the commission of sales without a known rate, the cargo that could not be
// estimated.
export const commissionGap = (data: ReportSales) => (data.commission.coverage ?? 0) < 1 || (financeCosts(data)?.commission?.missingOrders ?? 0) > 0
  ? 'Oranı bilinmeyen satışlardan komisyon düşülmedi.' : ''
export const cargoGap = (data: ReportSales) => {
  const cargo = financeCosts(data)?.cargo
  return cargo && cargoPartial(cargo) ? 'Tahmin edilemeyen kargo düşülmedi.' : ''
}
export const vatMore = (net: number | null | undefined) => net !== null && net !== undefined && net < 0 ? 'Eksi olduğu için devreden KDV\'dir ve kârı artırır.' : ''

// What each deduction tied to no order of the period is, under the pointer.
export const unlinkedMeaning: Record<string, string> = {
  SERVICE_FEE: 'Siparişlerine henüz paylaştırılamayan hizmet bedeli faturaları; ödeme kayıtları okununca paylaştırılır.',
  STOPPAGE: 'Siparişlerine henüz paylaştırılamayan e-ticaret stopajı; ödeme kayıtları okununca paylaştırılır.',
  ADVERTISING: 'Reklam bütçesi yüklemeleri ve reklam bedelleri; bir siparişe bağlı değildir.',
  CARGO: 'Dönemin siparişlerine bağlanamayan kargo faturası satırları.',
  COMMISSION: 'Dönemin siparişlerine bağlanamayan komisyon kayıtları ve düzeltmeler.',
  PENALTY: 'Trendyol\'un kestiği cezalar (geç kargo, iptal gibi); bir siparişe bağlı değildir.',
  EARLY_PAYMENT: 'Hakedişi erken almak için ödenen kesinti; bir siparişe bağlı değildir.',
  OTHER_DEDUCTION: 'Trendyol\'un türünü belirtmeden kestiği kesinti faturaları; bir siparişe bağlı değildir.'
}
export const unlinkedLabels: Record<string, string> = { SERVICE_FEE: 'Hizmet Bedeli', STOPPAGE: 'Stopaj', ADVERTISING: 'Reklam', CARGO: 'Kargo', COMMISSION: 'Komisyon', PENALTY: 'Ceza', EARLY_PAYMENT: 'Erken Ödeme Kesintisi', OTHER_DEDUCTION: 'Diğer Kesinti', PAYMENT_FEE: 'Ödeme Komisyonu' }
const unlinkedTip = 'Trendyol\'un bir siparişe bağlamadan kestiği reklam, ceza, erken ödeme ve kesinti faturaları. Net Kâr\'dan düşülür.'
// The unlinked deductions' line in the profit account, under the pointer.
// The name of that line on Kâr Köprüsü and in Kâr Hesabı (owner, 7 Nov 2026: plain words, no "siparişe bağlı olmayan").
export const unlinkedTitle = 'Reklam ve Diğer Kesintiler'

// Net Kâr: what every figure that says so means (the owner's approved wording, 9 Nov 2026).
export const netProfitTip = 'Tüm kesintiler, ürün maliyeti ve reklam düşüldükten sonra kalan tahmini kâr. Dökümü Kâr Hesabı\'nda.'
// A loss reads as a loss: the title already says Tahmini Zarar, so the sentence must not call it kâr.
export const lossSentence = 'Tüm kesintiler, ürün maliyeti ve reklam düşülünce satışı aşan tahmini zarar.'
export const netLossTip = `${lossSentence} Dökümü Kâr Hesabı'nda.`

const noCostTip = 'Ürün maliyeti girilince hesaplanır. Maliyeti ürün kartından girin.'

// Net Ciro: the same sales less what went back (Satış Tutarı − İade Edilen Tutar in Kâr Hesabı); one figure on every tab.
export const netSalesTip = 'Satıcı indirimi düşülmüş satış tutarından iadeler düşülünce kalan; iptaller sayılmaz. Kâr Hesabı\'ndaki Satış Tutarı − İade Edilen Tutar.'
// İade Oranı: the units returned over every unit ordered, the cancelled ones included (ReportBuilder, as Trendyol has it).
export const returnRateTip = 'Sipariş edilen ürün adedinin (iptaller dahil) yüzde kaçının iade edildiği, Trendyol gibi.'

// The deductions of the period's dates tied to no order of the period (only from Trendyol's statement), largest first.
export function unlinkedDeductions(costs: ReportCosts | null | undefined) {
  const lines = (costs?.unlinked ?? []).filter(line => line.isCost && line.amount !== 0)
    .map(line => ({ key: line.category, label: unlinkedLabels[line.category] ?? line.label, amount: line.amount, records: line.records }))
    .sort((a, b) => b.amount - a.amount)
  return { lines, total: lines.reduce((sum, line) => sum + line.amount, 0) }
}

// Reklam ve Diğer Kesintiler, one line per kind (advertising, penalties, early payment, other deduction invoices): those
// of the period's orders and those charged in the period apart; their total is the account's (moneyBreakdown).
export function orderlessLines(data: ReportSales) {
  const finance = financeCosts(data)
  if (!finance) return unlinkedDeductions(trendyolRead(data) ? data.costs : null)
  const apart = unlinkedDeductions(trendyolRead(data) ? data.costs : null).lines
  const lines = orderlessCategories.map(category => {
    const linked = finance.lines.find(line => line.category === category)
    const extra = apart.find(line => line.key === category)
    return { key: category, label: unlinkedLabels[category] ?? category, amount: costFigure(data, category) ?? 0, records: (linked?.records ?? 0) + (extra?.records ?? 0) }
  }).filter(line => line.amount !== 0).sort((a, b) => b.amount - a.amount)
  return { lines, total: figureOf(data, orderlessCategories) }
}

// Reklam ve Diğer Kesintiler: each kind with its amount and records, then their total, which the profit takes off in a
// line of its own (moneyBreakdown). Not drawn when the period has none. A panel, like its neighbours, on Kârlılık.
export function UnlinkedDeductions({ data, panel = false }: { data: ReportSales; panel?: boolean }) {
  const tip = useId()
  const { lines, total } = orderlessLines(data)
  if (!lines.length) return null
  const title = unlinkedTitle
  const tiles = <dl className="reports-unlinked">
      {lines.map(line => <div key={line.key} className="reports-tip-host">
        <dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${line.key}`}>{line.label}<TipMark /></button></dt>
        <dd>{signedTl(line.amount)}<small>{formatCount(line.records)} kayıt</small></dd>
        <span role="tooltip" id={`${tip}-${line.key}`} className="reports-hover-tip"><TipText title={line.label} text={unlinkedMeaning[line.key] ?? 'Dönemin siparişlerine bağlanamayan kesinti.'} /></span>
      </div>)}
      <div className="is-total"><dt>Toplam</dt><dd>{signedTl(total)}<small>Net Kâr'dan düşülür</small></dd></div>
    </dl>
  return panel ? <Panel title={title} description={unlinkedTip} className="reports-card-unlinked">{tiles}</Panel>
    : <Card title={title} className="reports-card-unlinked" tip={unlinkedTip}>{() => tiles}</Card>
}

export const kurus = (value: number) => Math.round(value * 100) / 100

// A narrow card shows only the arrow; the button keeps its name for screen readers and the pointer's tooltip.
export function MoreButton({ title, onClick, compact = false }: { title: string; onClick: () => void; compact?: boolean }) {
  return <button type="button" className={`secondary reports-more${compact ? ' is-compact' : ''}`} onClick={onClick} aria-label={`${title}: Tümünü Gör`} title={compact ? 'Tümünü Gör' : undefined}>
    {!compact && 'Tümünü Gör'}<UiIcon name="arrowRight" size={14} />
  </button>
}

type FlowStep = { key: string; label: string; value: number | null; kind: 'total' | 'minus'; missing?: string; note?: string }

// Trendyol's account statement when it was read for the period (else null): the commission and cargo of the period's
// orders, real where Trendyol settled or billed them and estimated for the rest, and the store's fees by their date.
export function financeCosts(data: ReportSales): ReportCosts | null { return data.costs?.commission && data.costs.cargo ? data.costs : null }
// Whether Trendyol's statement was read for the period (its cargo and fees are then real or estimated from it).
export function trendyolRead(data: ReportSales) { return data.costs?.source === 'trendyol-finance' }

export const feeCategories = [
  { category: 'SERVICE_FEE', key: 'fee', label: 'Hizmet Bedeli' },
  { category: 'PAYMENT_FEE', key: 'payment', label: 'Ödeme Komisyonu' },
  { category: 'STOPPAGE', key: 'stoppage', label: 'Stopaj' },
  { category: 'ADVERTISING', key: 'ads', label: 'Reklam' },
  { category: 'PENALTY', key: 'penalty', label: 'Ceza' },
  { category: 'EARLY_PAYMENT', key: 'early', label: 'Erken Ödeme' },
  // A deduction invoice of a kind no rule names: still charged to the seller.
  { category: 'OTHER_DEDUCTION', key: 'otherDeduction', label: 'Diğer Kesinti' }
]

// The per-sale fees of the marketplace (Pazaryeri Masrafları beside the commission, and the Stopaj step) and the kinds of
// deduction no order carries (Reklam ve Diğer Kesintiler).
export const chargeFeeCategories = ['SERVICE_FEE', 'PAYMENT_FEE', 'STOPPAGE']
const orderlessCategories = ['ADVERTISING', 'PENALTY', 'EARLY_PAYMENT', 'OTHER_DEDUCTION']

// What Trendyol's statement charged in the period of a category tied to none of the period's orders (0 when unread).
export function apartAmount(data: ReportSales, category: string) {
  return unlinkedDeductions(trendyolRead(data) ? data.costs : null).lines.find(line => line.key === category)?.amount ?? 0
}

// A fee category's one figure, the same on Masraf Kalemleri, Kâr Köprüsü and in the profit (null when the statement is
// not read): the period's orders' amount (their records, their shares of the payout invoices, the estimate) plus what
// the period charged of it tied to none of its orders. Never counted twice: a record is either one of an order or apart.
export function costFigure(data: ReportSales, category: string): number | null {
  const finance = financeCosts(data)
  if (!finance) return null
  return (finance.lines.find(line => line.category === category)?.amount ?? 0) + apartAmount(data, category)
}
// A fee invoice whose payout's records are not read yet: its orders' estimate already takes it off, so it is only told.
export function coveredNote(data: ReportSales, category: string) {
  const line = trendyolRead(data) ? data.costs?.coveredByEstimate?.find(item => item.category === category && item.amount !== 0) : null
  return line ? `${formatTl(line.amount)} tutarındaki fatura tahminle karşılandı, ayrıca düşülmedi.` : ''
}
const figureOf = (data: ReportSales, categories: string[]) => categories.reduce((sum, category) => sum + (costFigure(data, category) ?? 0), 0)

function feeLines(costs: ReportCosts) {
  return feeCategories.flatMap(fee => {
    const line = costs.lines.find(item => item.category === fee.category)
    return line && line.amount !== 0 ? [{ ...fee, value: line.amount }] : []
  })
}

function commissionNote(costs: ReportCosts) {
  const commission = costs.commission
  if (!commission) return ''
  const missing = (commission.missingOrders ?? 0) > 0 ? ` ${formatCount(commission.missingOrders ?? 0)} siparişin komisyonu bilinmiyor (siparişte ve mağazanın bu dönemdeki satışlarında oran yok); komisyon eksik.` : ''
  return (commission.realOrders > 0
    ? `Gerçek Komisyon: ${formatCount(commission.realOrders)} siparişte pazaryerinin kendi kaydından (Trendyol hesap kesimi; Hepsiburada, N11, Pazarama finans kayıtları), iadeler düşülmüş: ${formatTl(commission.real)}. Tahmini Komisyon: kaydı olmayan siparişlerde siparişteki komisyon oranından, ${formatTl(commission.estimatedForUnsettled)}.`
    : 'Pazaryerlerinin komisyon kaydı henüz yok; tümü Tahmini Komisyon, siparişteki komisyon oranından.') + missing
}

function cargoNote(cargo: NonNullable<ReportCosts['cargo']>) {
  const platforms = cargo.platformOrders ? ` Diğer platformların ${formatCount(cargo.platformOrders)} siparişi kargo firması ve desiye göre 2026 tarifelerinden: ${formatTl(cargo.platformEstimated ?? 0)}.${desiSourcesNote(cargo.desiSources)}` : ''
  const returns = cargo.unbilledReturns ?? 0
  const unestimated = cargo.unestimatedOrders ?? 0
  if (cargo.billedOrders === 0 && cargo.unbilledOrders === 0 && returns === 0) return platforms.trim()
  const billed = `Trendyol'da ${formatCount(cargo.billedOrders)} siparişin kargo faturası geldi: ${formatTl(cargo.real)}.`
  if (cargo.unbilledOrders === 0 && returns === 0) return billed + platforms
  const unbilled = [cargo.unbilledOrders > 0 ? `${formatCount(cargo.unbilledOrders)} sipariş` : '', returns > 0 ? `${formatCount(returns)} iadenin dönüş kargosu` : ''].filter(Boolean).join(' ve ')
  const trendyolEstimated = (cargo.estimated ?? 0) - (cargo.platformEstimated ?? 0)
  const missing = unestimated > 0 ? ` ${formatCount(unestimated)} siparişin kargosu tahmin edilemedi (mağazada son 60 günde faturalı kargo yok); kargo eksik.` : ''
  return cargo.estimated === null || (unestimated > 0 && trendyolEstimated <= 0)
    ? `${billed} Faturası henüz gelmeyen ${unbilled} tahmin edilemedi.${platforms}`
    : `${billed} Faturası henüz gelmeyen ${unbilled}, mağazanın kargo tarifesiyle tahmin edildi: ${formatTl(trendyolEstimated)}.${missing}${platforms}`
}

// The cargo or commission the period's figures are short of, said where the figures are.
function cargoPartial(cargo: NonNullable<ReportCosts['cargo']>) {
  return (cargo.estimated === null && (cargo.unbilledOrders > 0 || (cargo.unbilledReturns ?? 0) > 0)) || (cargo.unestimatedOrders ?? 0) > 0
}

// Platforms with orders in the period whose cargo and fees are not counted: Trendyol when its statement was not read
// (Hepsiburada, N11, Pazarama, PttAVM and Shopify are costed from their tariffs).
const tariffPlatforms = ['HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY']
function unreadFeeCodes(data: ReportSales) {
  return data.platforms.filter(group => group.known && group.orders > 0 && !tariffPlatforms.includes(group.key) && !(group.key === 'TRENDYOL' && trendyolRead(data))).map(group => group.key)
}
function unreadFeePlatforms(data: ReportSales) { return unreadFeeCodes(data).map(platformName) }

// Satış Tutarı → Net Ciro → komisyon (and, with Trendyol's statement, cargo and fees), from known amounts only; the
// profit is moneyBreakdown's, from these deductions. Each step says what it leaves out. No steps when no order carried an
// amount.
type ProfitSteps = { steps: FlowStep[] | null; description: string }

export function profitSteps(data: ReportSales): ProfitSteps {
  const now = data.summary
  const commission = data.commission
  const finance = financeCosts(data)
  const unread = unreadFeePlatforms(data)
  const description = finance
    ? `${trendyolRead(data) ? 'Trendyol\'da komisyon ve kargo gerçek tutarla, henüz kesinleşmeyenler tahmini; hizmet bedeli ve stopaj ödeme günü faturasından siparişlerine paylaştırılır, faturası gelmeyenlerde tahmini. Siparişe bağlı değil: reklam, ceza ve henüz paylaştırılamayan faturalar Net Kâr\'dan Reklam ve Diğer Kesintiler satırıyla düşülür.' : 'Komisyon siparişteki orandan tahmini.'} Diğer platformların kargo ve kesintileri 2026 tarifelerinden tahmini.${unread.length ? ` ${unread.join(', ')} kargo ve hizmet bedeli düşülmedi.` : ''}`
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
        note: fees.length ? `${fees.map(fee => fee.label).join(', ')} · dönemin siparişlerinin, Trendyol cari hesabından ya da tahmini` : 'Bu dönemde kesinti yok' },
      { key: 'payout', label: 'Masraflar Düşülmüş', value: now.netRevenue - charged, kind: 'total',
        note: cargoPartial(finance.cargo) ? 'Tahmin edilemeyen kargo eksik; ürün maliyeti hariç'
          : (finance.commission.missingOrders ?? 0) > 0 ? 'Bilinmeyen komisyon eksik; ürün maliyeti hariç' : 'Gerçek ve tahmini kalemlerle; ürün maliyeti hariç' }
    )
  } else steps.push(
    { key: 'commission', label: 'Tahmini Komisyon', value: hasCommission ? commission.estimatedCommission : null, kind: 'minus', missing: 'Komisyon bilinmiyor',
      note: hasCommission ? `Siparişteki orandan tahmin edildi; satışların ${formatPercent(commission.coverage)} kadarının oranı biliniyor.${partialCommission ? ' Kalanından komisyon düşülmedi.' : ''}` : 'Siparişte komisyon gelmedi.' },
    { key: 'payout', label: 'Komisyon Düşülmüş', value: hasCommission ? now.netRevenue - commission.estimatedCommission : null, kind: 'total', missing: '—',
      note: hasCommission ? (partialCommission ? 'Tahmini; gerçek tutar daha düşük olabilir' : 'Tahmini; kesinleşmiş hakediş değil') : undefined }
  )
  return { steps, description }
}

const unreadCostLines = ['Kargo Faturası', 'Hizmet Bedeli', 'Stopaj', 'Reklam', 'Ceza']

// Averages over the packages whose times were measured, weighted by that count, so a carrier with one measured
// package does not weigh as much as one with a hundred.
export function weightedHours(rows: ReportSales['cargo'], hours: (row: ReportSales['cargo'][number]) => number | null, measured: (row: ReportSales['cargo'][number]) => number) {
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

export type BarItem = { key: string; label: string; value: number; muted?: boolean; note?: string }

// The top known groups by a value, with the unknown bucket after them, muted.
export function topGroups(groups: ReportGroup[], value: (group: ReportGroup) => number, note?: (group: ReportGroup) => string, limit = 8): BarItem[] {
  const known = groups.filter(group => group.known && value(group) > 0).sort((a, b) => value(b) - value(a)).slice(0, limit)
  const unknown = groups.filter(group => !group.known && value(group) > 0)
  return [...known, ...unknown].map(group => ({ key: group.key, label: group.label, value: value(group), muted: !group.known, note: note?.(group) }))
}

// Platforms that send no amounts have no revenue to draw; their counts are written out instead.
export function NoAmountNote({ platforms }: { platforms: ReportGroup[] }) {
  const noAmount = platforms.filter(group => group.ordersWithAmount === 0)
  if (!noAmount.length) return null
  return <p className="reports-note">{noAmount.map(group => `${platformName(group.label)}: tutar — · ${formatNumber(group.soldQuantity)} adet, ${formatCount(group.orders)} sipariş`).join(' — ')}</p>
}

// The platforms added up into one row under the table: counts and amounts summed, the rates worked out again from the
// sums (İade Oranı = returned ÷ sold, as on each row), never summed.
function groupTotal(rows: ReportGroup[]): ReportGroup {
  const sum = (pick: (row: ReportGroup) => number) => rows.reduce((total, row) => total + pick(row), 0)
  const sold = sum(row => row.soldQuantity), returned = sum(row => row.returnedQuantity)
  return { key: 'total', label: 'Toplam', known: true, isTest: false, orders: sum(row => row.orders), soldQuantity: sold, returnedQuantity: returned, returnClaims: sum(row => row.returnClaims),
    returnRate: sold > 0 ? returned / sold : null, ordersWithAmount: sum(row => row.ordersWithAmount), grossSales: sum(row => row.grossSales), netRevenue: sum(row => row.netRevenue),
    estimatedCommission: sum(row => row.estimatedCommission), commissionCoveredRevenue: sum(row => row.commissionCoveredRevenue), children: [], split: [] }
}

export function TotalRow({ rows, columns, expand }: { rows: ReportGroup[]; columns: Column<ReportGroup>[]; expand?: boolean }) {
  const total = groupTotal(rows)
  return <tr className="reports-total-row">{expand && <td className="reports-expand-col" />}{columns.map(item => <td key={item.key} className={item.numeric ? 'is-num' : undefined}>{item.key === 'label' ? <strong>Toplam</strong> : item.render ? item.render(total) : item.value(total) ?? '—'}</td>)}</tr>
}

export const weekdays = ['Pazartesi', 'Salı', 'Çarşamba', 'Perşembe', 'Cuma', 'Cumartesi', 'Pazar']
