import { useContext, useId, useState, type CSSProperties } from 'react'
import { useQuery } from '@tanstack/react-query'
import { EmptyState } from '../../../shared/components'
import { valueWithUnit } from '../../../shared/metric-value'
import { PanelSelect } from '../../../shared/PanelSelect'
import { change, noValue, formatCount, formatDay, formatHours, formatNumber, formatPercent, formatTl, formatTlShort, formatTlWhole, platformName, type Change, type ReportGroup, type ReportReason, type ReportSales } from '../report-format'
import { AdReportModal, adReportQuery, adReportRow, combined, reportAge, roasText, staleDays, type AdReports } from '../AdReport'
import { Columns, Donut, Progress, useReveal, type ColumnPoint, type Part } from '../report-visuals'
import { MetricSourceContext, type DetailKey, Delta, type OpenDetail, TipMark, TipText, previousLine, Card, type Money, moneyBreakdown, profitReason, missingCostTip, profitLabel, estimateLine, signedTl, type Kpi, KpiStrip, wholeLiras, previousText, percent, previousMoney, explain, commissionGap, cargoGap, vatMore, netProfitTip, netLossTip, netSalesTip, returnRateTip, unlinkedDeductions, financeCosts, trendyolRead, feeCategories, costFigure, coveredNote, profitSteps, weightedHours, weekdays } from './report-parts'
import { ProfitBridge } from './ProfitBridge'

