import { TabSlidePanel } from '../../../shared/tab-slide'
import { change, formatCount, formatNumber, formatPercent, formatTl, type ReportGroup, type ReportSales } from '../report-format'
import { SalesMap } from '../SalesMap'
import { type OpsSub, SubTabs, Panel, Bars, moneyBreakdown, profitReason, missingCostTip, profitLabel, estimateLine, signedTl, type Kpi, KpiStrip, previousText, previousRate, percent, previousMoney, netProfitTip, netLossTip, netSalesTip, returnRateTip, UnlinkedDeductions, profitSteps, topGroups } from './report-parts'
import { Platforms, Cities, Movers, Products, BrandsAndCategories, Returns, Cancellations, Cargo, TimeOfDay, Customers, CostAndProfit, Invoices } from './SalesTables'

type ProductSub = 'products' | 'brands'

const opsSubs: OpsSub[] = ['returns', 'cancellations', 'cargo', 'invoices']

// Coğrafya: the sales map with its breakdown (its own figures over the map), the provinces ranked by sales and by
// returns, and the full city table.
export function Geography({ data }: { data: ReportSales }) {
  return <div className="reports-summary">
    <SalesMap data={data} />
    <Cities data={data} />
  </div>
}

// Ürünler: the best sellers, the most returned, brands and categories as bars, then their tables under two sub-tabs.
export function ProductsView({ data, sub, onSub }: { data: ReportSales; sub: ProductSub; onSub: (sub: ProductSub) => void }) {
  const now = data.summary
  const then = data.previousSummary
  const unit = (summary: ReportSales['summary']) => summary.ordersWithAmount > 0 && summary.soldQuantity > 0 ? summary.grossSales / summary.soldQuantity : null
  const selling = (groups: ReportGroup[]) => groups.filter(group => group.known && group.soldQuantity > 0).length
  const items: Kpi[] = [
    { key: 'sold', label: 'Satılan Adet', value: now.soldQuantity, delta: change(now.soldQuantity, then.soldQuantity), note: previousText(formatNumber(then.soldQuantity)), tip: 'Dönemde satılan ürün adedi; iptaller sayılmaz, iadeler düşülmez.' },
    { key: 'returned', label: 'İade Edilen Adet', value: now.returnedQuantity, delta: change(now.returnedQuantity, then.returnedQuantity), invert: true,
      note: previousText(formatNumber(then.returnedQuantity)) },
    { key: 'rate', label: 'İade Oranı', value: now.returnRate, format: percent, delta: change(now.returnRate, then.returnRate, 'rate'), invert: true,
      note: previousText(formatPercent(then.returnRate), '%0'), tip: returnRateTip },
    { key: 'price', label: 'Adet Başına Satış', value: unit(now), format: formatTl, delta: change(unit(now), unit(then)), note: previousText(formatTl(unit(then)), '₺0'), tip: 'Bir ürünün ortalama satış fiyatı; iadeler düşülmeden.' },
    { key: 'brands', label: 'Satan Marka', value: selling(data.brands), format: formatCount, note: `${formatCount(selling(data.categories))} kategoride`, tip: 'Dönemde satışı olan marka sayısı; markası girili ürünlerden sayılır.' }
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
      <Panel title="Satışta Yükselen Ürünler" description="Önceki döneme göre satılan adedi en çok artanlar." className="reports-list">
        <Movers items={data.rising ?? []} tone="up" empty="Önceki döneme göre satışı artan ürün yok." />
      </Panel>
      <Panel title="Satışta Düşen Ürünler" description="Önceki döneme göre satılan adedi en çok azalanlar." className="reports-list">
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
    {/* These sub-tabs sit under the lists, so a change keeps the scroll where they are. */}
    <TabSlidePanel active={sub} order={['products', 'brands']} scrollToTop={false} role="tabpanel">{sub === 'brands' ? <BrandsAndCategories data={data} /> : <Products data={data} />}</TabSlidePanel>
  </div>
}

// Kârlılık: the period's revenue, the part of it with a product cost, the net profit and the net
// profit over the sales and over the product cost, each against the previous period; then the profit account, Trendyol's
// real costs, the deductions tied to no order and the platforms. Every figure is the profit account's (moneyBreakdown),
// so the strip agrees with Kâr Hesabı under it and with Genel Bakış; the commission and the coverages are in the panels.
export function ProfitView({ data }: { data: ReportSales }) {
  const now = data.summary
  const then = data.previousSummary
  const cost = data.cost
  const earlierCost = data.previousAccount?.cost ?? null
  const money = moneyBreakdown(data, profitSteps(data))
  const earlier = previousMoney(data)
  const noAmount = now.ordersWithAmount === 0
  const costed = cost.costedQuantity > 0
  const earlierCosted = (earlierCost?.costedQuantity ?? 0) > 0
  const profit = money?.profit ?? null
  const earlierProfit = earlier?.profit ?? null
  const onCost = profit !== null && money?.cost ? profit / money.cost : null
  const earlierOnCost = earlierProfit !== null && earlier?.cost ? earlierProfit / earlier.cost : null
  const noCost = noAmount ? null : 'Maliyet girilmemiş'
  // Maliyeti Olan Ciro counts like Net Ciro: the kept units' sales, returns taken off (owner 14 Nov 2026).
  const costedSales = cost.costedRevenue
  const earlierCostedSales = earlierCost ? earlierCost.costedRevenue : null
  const tone = (value: number | null) => value === null ? undefined : value < 0 ? 'bad' as const : value > 0 ? 'good' as const : undefined
  const items: Kpi[] = [
    { key: 'revenue', label: 'Net Ciro', value: noAmount ? null : now.netRevenue, format: formatTl, delta: noAmount ? null : change(now.netRevenue, then.netRevenue),
      note: previousText(then.ordersWithAmount > 0 ? formatTl(then.netRevenue) : null, '₺0'),
      tip: `${netSalesTip} Genel Bakış'taki Net Ciro ile aynı.` },
    { key: 'costed', label: 'Maliyeti Olan Ciro', value: costed ? costedSales : null, format: formatTl,
      delta: costed && earlierCosted ? change(costedSales, earlierCostedSales) : null, note: !costed && noCost ? noCost : previousText(earlierCosted ? formatTl(earlierCostedSales) : null, '₺0'),
      tip: `Ürün maliyeti girili satışların tutarı; iadeler düşülmüş. Net Ciro'nun ${formatPercent(now.netRevenue > 0 ? costedSales / now.netRevenue : null)} kadarı.` },
    { key: 'profit', label: profitLabel(money), value: profit, format: formatTl,
      delta: change(profit, earlierProfit), note: profit === null && profitReason(money, noAmount) ? profitReason(money, noAmount)! : previousText(earlierProfit === null ? null : signedTl(earlierProfit), '₺0'), tone: tone(profit),
      tip: `${profit === null ? missingCostTip(money) : profit < 0 ? netLossTip : netProfitTip}${estimateLine(money)}` },
    { key: 'margin', label: 'Net Kâr / Satış Fiyatı Oranı', value: money?.margin ?? null, format: percent, delta: change(money?.margin ?? null, earlier?.margin ?? null, 'rate'),
      note: previousRate(earlier?.margin, then.ordersWithAmount > 0), tone: (money?.margin ?? 0) < 0 ? 'bad' : undefined,
      tip: 'Net kârın Net Ciro\'ya oranı; Genel Bakış\'taki Kâr Marjı ile aynı.' },
    { key: 'onCost', label: 'Net Kâr / Ürün Maliyeti Oranı', value: onCost, format: percent, delta: change(onCost, earlierOnCost, 'rate'),
      note: previousRate(earlierOnCost, then.ordersWithAmount > 0), tone: (onCost ?? 0) < 0 ? 'bad' : undefined,
      tip: 'Net kârın ürün maliyetine oranı; Genel Bakış\'taki Maliyete Göre Kâr ile aynı.' }
  ]
  return <div className="reports-summary">
    <KpiStrip label="Kârlılığın göstergeleri" items={items} long />
    <CostAndProfit data={data} />
    <UnlinkedDeductions data={data} panel />
    <Platforms data={data} />
  </div>
}

// Operasyon: returns, cancellations, cargo and invoices as sub-tabs (the stock is the Stok tab's), each opening with its breakdown as bars.
export function OperationsView({ data, sub, onSub }: { data: ReportSales; sub: OpsSub; onSub: (sub: OpsSub) => void }) {
  const now = data.summary
  const packages = data.cargo.reduce((sum, row) => sum + row.packages, 0)
  const invoiced = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const cancelWarn = (now.cancelRate ?? 0) > data.cancellations.warningRate
  return <div className="reports-summary">
    <SubTabs label="Operasyon kırılımı" value={sub} onChange={onSub} items={[
      { value: 'returns', label: 'İadeler', icon: 'returns', count: formatCount(now.returnClaims) },
      { value: 'cancellations', label: 'İptaller', icon: 'close', count: formatCount(now.cancelledOrders), warn: cancelWarn },
      { value: 'cargo', label: 'Kargo', icon: 'truck', count: formatCount(packages) },
      { value: 'invoices', label: 'Fatura', icon: 'invoice', count: formatCount(invoiced) }
    ]} />
    <TabSlidePanel active={sub} order={opsSubs} role="tabpanel" className="reports-summary">
      {sub === 'returns' && <Returns data={data} />}
      {sub === 'cancellations' && <Cancellations data={data} />}
      {sub === 'cargo' && <Cargo data={data} />}
      {sub === 'invoices' && <Invoices data={data} />}
    </TabSlidePanel>
  </div>
}

// Müşteriler: who buys (unique and repeat customers) and when (weekday and hour).
export function CustomersView({ data }: { data: ReportSales }) {
  return <div className="reports-summary">
    <Customers data={data} />
    <TimeOfDay data={data} />
  </div>
}