// Genel Bakış: the period's six figures in one strip, its costs round a donut (the deductions tied to no order of the
// period apart, under it), the way from the sales to the profit
// beside the period's buckets, then the product, order and advertising figures and the returns and operations. Every
// amount comes from one account (moneyBreakdown), so the cards agree with each other and with the profit tab; a row's
// explanation shows while the pointer or the focus is on it. Each card plays its animation once, when first in view.
export function Overview({ data, onMore }: { data: ReportSales; onMore: OpenDetail }) {
  const money = moneyBreakdown(data, profitSteps(data))
  // Trendyol's ad report the seller uploads: campaign totals, apart from the period's figures (AdReport.tsx).
  const ads = useQuery(adReportQuery)
  const [adsOpen, setAdsOpen] = useState(false)
  return <div className="reports-overview">
    <OverviewFigures data={data} money={money} />
    <CostLinesCard data={data} money={money} />
    <div className="reports-overview-row is-two">
      <ProfitBridge data={data} money={money} onMore={onMore} />
      <PeriodCard data={data} />
    </div>
    <div className="reports-hero-grid">
      {overviewCards(data, money, ads.data, ads.isError, () => setAdsOpen(true)).map(card => <HeroCard key={card.key} card={card} onMore={onMore} />)}
    </div>
    <AdReportModal open={adsOpen} onClose={() => setAdsOpen(false)} data={ads.data} />
  </div>
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
  // The best day as "5 Kasım, Perşembe"; a week or a month by its own name.
  const bestWhen = (point: ColumnPoint) => {
    if (data.granularity !== 'day') return point.title
    const day = new Date(`${point.key}T12:00:00`)
    return `${day.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long' })}, ${weekdays[(day.getDay() + 6) % 7]}`
  }
  // Money in whole liras on the facts, as in the strip; a count as it is.
  const short = money ? formatTlWhole : format
  return <Card title={names.title} className="reports-card-period"
    actions={<PanelSelect className="reports-measure-select" ariaLabel="Grafik ölçüsü" value={measure} options={(Object.keys(measures) as Measure[]).map(key => ({ value: key, label: measures[key] }))} onChange={setMeasure} />}>
    {shown => <>
      {noAmount ? <EmptyState icon="chart" title="Veri Yok">Bu dönemde tutarı olan sipariş yok.</EmptyState>
        : <Columns points={points} format={format} axisFormat={money ? formatTlShort : formatNumber} shown={shown} label={`${measures[measure]} dönem içinde`} />}
      {/* One row of three columns divided by hairlines, no boxes (owner, 7 Nov 2026): the best bucket's figure and its
          date, the average with its change and the previous one, and how many buckets beat the same bucket of the
          previous period, drawn as a share of it. Money in whole liras here, to the kuruş in the explanation. */}
      <dl className="reports-period-facts">
        <div className="reports-tip-host"><dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-best`}>En iyi {unit}</button></dt>
          <dd><strong style={fitChars(hasBest && best ? short(best.value) : '—')}>{hasBest && best ? short(best.value) : '—'}</strong><small>{hasBest && best ? bestWhen(best) : noValue}</small></dd>
          <span role="tooltip" id={`${tip}-best`} className="reports-hover-tip"><TipText title={`En İyi ${names.unit}`} text={`Dönemin ${measureOf[measure]} en yüksek ${names.its}${hasBest && best ? `: ${format(best.value)}` : ''}; grafikte mor sütunla gösterilir.`} /></span></div>
        <div className="reports-tip-host"><dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-average`}>{names.every} ortalama</button></dt>
          <dd><strong style={fitChars(noAmount || average === null ? '—' : short(average))}>{noAmount || average === null ? '—' : short(average)}</strong>
            <small>{!noAmount && average !== null && <Delta delta={change(average, previousAverage)} />}{previousAverage === null ? 'önceki dönem: —' : `önceki ${short(previousAverage)}`}</small></dd>
          <span role="tooltip" id={`${tip}-average`} className="reports-hover-tip"><TipText title={`${names.every} Ortalama`} text={`Bir ${names.of} ortalama ${measureOf[measure]}${noAmount || average === null ? '' : `: ${format(average)}`}.\n${previousLine(previousAverage === null ? null : format(previousAverage))}`} /></span></div>
        <div className="reports-tip-host"><dt><button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-ahead`}>Önceki döneme göre</button></dt>
          <dd><strong className={!noAmount && compared.length > 0 && ahead > compared.length - ahead ? 'is-good' : undefined} style={fitChars(noAmount || !compared.length ? '—' : `${ahead} ${unit} önde`)}>{noAmount || !compared.length ? '—' : `${ahead} ${unit} önde`}</strong>
            {!noAmount && compared.length > 0 ? <Progress value={ahead / compared.length} shown={shown} tone={ahead > compared.length - ahead ? 'good' : undefined} /> : <small>Önceki dönem: —</small>}</dd>
          <span role="tooltip" id={`${tip}-ahead`} className="reports-hover-tip"><TipText title="Önceki Döneme Göre" text={`Önceki dönemin aynı sıradaki ${names.from} yüksek geçen ${names.many}${noAmount || !compared.length ? '' : `: ${ahead} ${unit} önde, ${compared.length - ahead} ${unit} geride; toplam ${compared.length} ${unit}`}.`} /></span></div>
      </dl>
    </>}
  </Card>
}

// How much of the cost is entered, said only when some of it is missing: with the estimate when the profit is shown.
const costCoverageText = (money: Money) => (money.costCoverage ?? 0) >= 1 ? ''
  : money.profit !== null ? `Satılan adetlerin ${formatPercent(money.costCoverage)} kadarının maliyeti girili. ${money.estimate}`
    : `Satılan adetlerin ${formatPercent(money.costCoverage)} kadarının maliyeti girili; Net Kâr, en az %95'inin maliyeti girilince hesaplanır.`

// The sentence the profit's other figures (per order, per unit, the margin before advertising) add while it is unknown.
const profitLater = (money: Money | null) => (money?.uncosted ?? 0) > 0 ? ' Satılan adetlerin en az %95\'inin maliyeti girilince hesaplanır.' : ' Ürün maliyeti girilince hesaplanır.'

// The period's six figures in one strip: Net Ciro (sales less returns, owner 14 Nov 2026), the estimated net profit, its
// margin over Net Ciro and over the product cost, the orders and the units sold, each against the previous period.
function OverviewFigures({ data, money }: { data: ReportSales; money: Money | null }) {
  const now = data.summary
  const then = data.previousSummary
  const earlier = previousMoney(data)
  const noAmount = now.ordersWithAmount === 0
  // No order at all in the period: the sources exist and every figure is a real zero ("₺0"); orders without an amount are an unknown ("—").
  const empty = noAmount && now.orders === 0
  const profit = money?.profit ?? null
  const onCost = profit !== null && money?.cost ? profit / money.cost : null
  const earlierProfit = earlier?.profit ?? null
  const earlierOnCost = earlierProfit !== null && earlier?.cost ? earlierProfit / earlier.cost : null
  // The margin before the advertising, as the Kârlılık card has it.
  const spend = adSpend(data)
  const beforeAds = spend !== null && profit !== null && now.netRevenue > 0 ? (profit + spend) / now.netRevenue : null
  // What each platform brought to the figure, the biggest first; a platform without the figure is left out.
  const split = (pick: (row: ReportGroup) => number | null, format: (value: number) => string) => data.platforms
    .map(row => ({ row, value: pick(row) })).filter((item): item is { row: ReportGroup; value: number } => item.value !== null && item.value !== 0)
    .sort((left, right) => right.value - left.value).map(item => ({ key: item.row.key, label: platformName(item.row.label), value: format(item.value) }))
  const items: Kpi[] = [
    { key: 'net', label: 'Net Ciro', value: noAmount && !empty ? null : now.netRevenue, format: formatTl, delta: noAmount && !empty ? null : change(now.netRevenue, then.netRevenue),
      note: noAmount && !empty ? previousText(null, '₺0') : `Toplam ${formatTl(now.grossSales)} · İade ${formatTl(now.returnedAmount)}`,
      tip: `${netSalesTip} Alttaki toplam, iadeler düşülmeden önceki satış tutarıdır.\n${previousLine(then.ordersWithAmount > 0 ? formatTl(then.netRevenue) : null, '₺0')}`, split: split(row => row.ordersWithAmount > 0 ? row.netRevenue : null, formatTl) },
    { key: 'profit', label: profitLabel(money), value: empty ? 0 : profit, format: formatTl,
      delta: change(profit, earlierProfit),
      note: profit === null ? profitReason(money, noAmount) ?? 'Sipariş başına ₺0' : now.ordersWithAmount === 0 ? 'Sipariş başına ₺0' : `Sipariş başına ${signedTl(profit / now.ordersWithAmount)}`,
      tip: `${profit === null ? missingCostTip(money) : profit < 0 ? netLossTip : netProfitTip}${estimateLine(money)}\n${previousLine(earlierProfit === null ? null : signedTl(earlierProfit), '₺0')}`,
      tone: profit === null ? undefined : profit < 0 ? 'bad' : profit > 0 ? 'good' : undefined },
    { key: 'margin', label: 'Kâr Marjı', value: money?.margin ?? null, format: percent, delta: change(money?.margin ?? null, earlier?.margin ?? null, 'rate'),
      note: `Reklam öncesi ${beforeAds === null ? '%0' : formatPercent(beforeAds)}`,
      tip: `Net kârın Net Ciro'ya oranı. Reklam öncesi oranda Trendyol reklamı düşülmez.${estimateLine(money)}\n${previousLine(formatPercent(earlier?.margin), '%0')}`, tone: (money?.margin ?? 0) < 0 ? 'bad' : undefined },
    { key: 'cost', label: 'Maliyete Göre Kâr', value: onCost, format: percent, delta: change(onCost, earlierOnCost, 'rate'),
      note: `Ürün maliyeti ${money?.cost && money.profit !== null ? formatTl(money.cost) : '₺0'}`,
      tip: `Net kârın ürün maliyetine oranı: ürüne harcanan her liranın getirdiği kâr.${estimateLine(money)}\n${previousLine(formatPercent(earlierOnCost), '%0')}`, tone: (onCost ?? 0) < 0 ? 'bad' : undefined },
    { key: 'orders', label: 'Sipariş', value: now.orders, format: formatCount, delta: change(now.orders, then.orders), note: `${formatCount(now.cancelledOrders)} iptal · sepet ${noAmount ? '₺0' : formatTl(now.averageOrderValue)}`,
      tip: `Dönemde gelen sipariş sayısı; iptal edilenler sayılmaz, tutarı okunmayan ve TL dışındaki siparişler sayılır. Ana sayfadaki Toplam Sipariş yalnız tutarı olanları sayar; sepet de tutarı olan siparişlere bölünür.\n${previousLine(formatCount(then.orders))}`, split: split(row => row.orders, formatCount) },
    // The units sold (cancellations out, returns not taken off), with what the customers kept and sent back under it;
    // the Satış card under the strip leads with the same figure (owner, 7 Nov 2026: the strip as 3 + 3).
    { key: 'sold', label: 'Satılan Adet', value: now.soldQuantity, delta: change(now.soldQuantity, then.soldQuantity),
      note: `Net ${formatNumber(now.keptQuantity)} · ${formatNumber(now.returnedQuantity)} iade`,
      tip: `Dönemde satılan ürün adedi; iptaller sayılmaz, iadeler düşülmez. Net adet, iadeler düşülünce müşteride kalandır.\n${previousLine(formatNumber(then.soldQuantity))}`,
      split: split(row => row.soldQuantity, formatNumber) }
  ]
  return <KpiStrip label="Dönemin göstergeleri" items={items} />
}

type OverviewCost = { key: string; label: string; value: number | null; tone: string; note: string }

const notRead = 'Bu seçimde hesaplanamadı.'

function costLines(data: ReportSales, money: Money): OverviewCost[] {
  const finance = financeCosts(data)
  const part = (key: string) => money.deductions.find(item => item.key === key)
  const commission = part('commission')
  const cargo = part('cargo')
  // The deductions of the period's dates tied to none of its orders (Trendyol's statement: advertising, other deduction
  // invoices, the service fee not shared over its orders yet) go into their own category's line, so the ring holds every
  // cost Net Kâr takes off (owner, 7 Nov 2026: one cost card, no separate card for them).
  const unlinked = unlinkedDeductions(trendyolRead(data) ? data.costs : null).lines
  const apart = (category: string) => unlinked.find(line => line.key === category)
  const lines: OverviewCost[] = [
    { key: 'cost', label: 'Ürün Maliyeti', value: money.cost, tone: 'cost', note: explain('cost', money.cost === null ? 'Ürün kartında maliyet girilince hesaplanır.' : costCoverageText(money)) },
    // The account's commission and cargo already hold what the period charged of them apart (moneyBreakdown).
    { key: 'commission', label: commission?.label ?? 'Komisyon', value: commission?.value ?? null, tone: 'commission',
      note: commission ? explain('commission', commissionGap(data)) : 'Pazaryerlerinin satışlardan aldığı komisyon. Siparişlerde komisyon bilgisi gelmedi.' },
    { key: 'cargo', label: 'Kargo', value: finance ? cargo?.value ?? 0 : null, tone: 'cargo', note: explain('cargo', finance ? cargoGap(data) : notRead) },
    // The service fee and the withholding tax are on every order. The rest is listed only when the period carries one,
    // on an order or apart ("Diğer Kesintiler" and "Ödeme Komisyonu" also not when nothing is read).
    ...feeCategories.filter(fee => ['SERVICE_FEE', 'STOPPAGE'].includes(fee.category) || apart(fee.category) || (finance ? finance.lines.some(item => item.category === fee.category && item.amount !== 0) : !['OTHER_DEDUCTION', 'PAYMENT_FEE'].includes(fee.category))).map(fee =>
      ({ key: fee.key, label: fee.category === 'EARLY_PAYMENT' ? 'Erken Ödeme Kesintisi' : fee.category === 'OTHER_DEDUCTION' ? 'Diğer Kesintiler' : fee.label,
        value: costFigure(data, fee.category), tone: fee.key, note: explain(fee.key, finance ? coveredNote(data, fee.category) : notRead) })),
    { key: 'vat', label: 'Net KDV', value: finance?.netVat?.net ?? null, tone: 'vat', note: explain('vat', finance?.netVat ? vatMore(finance.netVat.net) : notRead) }
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
  if (!money) return <Card title="Masraf Kalemleri" className="reports-card-costlines">{() => <EmptyState icon="chart" title="Veri Yok">Bu dönemde tutarı olan sipariş yok.</EmptyState>}</Card>
  const lines = costLines(data, money)
  const parts: Part[] = lines.filter(line => line.value !== null && line.value > 0).map(line => ({ key: line.key, label: line.label, value: line.value ?? 0, tone: line.tone }))
  // The total is what the ring draws, so the shares add up to 100 %.
  const total = parts.reduce((sum, part) => sum + part.value, 0)
  const picked = parts.find(part => part.key === active) ?? null
  const pick = (key: string) => parts.some(part => part.key === key) ? setActive(key) : undefined
  return <Card title="Masraf Kalemleri" className="reports-card-costlines">
    {shown => <div className="reports-costs">
      <Donut parts={parts} shown={shown} active={active} onActive={setActive} label={`Masraflar: ${parts.map(part => `${part.label} ${formatPercent(part.value / total)}`).join(', ')}`}>
        {/* A new key each time, so the middle fades in afresh. */}
        <div key={picked?.key ?? 'total'} className="reports-donut-swap" aria-live="polite">{picked ? <>
          <small>{picked.label}</small>
          <strong>{formatTlWhole(picked.value)}</strong>
          <span>Masraf payı {formatPercent(total > 0 ? picked.value / total : null)}</span>
        </> : <>
          <small>Toplam Masraf</small>
          <strong>{formatTlWhole(total)}</strong>
          <span>Satış payı {formatPercent(money.total > 0 ? total / money.total : null)}</span>
        </>}</div>
      </Donut>
      {/* A line's name is a button like the panel's "?": a tap on a phone or Tab on a keyboard opens its explanation too. */}
      <ul className="reports-costlines">{lines.map(line => <li key={line.key} className={`reports-tip-host${line.value === null || line.value === 0 ? ' is-zero' : ''}${line.key === picked?.key ? ' is-active' : ''}`}
        onMouseEnter={() => pick(line.key)} onMouseLeave={() => setActive(null)}>
        <i className={`is-${line.value ? line.tone : 'none'}`} aria-hidden="true" />
        <button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${line.key}`} onFocus={() => pick(line.key)} onBlur={() => setActive(null)}>{line.label}<TipMark /></button>
        <b>{line.value === null ? '—' : signedTl(line.value)}</b>
        <em>{line.value !== null && line.value > 0 && total > 0 ? formatPercent(line.value / total) : ''}</em>
        <span role="tooltip" id={`${tip}-${line.key}`} className="reports-hover-tip"><TipText title={line.label} text={line.note} /></span>
      </li>)}</ul>
    </div>}
  </Card>
}

// One figure of the overview cards (overviewCards): its name, value and explanation, a second line (sub), the change
// against the previous period and the tab it opens. tone: warn only where a threshold is passed, and the explanation
// names the threshold; bad for a loss.
type Metric = { key: string; label: string; value: string; sub?: string; note: string; delta?: Change | null; invert?: boolean; previous?: string; tone?: 'warn' | 'bad'; detail?: DetailKey
  // A row that opens a window of its own (the Trendyol ad report) instead of a tab.
  onOpen?: () => void }

// Satış ve Ürün: the average sale price and profit of a unit and the seller's discount off the list price.
function productMetrics(data: ReportSales, money: Money | null): Metric[] {
  const now = data.summary
  const noAmount = now.ordersWithAmount === 0
  const profit = money?.profit ?? null
  const list = now.listAmount ?? 0
  const discount = now.discountAmount ?? null
  const discountKnown = !noAmount && discount !== null && list > 0
  return [
    { key: 'price', label: 'Adet Başına Satış', value: noAmount || now.soldQuantity === 0 ? '—' : formatTl(now.grossSales / now.soldQuantity), note: 'Bir ürünün ortalama satış fiyatı; iadeler düşülmeden.' },
    { key: 'profit', label: 'Adet Başına Kâr', value: profit === null || now.keptQuantity === 0 ? '—' : signedTl(profit / now.keptQuantity), tone: profit !== null && profit < 0 ? 'bad' : undefined,
      note: `Müşteride kalan her ürüne düşen ortalama tahmini kâr.${profit === null ? profitLater(money) : money?.estimate ? ` ${money.estimate}` : ''}` },
    { key: 'discount', label: 'Satıcı İndirimi', value: discountKnown ? formatTl(discount ?? 0) : '—', sub: discountKnown ? `liste fiyatının ${formatPercent((discount ?? 0) / list)} kadarı` : undefined,
      note: `Satıcının liste fiyatından yaptığı indirim; pazaryerinin indirimi sayılmaz. ${discountKnown ? 'Liste fiyatı yalnız Trendyol ve Hepsiburada siparişlerinde okunur.' : 'Bu dönemde liste fiyatı okunmadı; yalnız Trendyol ve Hepsiburada siparişlerinde okunur.'}` },
    { key: 'vatFree', label: 'KDV Hariç Net Satış', value: noAmount || now.vatFreeNetRevenue === undefined ? '—' : formatTl(now.vatFreeNetRevenue),
      note: 'İadeler düşülmüş satışın KDV hariç tutarı, muhasebeyle karşılaştırmak için. TL dışındaki siparişler sayılmaz.' }
  ]
}

// A text cut to a length with an ellipsis, so a long reason does not wrap a card's row.
const shortText = (text: string, length: number) => text.length > length ? `${text.slice(0, length - 1).trimEnd()}…` : text

// Sipariş ve Müşteri: what an order brought in and left, its cargo and its units, and the customers who came back.
function orderMetrics(data: ReportSales, money: Money | null): Metric[] {
  const now = data.summary
  const before = data.previousSummary
  const noAmount = now.ordersWithAmount === 0
  const profit = money?.profit ?? null
  const finance = financeCosts(data)
  const cargo = finance?.cargo ?? null
  const cargoOrders = finance && cargo ? finance.financeOrders + (cargo.platformOrders ?? 0) : 0
  const taken = money?.deductions.filter(part => part.key === 'commission' || part.key === 'fees') ?? []
  const marketplaceTaken = taken.length ? taken.reduce((sum, part) => sum + part.value, 0) : null
  return [
    // Its change goes in the explanation: a badge beside it would wrap the name and put the card's rows out of line.
    { key: 'basket', label: 'Sipariş Başına Satış', value: noAmount ? '—' : formatTl(now.averageOrderValue),
      note: `Bir siparişin ortalama satış tutarı.\n${previousLine(before.ordersWithAmount > 0 ? formatTl(before.averageOrderValue) : null, '₺0')}` },
    { key: 'profit', label: 'Sipariş Başına Kâr', value: profit === null || now.ordersWithAmount === 0 ? '—' : signedTl(profit / now.ordersWithAmount), tone: profit !== null && profit < 0 ? 'bad' : undefined,
      note: `Bir siparişe düşen ortalama tahmini kâr.${profit === null ? profitLater(money) : money?.estimate ? ` ${money.estimate}` : ''}` },
    { key: 'cargo', label: 'Sipariş Başına Kargo', value: finance && cargo && cargoOrders > 0 ? formatTl(cargo.combined / cargoOrders) : '—',
      note: `Bir siparişin ortalama kargo ücreti.${finance && cargo ? '' : ` ${notRead}`}` },
    // The same cargo over the units the customers kept: the denominator of Adet Başına Kâr, so the two compare.
    { key: 'unitCargo', label: 'Adet Başına Kargo', value: finance && cargo && cargoOrders > 0 && now.keptQuantity > 0 ? formatTl(cargo.combined / now.keptQuantity) : '—',
      note: `Müşteride kalan bir ürüne düşen ortalama kargo; iadelerin kargosu da dahildir.${finance && cargo && cargoOrders > 0 ? '' : ` ${notRead}`}` },
    // The marketplaces' share of an order: the commission and the fees (service fee, withholding tax) of the bridge's
    // Pazaryeri Masrafları and Stopaj, from the same account.
    { key: 'fees', label: 'Sipariş Başına Kesinti', value: marketplaceTaken === null || noAmount ? '—' : formatTl(marketplaceTaken / now.ordersWithAmount),
      note: marketplaceTaken === null ? 'Pazaryerinin bir siparişten aldığı ortalama kesinti. Bu dönemde komisyon bilinmiyor.'
        : finance ? 'Pazaryerinin bir siparişten aldığı ortalama komisyon, hizmet bedeli ve stopaj.' : 'Pazaryerinin bir siparişten aldığı ortalama komisyon; hizmet bedeli ve stopaj bu seçimde okunmadı.' },
    { key: 'units', label: 'Sipariş Başına Adet', value: now.orders > 0 ? formatNumber(now.soldQuantity / now.orders) : '—', note: 'Bir siparişteki ortalama ürün adedi.' },
    { key: 'repeat', label: 'Tekrar Gelen Müşteri', value: formatPercent(data.customers.repeatRate), detail: 'customers',
      note: 'Dönemde birden fazla sipariş veren müşterilerin oranı. Müşteri mağaza bazında tanınır.' }
  ]
}

// The advertising spend of the period: read from Trendyol's statement only (else null). It is tied to no order, so it is
// read from the deductions tied to no order (and, should Trendyol ever tie one to an order, from the costs).
function adSpend(data: ReportSales): number | null {
  const finance = trendyolRead(data) ? financeCosts(data) : null
  return finance ? (finance.lines.find(line => line.category === 'ADVERTISING')?.amount ?? 0) + (finance.unlinked?.find(line => line.category === 'ADVERTISING')?.amount ?? 0) : null
}

// Maliyet ve Reklam: the marketplaces' commission rate, then what was spent on advertising (Trendyol only) and what it
// weighs against the profit and Trendyol's revenue; what is not read says so.
function adMetrics(data: ReportSales, money: Money | null): Metric[] {
  const spend = adSpend(data)
  const profit = money?.profit ?? null
  // The advertising is Trendyol's, so its share is taken of Trendyol's net revenue, not of every platform's.
  const trendyol = data.platforms.find(row => row.key.toUpperCase() === 'TRENDYOL' && row.ordersWithAmount > 0)
  const revenue = trendyol?.netRevenue ?? null
  const trendyolOrders = trendyol?.ordersWithAmount ?? 0
  const sales = data.summary.netRevenue
  // The profit with the advertising given back, over the sales: what the sales leave before the advertising.
  const beforeAds = spend !== null && profit !== null && sales > 0 ? (profit + spend) / sales : null
  return [
    { key: 'commission', label: 'Komisyon Oranı', value: formatPercent(data.commission.effectiveRate), note: 'Pazaryerlerinin satıştan aldığı ortalama komisyon oranı; oranı bilinen satışlar üzerinden.' },
    { key: 'spend', label: 'Trendyol Reklamı', value: spend === null ? '—' : formatTl(spend),
      note: spend === null ? 'Dönemde reklama harcanan tutar. Yalnız Trendyol cari hesabından okunur; bu seçimde okunmadı.' : 'Dönemde Trendyol reklamlarına harcanan tutar; Net Kâr\'dan düşülür. Diğer pazaryerlerinin reklamı okunmuyor.' },
    { key: 'tacos', label: 'Reklam Payı (TACoS)', value: spend !== null && revenue !== null && revenue > 0 ? formatPercent(spend / revenue, 2) : '—',
      note: 'Trendyol net cirosunun reklama giden payı.' },
    { key: 'perOrder', label: 'Sipariş Başına Reklam', value: spend !== null && trendyolOrders > 0 ? formatTl(spend / trendyolOrders) : '—',
      note: 'Bir Trendyol siparişine düşen ortalama reklam harcaması.' },
    { key: 'beforeAds', label: 'Reklam Öncesi Marj', value: beforeAds === null ? '—' : formatPercent(beforeAds), tone: beforeAds !== null && beforeAds < 0 ? 'bad' : undefined,
      note: `Trendyol reklamı düşülmeden kalan kârın Net Ciro'ya oranı.${beforeAds === null ? ' Ürün maliyeti girilince ve Trendyol cari hesabı okununca hesaplanır.' : ''}` }
  ]
}

function returnMetrics(data: ReportSales): Metric[] {
  const now = data.summary
  const before = data.previousSummary
  const warning = data.cancellations.warningRate
  const cancelled = data.cancellations.cancelledAmount
  // The billed return cargo of Trendyol, read from its statement only (else unknown): the return lines of the period's
  // orders and the cross-border return operation fees dated in it. A server that sends neither leaves it unknown.
  const cargo = trendyolRead(data) ? financeCosts(data)?.cargo ?? null : null
  const returnCargo = cargo && cargo.returnBilled !== undefined ? cargo.returnBilled + (cargo.returnOperationFees ?? 0) : null
  // The reason given most, among the known ones, with its share of the claims with a reason.
  const reasonsTotal = data.returnReasons.reduce((sum, reason) => sum + reason.count, 0)
  const topReason = [...data.returnReasons].filter(reason => reason.known && reason.count > 0).sort((a, b) => b.count - a.count)[0]
  return [
    { key: 'rate', label: 'İade Oranı', value: formatPercent(now.returnRate), sub: `önceki ${formatPercent(before.returnRate)}`, delta: change(now.returnRate, before.returnRate, 'rate'), invert: true, previous: formatPercent(before.returnRate), detail: 'returns',
      note: `${returnRateTip}\n${previousLine(formatPercent(before.returnRate), '%0')}` },
    // The amount the customers paid for what they sent back: the İade Edilen Tutar taken off on Kâr Köprüsü.
    { key: 'returned', label: 'İade Edilen', value: valueWithUnit(now.returnedQuantity, 'adet', formatNumber), sub: `${formatCount(now.returnClaims)} talep · ${formatTl(now.returnedAmount)}`, detail: 'returns',
      note: 'Müşterilerin iade ettiği ürün adedi; reddedilen ve iptal edilen talepler sayılmaz.' },
    { key: 'cargo', label: 'İade Kargo Zararı', value: returnCargo === null ? '—' : formatTl(returnCargo), sub: returnCargo === null ? undefined : 'yalnız Trendyol', detail: 'returns',
      note: 'İadelerin faturası gelen kargo ve operasyon maliyeti, yalnız Trendyol.' },
    // The two parts of İade Kargo Zararı apart (the overview's İade card, owner 7 Nov 2026), the same statement figures.
    { key: 'returnCargo', label: 'İade Kargo Bedeli', value: returnCargo === null ? '—' : formatTl(cargo?.returnBilled ?? 0), detail: 'returns',
      note: `İadelerin faturası gelen dönüş kargosu, yalnız Trendyol.${returnCargo === null ? ' Trendyol cari hesabı bu seçimde okunmadı.' : ''}` },
    { key: 'operation', label: 'Yurt Dışı Operasyon İade Bedeli', value: returnCargo === null ? '—' : formatTl(cargo?.returnOperationFees ?? 0), detail: 'returns',
      note: `Trendyol'un yurt dışı siparişlerin iadesi için kestiği operasyon bedeli.${returnCargo === null ? ' Trendyol cari hesabı bu seçimde okunmadı.' : ''}` },
    { key: 'returnCost', label: 'Toplam İade Maliyeti', value: returnCargo === null ? '—' : formatTl(returnCargo), tone: returnCargo !== null && returnCargo > 0 ? 'bad' : undefined, detail: 'returns',
      note: `İadelerin satıcıya kargo ve operasyon maliyeti, yalnız Trendyol; ürün tutarı sayılmaz.${returnCargo === null ? ' Trendyol cari hesabı bu seçimde okunmadı.' : ''}` },
    { key: 'reason', label: 'En Sık İade Nedeni', value: topReason ? shortText(topReason.reason, 18) : '—', sub: topReason ? `taleplerin ${formatPercent(topReason.count / reasonsTotal)} kadarı` : undefined, detail: 'returns',
      note: `İade taleplerinde en sık bildirilen neden${topReason ? `: ${topReason.reason}` : ''}.` },
    { key: 'cancel', label: 'İptal Oranı', value: formatPercent(now.cancelRate), tone: (now.cancelRate ?? 0) > warning ? 'warn' : undefined, detail: 'cancellations',
      note: `Sipariş edilen ürün adedinin yüzde kaçının iptal edildiği. ${formatPercent(warning)} eşiğini geçince uyarı rengiyle yazılır.` },
    { key: 'cancelled', label: 'İptal Edilen', value: valueWithUnit(now.cancelledOrders, 'sipariş', formatCount), sub: `${formatNumber(data.cancellations.cancelledQuantity)} adet · ${formatTl(cancelled ?? 0)}`, detail: 'cancellations',
      note: 'İptal edilen sipariş, adet ve liste fiyatıyla tutar; satışa girmez.' }
  ]
}

// The share of late packages above which Geciken Kargo is drawn in the warning colour (Amazon's late shipment rate
// target is under 4 %; marketplace-docs has no Trendyol figure).
const lateWarningRate = 0.04

// Operasyon: how fast and how late the packages left, how long they took to arrive, and what waits to be invoiced or
// The report's period as the invoice figures name it ("son 30 günde"), so they are not read as every open package.
function invoicePeriod(data: ReportSales) { return `${formatCount(data.stock.periodDays)} günde` }

// restocked; each row opens its tab.
function operationMetrics(data: ReportSales): Metric[] {
  const ship = weightedHours(data.cargo, row => row.averageHoursToShip, row => row.measuredShip)
  const deliver = weightedHours(data.cargo, row => row.averageHoursToDeliver, row => row.measuredDeliver)
  const late = data.cargo.reduce((sum, row) => sum + row.lateShipments, 0)
  const measuredLate = data.cargo.reduce((sum, row) => sum + row.measuredLate, 0)
  const low = data.stock.bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const packages = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const notInvoiced = data.invoices.buckets.find(bucket => bucket.key === 'NOT_INVOICED')?.packages ?? 0
  const questions = data.questionTimes ?? null
  return [
    { key: 'ship', label: 'Kargoya Verme Süresi', value: formatHours(ship.hours), detail: 'cargo', note: `Siparişin kargoya verilmesine kadar geçen ortalama süre.${ship.measured > 0 ? ` ${formatCount(ship.measured)} pakette ölçüldü.` : ''}` },
    { key: 'late', label: 'Geciken Kargo', value: measuredLate > 0 ? formatPercent(late / measuredLate) : '—', sub: measuredLate > 0 ? `${formatCount(late)} paket` : undefined,
      tone: measuredLate > 0 && late / measuredLate > lateWarningRate ? 'warn' : undefined, detail: 'cargo',
      note: `Süresinde kargoya verilmeyen paketlerin oranı; PttAVM ölçülmez. ${measuredLate > 0 ? `${formatPercent(lateWarningRate)} eşiğini geçince uyarı rengiyle yazılır.` : 'Bu dönemde süresi bilinen paket yok.'}` },
    { key: 'deliver', label: 'Teslim Süresi', value: formatHours(deliver.hours), detail: 'cargo',
      note: `Kargoya verilen paketin müşteriye ulaşmasına kadar geçen ortalama süre.${deliver.hours === null ? ' Bu dönemde teslim anı okunan paket yok.' : ` ${formatCount(deliver.measured)} pakette ölçüldü.`}` },
    // The period's packages only (its orders): Faturalar and the overview count every open package, so the period is named.
    { key: 'invoice', label: 'Faturasız Paket', value: formatCount(notInvoiced), sub: `${invoicePeriod(data)} ${formatCount(packages)} paket içinde`, tone: notInvoiced > 0 ? 'warn' : undefined, detail: 'invoices',
      note: `Seçilen dönemin (${invoicePeriod(data)}) siparişlerinde faturası henüz kesilmemiş paketler; bir tane bile varsa uyarı rengiyle yazılır.\nFaturalar sayfası ve Genel Bakış dönemden bağımsız bütün açık paketleri sayar, bu yüzden sayıları farklı olabilir.` },
    { key: 'stock', label: 'Stok Riski', value: valueWithUnit(low, 'ürün', formatCount), tone: low > 0 ? 'warn' : undefined, detail: 'stock',
      note: 'Stoğu, dönemin satış hızıyla bir haftadan az yetecek çok satan ürünler.' },
    // Trendyol and Hepsiburada only: N11's and Pazarama's answer times are not reliable (marketplace-docs).
    { key: 'questions', label: 'Soru Yanıt Süresi', value: questions?.averageHours == null ? '—' : formatHours(questions.averageHours),
      note: `Müşteri sorusunun ilk yanıtına kadar geçen ortalama süre; yalnız Trendyol ve Hepsiburada soruları sayılır.${!questions ? ' Bu seçimde ikisi de yok.'
        : questions.averageHours === null ? ' Bu dönemde yanıtlanan soru yok.' : ` ${formatCount(questions.answered)} yanıtta ölçüldü.`}${questions?.waiting ? ` ${formatCount(questions.waiting)} soru yanıt bekliyor.` : ''}` }
  ]
}


// The overview's figure cards (owner's pick 7 Nov 2026, "Başlıkta Rakam"): nine cards, each with its large figure at the
// top (the name and the change or the threshold on the first line, the figure and a short note on the second, a bar with
// its threshold under them where the figure is a rate) and, under a line, the figures beside it as rows of one height:
// the name on the left, never wrapping, the value on the right. No boxes inside the card. The figures and their
// explanations are the metric rows' (productMetrics … operationMetrics), so nothing is worked out twice; a row opens its
// tab where it has one.
type HeroRow = { key: string; label: string; value: string; note: string; tone?: 'warn' | 'bad' | 'good'; detail?: DetailKey; onOpen?: () => void
  // A text (a reason) rather than a figure: it gives way with an ellipsis, the full text in the explanation.
  text?: boolean }
// value and threshold on a scale from 0 to `scale` (a rate); tone colours the filled part.
type HeroBar = { value: number; scale: number; threshold?: number; tone: 'warn' | 'good' | 'plain' }
type HeroCardDef = {
  key: string; title: string; value: string; aside?: string; note: string; tone?: 'warn' | 'bad' | 'good'
  // The name of the large figure, on its explanation (the card's title names the area, not the figure).
  tipTitle: string
  // On the title's line: the change against the previous period, or a threshold or share note.
  delta?: Change | null; invert?: boolean; previous?: string; badge?: string; badgeTone?: 'warn'
  bar?: HeroBar; rows: HeroRow[]; alert?: boolean
}

// A figure's length for its font size: the CSS shrinks a long figure (₺1.638.267) to fit its box, never cutting it.
const fitChars = (value: string) => ({ '--chars': value.length }) as CSSProperties

function HeroCard({ card, onMore }: { card: HeroCardDef; onMore: OpenDetail }) {
  const noSource = useContext(MetricSourceContext) === false
  const tip = useId()
  const [ref, shown] = useReveal<HTMLElement>()
  const bar = card.bar
  const fill = bar ? Math.max(0, Math.min(1, bar.value / bar.scale)) : 0
  return <section ref={ref} className={`reports-card reports-hero-card is-${card.key}${card.alert ? ' is-alert' : ''}${shown ? ' is-shown' : ''}`} aria-label={card.title}>
    <div className="reports-hero-top reports-tip-host">
      <div className="reports-hero-head">
        <h2 className="reports-hero-title"><i aria-hidden="true" />{card.title}</h2>
        {card.delta !== undefined ? <Delta delta={card.delta} invert={card.invert} previous={card.previous} />
          : card.badge && <span className={`reports-hero-badge${card.badgeTone ? ` is-${card.badgeTone}` : ''}`}>{card.badge}</span>}
      </div>
      <div className="reports-hero">
        <button type="button" className="reports-tip-trigger reports-hero-value" aria-describedby={`${tip}-hero`}>
          <strong className={card.tone ? `is-${card.tone}` : undefined} style={fitChars(noSource ? noValue : card.value)}>{noSource ? noValue : card.value}</strong>
        </button>
        {card.aside && <span className="reports-hero-aside">{card.aside}</span>}
      </div>
      {bar ? <span className={`reports-hero-bar is-${bar.tone}`} aria-hidden="true">
        <i style={{ width: shown ? `${fill * 100}%` : '0%' }} />
        {bar.threshold !== undefined && <b style={{ left: `${Math.min(1, bar.threshold / bar.scale) * 100}%` }} />}
      </span> : <span className="reports-hero-bar is-empty" aria-hidden="true" />}
      <span role="tooltip" id={`${tip}-hero`} className="reports-hover-tip"><TipText title={card.tipTitle} text={card.note} /></span>
    </div>
    <ul className="reports-hero-rows">{card.rows.map(row => {
      const open = row.onOpen ?? (row.detail ? () => onMore(row.detail!) : undefined)
      return <li key={row.key} className="reports-tip-host">
        <button type="button" className={`reports-hero-row${open ? '' : ' is-static'}`} onClick={open} aria-describedby={`${tip}-${row.key}`} aria-haspopup={row.onOpen ? 'dialog' : undefined}>
          <span className="reports-hero-row-label">{row.label}</span>
          <strong className={[row.tone ? `is-${row.tone}` : '', row.text ? 'is-text' : ''].filter(Boolean).join(' ') || undefined}>{noSource ? noValue : row.value}</strong>
        </button>
        <span role="tooltip" id={`${tip}-${row.key}`} className="reports-hover-tip"><TipText title={row.label} text={row.note} /></span>
      </li>
    })}</ul>
  </section>
}

// A money figure of a row in whole liras from a thousand up ("₺409.298"); a smaller one keeps its kuruş.
const tileTl = (value: string) => /₺[\d.]{5,},\d{2}/u.test(value) ? wholeLiras(value) : value

// The healthy ceiling of TACoS the bar marks (Helium 10's rule of thumb, rapor-metrikleri §3).
const tacosLine = 0.05
// A threshold as it is said: %5, %4, %2 without a decimal; %2,5 with one.
const limitText = (rate: number) => formatPercent(rate, Number.isInteger(Math.round(rate * 1000) / 10) ? 0 : 1)

function overviewCards(data: ReportSales, money: Money | null, ads: AdReports | undefined, adsFailed: boolean, openAds: () => void): HeroCardDef[] {
  const now = data.summary
  const before = data.previousSummary
  const metric = (rows: Metric[], key: string) => rows.find(item => item.key === key)!
  const line = (rows: Metric[], key: string, label: string, value?: string): HeroRow => {
    const item = metric(rows, key)
    return { key, label, value: tileTl(value ?? item.value), note: item.note, tone: item.tone, detail: item.detail, onOpen: item.onOpen }
  }
  const product = productMetrics(data, money)
  const order = orderMetrics(data, money)
  const cost = adMetrics(data, money)
  const back = returnMetrics(data)
  const ops = operationMetrics(data)

  // Satış: the seller's discount as a share of the list price.
  const list = now.listAmount ?? 0
  const discount = now.discountAmount ?? null
  const discountShare = now.ordersWithAmount > 0 && discount !== null && list > 0 ? formatPercent(discount / list) : '—'

  // Kârlılık: the margin before the advertising, its bar from 0 to 100 %.
  const spend = adSpend(data)
  const profit = money?.profit ?? null
  const beforeAds = spend !== null && profit !== null && now.netRevenue > 0 ? (profit + spend) / now.netRevenue : null
  const unitCost = data.cost.costedQuantity > 0 ? data.cost.costTotal / data.cost.costedQuantity : null

  // Reklam: the statement's spend with its share of Trendyol's revenue against the 5 % line, and the uploaded report.
  const trendyol = data.platforms.find(item => item.key.toUpperCase() === 'TRENDYOL' && item.ordersWithAmount > 0)
  const tacos = spend !== null && trendyol && trendyol.netRevenue > 0 ? spend / trendyol.netRevenue : null
  const adTotals = combined(ads?.reports ?? [])
  const adOld = (ads?.reports ?? []).some(report => reportAge(report) > staleDays)
  const adRow = adReportRow(ads, adsFailed)
  const adRows: HeroRow[] = adTotals
    ? [{ key: 'roas', label: 'ROAS (kampanya)', value: adTotals.roas === null ? '—' : roasText(adTotals.roas), note: adRow.note, tone: adOld ? 'warn' : undefined, onOpen: openAds },
      { key: 'adRevenue', label: 'Reklam Cirosu (Kampanya)', value: formatTlWhole(adTotals.revenue), note: adRow.note, tone: adOld ? 'warn' : undefined, onOpen: openAds }]
    : [{ key: 'adReport', label: 'Reklam Raporu', value: adRow.value, note: adRow.note, onOpen: openAds }]

  // Kargo: the late share against its 4 % line, the bar to 10 %.
  const late = data.cargo.reduce((sum, item) => sum + item.lateShipments, 0)
  const measuredLate = data.cargo.reduce((sum, item) => sum + item.measuredLate, 0)
  const lateRate = measuredLate > 0 ? late / measuredLate : null

  const warning = data.cancellations.warningRate
  const topCancel = [...data.cancellations.reasons].filter(reason => reason.known && reason.count > 0).sort((a, b) => b.count - a.count)[0]
  const cancelTotal = data.cancellations.reasons.reduce((sum, reason) => sum + reason.count, 0)
  const topReturn = [...data.returnReasons].filter(reason => reason.known && reason.count > 0).sort((a, b) => b.count - a.count)[0]
  const returnTotal = data.returnReasons.reduce((sum, reason) => sum + reason.count, 0)
  const cancelled = data.cancellations.cancelledAmount
  const notInvoiced = data.invoices.buckets.find(bucket => bucket.key === 'NOT_INVOICED')?.packages ?? 0
  const packages = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const over = (value: number | null, limit: number) => value !== null && value > limit
  // The reason given most as a row: the reason and its share, cut with an ellipsis when long, whole in the explanation.
  const reasonRow = (key: string, reason: ReportReason | undefined, total: number, what: string, detail: DetailKey): HeroRow[] => reason
    ? [{ key, label: 'En Sık Neden', value: `${reason.reason} (${formatPercent(reason.count / total)})`, text: true, detail,
      note: `${what} en sık verilen neden; nedeni bilinen kayıtların ${formatPercent(reason.count / total)} kadarı.\n${reason.reason}` }]
    : []

  return [
    // Satış: the units sold (cancellations out, returns not taken off) with the variants behind them.
    { key: 'sales', title: 'Satış', tipTitle: 'Satılan Adet', value: valueWithUnit(now.soldQuantity, 'adet', formatNumber),
      aside: now.soldVariants === undefined ? 'satılan adet' : `${formatCount(now.soldVariants)} farklı varyant`,
      delta: change(now.soldQuantity, before.soldQuantity), previous: formatNumber(before.soldQuantity),
      note: `Dönemde satılan ürün adedi ve satılan farklı varyant sayısı; iptaller sayılmaz, iadeler düşülmez.\n${previousLine(formatNumber(before.soldQuantity))}`,
      rows: [{ key: 'kept', label: 'Net Satılan Adet', value: formatNumber(now.keptQuantity),
        note: 'Müşteride kalan ürün adedi: satılan adetten iadeler düşülür.' },
        line(product, 'price', 'Adet Başına Satış'), line(product, 'vatFree', 'KDV Hariç Net Satış'), line(product, 'discount', 'Satıcı İndirimi', discountShare)] },
    { key: 'orders', title: 'Sipariş ve Müşteri', tipTitle: 'Sepet Ortalaması', value: metric(order, 'basket').value, aside: 'sepet ortalaması', note: metric(order, 'basket').note,
      // Sipariş Başına Kâr is Kârlılık's row only: one figure, one place.
      rows: [line(order, 'units', 'Sipariş Başına Adet'), line(order, 'repeat', 'Tekrar Gelen Müşteri'),
        { key: 'perCustomer', label: 'Müşteri Başına Sipariş', value: data.customers.ordersPerCustomer === null ? '—' : formatNumber(data.customers.ordersPerCustomer),
          note: 'Müşterisi tanınan siparişlerin müşteri başına ortalaması.' }] },
    { key: 'profit', title: 'Kârlılık', tipTitle: 'Reklam Öncesi Marj', value: metric(cost, 'beforeAds').value, aside: 'reklam öncesi marj', note: metric(cost, 'beforeAds').note,
      tone: beforeAds === null ? undefined : beforeAds < 0 ? 'bad' : 'good', bar: beforeAds === null ? undefined : { value: Math.max(0, beforeAds), scale: 1, tone: 'good' },
      rows: [line(product, 'profit', 'Adet Başına Kâr'), line(order, 'profit', 'Sipariş Başına Kâr'),
        { key: 'unitCost', label: 'Adet Başına Maliyet', value: unitCost === null ? '—' : formatTl(unitCost),
          note: 'Maliyeti girili ürünlerin adet başına ortalama alış maliyeti.' }] },
    { key: 'fees', title: 'Pazaryeri Kesintileri', tipTitle: 'Sipariş Başına Kesinti', value: metric(order, 'fees').value, aside: 'sipariş başına', note: metric(order, 'fees').note,
      rows: [line(cost, 'commission', 'Komisyon Oranı'), line(order, 'cargo', 'Sipariş Başına Kargo'), line(order, 'unitCargo', 'Adet Başına Kargo')] },
    { key: 'ads', title: 'Reklam · Trendyol', tipTitle: 'Trendyol Reklamı', value: spend === null ? '—' : formatTlWhole(spend),
      aside: tacos === null ? 'reklam harcaması' : `sınır ${limitText(tacosLine)}`,
      badge: tacos === null ? undefined : `payı ${formatPercent(tacos)}`, badgeTone: over(tacos, tacosLine) ? 'warn' : undefined,
      note: `${metric(cost, 'spend').note}\nPayı, Trendyol net cirosuna oranıdır; ${formatPercent(tacosLine)} sınırını geçince uyarı rengiyle yazılır.`,
      tone: over(tacos, tacosLine) ? 'warn' : undefined, bar: tacos === null ? undefined : { value: tacos, scale: 0.1, threshold: tacosLine, tone: over(tacos, tacosLine) ? 'warn' : 'plain' },
      rows: [line(cost, 'perOrder', 'Sipariş Başına Reklam'), ...adRows] },
    { key: 'cargo', title: 'Kargo ve Teslimat', tipTitle: 'Geciken Kargo', value: metric(ops, 'late').value, aside: lateRate === null ? 'ölçülemedi' : `${formatCount(late)} paket gecikti`,
      badge: `eşik ${limitText(lateWarningRate)}`, badgeTone: over(lateRate, lateWarningRate) ? 'warn' : undefined,
      note: metric(ops, 'late').note, tone: metric(ops, 'late').tone,
      bar: lateRate === null ? undefined : { value: lateRate, scale: 0.1, threshold: lateWarningRate, tone: over(lateRate, lateWarningRate) ? 'warn' : 'plain' },
      rows: [line(ops, 'ship', 'Kargoya Verme Süresi'), line(ops, 'deliver', 'Teslim Süresi'),
        { key: 'measured', label: 'Ölçülen Paket', value: formatCount(measuredLate), detail: 'cargo', note: 'Kargoya verme süresi ve son günü bilinen paketler; Geciken Kargo oranı bunlar üzerinden.' }] },
    { key: 'returns', title: 'İade', tipTitle: 'İade Oranı', value: formatPercent(now.returnRate), aside: `önceki dönem ${formatPercent(before.returnRate)}`, note: metric(back, 'rate').note,
      delta: metric(back, 'rate').delta, invert: true, previous: formatPercent(before.returnRate),
      rows: [line(back, 'returned', 'İade Edilen Adet', formatNumber(now.returnedQuantity)),
        { key: 'claims', label: 'İade Talebi', value: formatCount(now.returnClaims), detail: 'returns', note: 'Müşterilerin açtığı iade talepleri; reddedilen ve iptal edilenler sayılmaz.' },
        // Exchange requests on their own row, for information (owner, 18 Nov 2026).
        ...((now.exchangeClaims ?? 0) > 0 ? [{ key: 'exchanges', label: 'Değişim Talebi', value: formatCount(now.exchangeClaims), detail: 'returns' as const, note: 'Ürün değişimi istenen talepler, bilgi için. Hepsiburada değişimi iade sayılmaz; Trendyol talebi kabul edildiyse iade olarak sayılmaya devam eder.' }] : []),
        { key: 'amount', label: 'İade Edilen Tutar', value: formatTlWhole(now.returnedAmount), detail: 'returns', note: 'İade edilen ürünler için müşterinin ödediği tutar; Kâr Köprüsü\'nde satıştan düşülür.' },
        line(back, 'returnCargo', 'İade Kargo Bedeli'), line(back, 'operation', 'Yurt Dışı Operasyon İade Bedeli'), line(back, 'returnCost', 'Toplam İade Maliyeti'),
        ...reasonRow('returnReason', topReturn, returnTotal, 'İade taleplerinde', 'returns')] },
    { key: 'cancels', title: 'İptal', tipTitle: 'İptal Oranı', value: metric(back, 'cancel').value, aside: `${formatCount(now.cancelledOrders)} sipariş iptal`, note: metric(back, 'cancel').note, tone: metric(back, 'cancel').tone,
      badge: `eşik ${limitText(warning)}`, badgeTone: (now.cancelRate ?? 0) > warning ? 'warn' : undefined,
      bar: { value: now.cancelRate ?? 0, scale: 0.1, threshold: warning, tone: (now.cancelRate ?? 0) > warning ? 'warn' : 'plain' },
      rows: [{ key: 'cancelUnits', label: 'İptal Edilen Adet', value: formatNumber(data.cancellations.cancelledQuantity), detail: 'cancellations', note: 'İptal edilen ürün adedi; kısmi iptaller dahil.' },
        { key: 'cancelAmount', label: 'İptal Edilen Tutar', value: cancelled === undefined ? '—' : formatTlWhole(cancelled), detail: 'cancellations', note: 'İptal edilen ürünlerin liste fiyatıyla tutarı; satışa girmez.' },
        ...reasonRow('cancelReason', topCancel, cancelTotal, 'İptallerde', 'cancellations')] },
    { key: 'todo', title: 'Yapılacaklar', tipTitle: 'Faturasız Paket', value: metric(ops, 'invoice').value, aside: `${invoicePeriod(data)} faturasız paket`, badge: `${formatCount(packages)} paket içinde`, note: metric(ops, 'invoice').note,
      tone: metric(ops, 'invoice').tone, alert: notInvoiced > 0,
      rows: [line(ops, 'stock', 'Stok Riski'), line(ops, 'questions', 'Soru Yanıt Süresi')] }
  ]
}
