import { useId, useState } from 'react'
import { EmptyState, UiIcon } from '../../../shared/components'
import { change, downloadCsv, variantColourAndSize, formatCount, formatHours, formatNumber, formatPercent, formatTl, moneyOrMissing, platformName, toCsv, unknownLabel, type CsvColumn, type ReportCosts, type ReportGroup, type ReportProduct, type ReportReason, type ReportSales, type ReportStockRow } from '../report-format'
import { Columns, StackedBar, useReveal, type ColumnPoint, type Part } from '../report-visuals'
import { thumbnailUrl } from '../../../shared/product-thumbnail'
import { Delta, Panel, RevealPanel, type Column, DataTable, Bars, TipMark, TipText, moneyBreakdown, type Kpi, KpiStrip, previousText, percent, unlinkedLabels, returnRateTip, financeCosts, trendyolRead, profitSteps, weightedHours, type BarItem, topGroups, NoAmountNote, TotalRow, weekdays } from './report-parts'
import { EstimateGaps, ProfitLedger } from './ProfitBridge'

// İade ve İptal: the return rate, what came back (units, claims and the amount paid for them), then the cancel rate
// against its warning line and what was cancelled.
// Returns at their list price, as Trendyol's sales report shows them; what was paid when an older server sends no list.
export const returnedAtList = (summary: { returnedAmount: number; returnedListAmount?: number }) => summary.returnedListAmount ?? summary.returnedAmount

function topReasons(reasons: ReportReason[], limit = 6): BarItem[] {
  const known = reasons.filter(reason => reason.known).sort((a, b) => b.count - a.count).slice(0, limit)
  return [...known, ...reasons.filter(reason => !reason.known)].map(reason => ({ key: reason.reason, label: reason.reason, value: reason.count, muted: !reason.known }))
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
export function Platforms({ data }: { data: ReportSales }) {
  const columns = groupColumns(labelColumn('Platform', row => platformName(row.label)), { commission: true, share: data.summary.netRevenue })
  return <div className="reports-grid">
    <div className="is-wide"><StatementLines data={data} /></div>
    <div className="is-wide"><DataTable title="Platform Özeti" csvName="rapor-platformlar" rows={data.platforms} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold"
      columns={columns} footer={data.platforms.length > 1 && <TotalRow rows={data.platforms} columns={columns} expand />}
      expand={row => <DataTable title={`${platformName(row.label)} Mağazaları`} csvName={`rapor-${row.key.toLowerCase()}-magazalar`} rows={row.children} rowKey={child => child.key} initialSort="sold"
        columns={groupColumns(labelColumn('Mağaza'), { commission: true })} />} /></div>
  </div>
}

export function Cities({ data }: { data: ReportSales }) {
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
        tools={<label className="reports-search"><UiIcon name="search" size={16} /><input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="İl ara" aria-label="İl ara" /></label>} description={`Toplam ${formatNumber(total)} adet; ili okunamayan siparişler Bilinmiyor satırında. Satırı açınca ilçeler ve platformlar görünür.`}
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
export function Movers({ items, tone, empty }: { items: ReportProduct[]; tone: 'up' | 'down'; empty: string }) {
  const [ref, shown] = useReveal<HTMLUListElement>()
  if (!items.length) return <EmptyState icon="chart" title="Veri Yok">{empty}</EmptyState>
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

export function Products({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><DataTable title="En Çok Satan 20 Ürün" description="Varyant bazında; satış tutarı, sipariş tutarının satıra düşen payıdır." csvName="rapor-en-cok-satanlar" rows={data.topProducts} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={productColumns()} /></div>
    <div className="is-wide"><DataTable title="En Çok İade Edilen 20 Ürün" csvName="rapor-en-cok-iade" rows={data.topReturned} rowKey={row => row.variantId ?? row.sku} initialSort="returned" columns={productColumns()} empty="Bu dönemde iade yok." /></div>
  </div>
}

export function BrandsAndCategories({ data }: { data: ReportSales }) {
  return <div className="reports-grid">
    <div className="is-wide"><DataTable title="Markalar" description="Ürün kartındaki markaya göre; eşleşmeyen satırlar Bilinmiyor." csvName="rapor-markalar" rows={data.brands} rowKey={row => row.key} unknown={row => !row.known} initialSort="sold" columns={groupColumns(labelColumn('Marka'), { commission: true })} /></div>
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
    {total > 0 ? <Bars label={title} format={formatCount} items={topReasons(reasons, 8).map(item => ({ ...item, note: formatPercent(item.value / total) }))} /> : <EmptyState icon="chart" title="Veri Yok">{empty}</EmptyState>}
  </Panel>
}

// İadeler: four figures against the previous period, the reasons beside the provinces they came from, then every
// province's returns.
export function Returns({ data }: { data: ReportSales }) {
  const byCity = data.cities.filter(city => city.returnedQuantity > 0 || city.returnClaims > 0)
  const now = data.summary
  const then = data.previousSummary
  const items: Kpi[] = [
    { key: 'claims', label: 'İade Talebi', value: now.returnClaims, format: formatCount, delta: change(now.returnClaims, then.returnClaims), invert: true,
      note: previousText(formatCount(then.returnClaims)), tip: 'Müşterilerin açtığı iade talepleri; reddedilen ve iptal edilenler sayılmaz.' },
    { key: 'units', label: 'İade Adedi', value: now.returnedQuantity, delta: change(now.returnedQuantity, then.returnedQuantity), invert: true,
      note: previousText(formatNumber(then.returnedQuantity)), tip: 'Müşterilerin geri gönderdiği ürün adedi.' },
    { key: 'rate', label: 'İade Oranı', value: now.returnRate, format: percent, delta: change(now.returnRate, then.returnRate, 'rate'), invert: true,
      note: previousText(formatPercent(then.returnRate), '%0'), tip: returnRateTip },
    { key: 'amount', label: 'İade Liste Tutarı', value: now.ordersWithAmount > 0 ? returnedAtList(now) : null, format: formatTl,
      delta: now.ordersWithAmount > 0 ? change(returnedAtList(now), returnedAtList(then)) : null, invert: true,
      note: previousText(then.ordersWithAmount > 0 ? formatTl(returnedAtList(then)) : null, '₺0'), tip: 'İade edilen ürünlerin liste fiyatıyla tutarı, Trendyol gibi. Kâr hesabında müşterinin ödediği tutar düşülür.' }
  ]
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="İadelerin göstergeleri" items={items} long /></div>
    <ReasonsCard title="İade Nedenleri" description="Pazaryerinin bildirdiği nedene göre; nedeni gelmeyenler Bilinmiyor." reasons={data.returnReasons} csvName="rapor-iade-nedenleri" empty="Bu dönemde iade talebi yok." />
    <Panel title="İade Gelen İller" description="İade adedine göre; yanında iade oranı." className="reports-list">
      <Bars label="İllere göre iade" format={formatNumber} items={topGroups(data.cities, group => group.returnedQuantity, group => formatPercent(group.returnRate))} />
    </Panel>
    <div className="is-wide"><DataTable title="İllere Göre İadeler" csvName="rapor-iade-sehirler" rows={byCity} rowKey={row => row.key} unknown={row => !row.known} initialSort="returned" columns={groupColumns(labelColumn('İl'))} empty="Bu dönemde iade yok." /></div>
  </div>
}

export function Cancellations({ data }: { data: ReportSales }) {
  const cancellations = data.cancellations
  const then = data.previousSummary
  const warn = (rate: number | null) => (rate ?? 0) > cancellations.warningRate
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="İptallerin göstergeleri" long items={[
      { key: 'orders', label: 'İptal Edilen Sipariş', value: cancellations.cancelledOrders, format: formatCount, delta: change(cancellations.cancelledOrders, then.cancelledOrders), invert: true,
        note: previousText(formatCount(then.cancelledOrders)), tip: 'Tamamı iptal edilen sipariş sayısı.' },
      { key: 'rate', label: 'İptal Oranı', value: cancellations.cancelRate, format: percent, delta: change(cancellations.cancelRate, then.cancelRate, 'rate'), invert: true,
        note: previousText(formatPercent(then.cancelRate), '%0'), tip: `Sipariş edilen ürün adedinin yüzde kaçının iptal edildiği. Uyarı eşiği ${formatPercent(cancellations.warningRate)}.`, tone: warn(cancellations.cancelRate) ? 'warn' : undefined },
      { key: 'units', label: 'İptal Edilen Adet', value: cancellations.cancelledQuantity, note: 'Kısmi iptaller dahil', tip: 'İptal edilen ürün adedi; kısmi iptaller dahil.' },
      { key: 'amount', label: 'İptal Edilen Tutar', value: cancellations.cancelledAmount ?? null, format: formatTl, note: 'Satış tutarına girmez', tip: 'İptal edilen ürünlerin liste fiyatıyla tutarı; satışa girmez.' }
    ]} /></div>
    {warn(cancellations.cancelRate) && <p className="reports-warning is-wide" role="alert"><UiIcon name="alert" size={16} />İptal oranı {formatPercent(cancellations.warningRate)} eşiğinin üstünde; Trendyol satıcı puanı yönergesi bu oranı izler.</p>}
    <ReasonsCard title="İptal Nedenleri" description="Siparişte gelen iptal nedenine göre; nedeni gelmeyenler Bilinmiyor." reasons={cancellations.reasons} csvName="rapor-iptal-nedenleri" empty="Bu dönemde iptal yok." />
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
export function Cargo({ data }: { data: ReportSales }) {
  const packages = data.cargo.reduce((sum, row) => sum + row.packages, 0)
  const ship = weightedHours(data.cargo, row => row.averageHoursToShip, row => row.measuredShip)
  const deliver = weightedHours(data.cargo, row => row.averageHoursToDeliver, row => row.measuredDeliver)
  const measured = (count: number) => count > 0 ? `${formatCount(count)} pakette ölçüldü` : 'Ölçülemedi'
  const carrier = [...data.cargo].filter(row => row.known).sort((a, b) => b.packages - a.packages)[0]
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="Kargonun göstergeleri" long items={[
      { key: 'packages', label: 'Paket', value: packages, format: formatCount, note: `${formatCount(data.cargo.filter(row => row.known).length)} kargo firması`, tip: 'Dönemin siparişlerinden kargoya verilen paket sayısı.' },
      { key: 'carrier', label: 'Kargo Firması', value: null, text: carrier?.label, note: carrier ? `Paketlerin ${formatPercent(carrier.share)} kadarı` : 'Okunamadı', tip: 'Dönemde en çok paket taşıyan kargo firması.' },
      { key: 'ship', label: 'Kargo Teslim Alma', value: ship.hours, format: formatHours, note: measured(ship.measured), tip: 'Siparişten kargo firmasının paketi okuttuğu ana kadar geçen ortalama süre; yaklaşık.' },
      { key: 'deliver', label: 'Teslimat', value: deliver.hours, format: formatHours, note: measured(deliver.measured), tip: 'Kargoya verilişten müşteriye teslime kadar geçen ortalama süre; yaklaşık.' }
    ]} /></div>
    <Panel title="Kargo Firmalarının Payı" description="Paket sayısına göre." className="reports-list is-wide">
      <Bars label="Kargo firmalarının payı" format={value => formatPercent(value)} items={[...data.cargo].sort((a, b) => b.packages - a.packages)
        .map(row => ({ key: row.label, label: row.label, value: row.share ?? null, note: `${formatCount(row.packages)} paket`, muted: !row.known }))} />
    </Panel>
    <div className="is-wide"><DataTable title="Kargo Firmaları" description="Pazaryerinin bildirdiği durum zamanlarından yaklaşık süreler. Teslim alma, kargo firmasının paketi okuttuğu andır." csvName="rapor-kargo"
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

const hourRange = (hour: number) => `${String(hour).padStart(2, '0')}:00–${String(hour + 1).padStart(2, '0')}:00`

// When the orders come, in one card: pick the platforms (none picked: all of them) and a weekday (none: the whole
// week), and the columns show those orders hour by hour, the busiest hour marked. Each weekday button carries its
// count and a small bar of it, so the week reads at a glance. The full 7 × 24 grid still goes out as CSV.
export function TimeOfDay({ data }: { data: ReportSales }) {
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
        {total > 0 ? <Columns points={hours} format={value => `${formatCount(value)} sipariş`} axisFormat={formatCount} shown={shown} label={`${scope}: saatlere göre siparişler`} /> : <EmptyState icon="chart" title="Veri Yok">Bu seçimde sipariş yok.</EmptyState>}
      </>}
    </RevealPanel>
  </div>
}


export function Customers({ data }: { data: ReportSales }) {
  const customers = data.customers
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="Müşterilerin göstergeleri" stack items={[
      { key: 'unique', label: 'Müşteri', value: customers.uniqueCustomers, format: formatCount, note: `${formatCount(customers.identifiedOrders)} siparişten`, tip: 'Dönemde sipariş veren farklı müşteri sayısı; müşteri numarası, e-posta ya da telefondan tanınır.' },
      { key: 'repeat', label: 'Tekrar Gelen Müşteri', value: customers.repeatCustomers, format: formatCount, note: 'birden çok sipariş verdi' },
      { key: 'rate', label: 'Tekrar Gelme Oranı', value: customers.repeatRate, format: percent, note: 'tekrar gelen ÷ müşteri', tip: 'Müşterilerin yüzde kaçının birden fazla sipariş verdiği.' },
      { key: 'orders', label: 'Kişi Başı Sipariş', value: customers.ordersPerCustomer, note: 'ortalama', tip: 'Bir müşterinin dönemdeki ortalama sipariş sayısı.' },
      { key: 'unknown', label: 'Tanınmayan Sipariş', value: customers.unidentifiedOrders, format: formatCount, note: 'müşteri sayısına girmez', tip: 'Müşteri bilgisi gelmeyen siparişler; müşteri sayılarına girmez.' }
    ]} /></div>
    <RevealPanel title="Bir Kez Alan ve Tekrar Alan" className="is-wide" description="Dönemde birden çok sipariş veren müşteriler tekrar alan sayılır.">
      {shown => customers.uniqueCustomers > 0 ? <>
        <StackedBar shown={shown} total={customers.uniqueCustomers} label={`Bir kez alan ${formatCount(customers.uniqueCustomers - customers.repeatCustomers)}, tekrar alan ${formatCount(customers.repeatCustomers)}`}
          parts={[{ key: 'repeat', label: 'Tekrar Alan', value: customers.repeatCustomers, tone: 'left' }, { key: 'once', label: 'Bir Kez Alan', value: customers.uniqueCustomers - customers.repeatCustomers, tone: 'fees' }]} />
        <ul className="reports-legend-row">
          <li><i className="is-left" aria-hidden="true" />Tekrar Alan<b>{formatCount(customers.repeatCustomers)}</b></li>
          <li><i className="is-fees" aria-hidden="true" />Bir Kez Alan<b>{formatCount(customers.uniqueCustomers - customers.repeatCustomers)}</b></li>
        </ul>
      </> : <EmptyState icon="chart" title="Veri Yok">Kimliği okunan müşteri yok.</EmptyState>}
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

export function BestSellerTables({ data }: { data: ReportSales }) {
  return <>
    <div className="is-wide"><DataTable title="En Çok Satanların Stoğu" description="Ana depodaki güncel kullanılabilir stok ve dönemin satış hızıyla kalan gün; 7 günden azı işaretli." csvName="rapor-stok" rows={data.stock.bestSellers} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={stockColumns(data.stock.periodDays)} /></div>
    <div className="is-wide"><DataTable title="Satıp Stoğu Biten Ürünler" csvName="rapor-stok-biten" rows={data.stock.soldOut} rowKey={row => row.variantId ?? row.sku} initialSort="sold" columns={stockColumns(data.stock.periodDays)} empty="Dönemde satılıp stoğu sıfır olan ürün yok." /></div>
  </>
}

// The profit account line by line beside Trendyol's commission and cargo (the strip above holds the coverage and the
// estimated commission).
export function CostAndProfit({ data }: { data: ReportSales }) {
  const money = moneyBreakdown(data, profitSteps(data))
  return <div className="reports-grid">
    <Panel title="Kâr Hesabı" description="Satıştan net kâra kadar hesabın satır satır dökümü. Satırın üzerine gelince satış tutarına oranı görünür." className="reports-ledger-panel">
      {money ? <><ProfitLedger data={data} money={money} notes={ledgerNotes(data)} /><EstimateGaps money={money} shown /></> : <EmptyState icon="chart" title="Veri Yok">Bu dönemde tutarı olan sipariş yok.</EmptyState>}
    </Panel>
    <div className="reports-stack">
      <Ledger title="Komisyon ve Trendyol Kargo" description="Komisyon pazaryeri kaydı olan siparişte gerçek, kalanında tahmini; kargo Trendyol cari hesabından." rows={trendyolLedger(data)}
        empty="Trendyol cari hesabı bu seçimde okunmadı." />
      <Panel title="Platformlara Göre Net Ciro" description="Her platformun iadeler düşüldükten sonra kalan cirosu.">
        {/* A platform without amounts is listed in words, not drawn as a ₺0 bar. */}
        <Bars label="Platformlara göre net ciro" format={formatTl} items={data.platforms.filter(group => group.ordersWithAmount > 0).map(group => ({ key: group.key, label: platformName(group.label), value: group.netRevenue }))} />
        <NoAmountNote platforms={data.platforms} />
      </Panel>
    </div>
  </div>
}

// What the Kârlılık strip showed before 5 Nov 2026 (the commission's rate and coverage, how much of the sales carry a
// cost, and a cost and a commission), now under the account's commission and product cost rows.
function ledgerNotes(data: ReportSales): Record<string, string> {
  const { commission, cost } = data
  return {
    commission: `Ortalama komisyon oranı ${formatPercent(commission.effectiveRate)}; satışların ${formatPercent(commission.coverage)} kadarının oranı biliniyor.`,
    // The cost row is drawn only with the profit; with an estimated part its tip says so instead (Money.estimate).
    cost: `Müşteride kalan ${formatNumber(cost.keptQuantity)} adedin hepsinin maliyeti girili.`
  }
}

type LedgerRow = { label: string; value: string; hint?: string; kind?: 'minus' | 'total' | 'result'; tone?: 'good' | 'bad' }

// A small account: rows one under another, deductions in red with a minus, subtotals and the result ruled above and
// coloured by their sign.
// A row's note ("₺70.416,27 kesin, ₺19.639,78 tahmini") opens from the "i" beside its label, as Kâr Hesabı's rows do.
function Ledger({ title, description, rows, empty }: { title: string; description: string; rows: LedgerRow[]; empty: string }) {
  const tip = useId()
  return <Panel title={title} description={description} className="reports-ledger-panel">
    {rows.length === 0 ? <EmptyState icon="chart" title="Veri Yok">{empty}</EmptyState> : <dl className="reports-ledger">{rows.map((row, index) => <div key={row.label} className={['reports-tip-host', row.kind && `is-${row.kind}`, row.tone && `is-${row.tone}`].filter(Boolean).join(' ')}>
      <dt>{row.hint ? <button type="button" className="reports-tip-trigger" aria-describedby={`${tip}-${index}`}>{row.label}<TipMark /></button> : row.label}</dt>
      <dd>{row.kind === 'minus' ? `− ${row.value}` : row.value}</dd>
      {row.hint && <span role="tooltip" id={`${tip}-${index}`} className="reports-hover-tip"><TipText title={row.label} text={row.hint} /></span>}
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
    { label: 'Gerçek Komisyon', value: formatTl(commission.real), hint: commission.realOrders > 0 ? `${formatCount(commission.realOrders)} siparişte pazaryerinin kendi kaydından okunan komisyon; aynı siparişlerin tahmini ${formatTl(commission.estimatedForSettled)}.` : 'Pazaryeri kaydı olan sipariş yok.' },
    { label: 'Tahmini Komisyon', value: formatTl(commission.estimatedForUnsettled), hint: 'Pazaryeri kaydı olmayan siparişlerde, siparişteki komisyon oranından.' },
    { label: 'Hesap Kesimi Olan Sipariş', value: `${formatCount(finance.settledOrders)} / ${formatCount(finance.financeOrders)}`, hint: 'Trendyol siparişlerinden hesap kesimi gelenler.' },
    { label: 'Trendyol Kargo', value: formatTl(cargo.combined - (cargo.platformEstimated ?? 0)), hint: `Trendyol siparişlerinin kargosu: ${[`${formatCount(cargo.billedOrders)} sipariş faturalı`, cargo.unbilledOrders > 0 ? `${formatCount(cargo.unbilledOrders)} sipariş tahmini` : '',
      (cargo.unbilledReturns ?? 0) > 0 ? `${formatCount(cargo.unbilledReturns ?? 0)} iade dönüşü tahmini` : '',
      (cargo.unestimatedOrders ?? 0) > 0 ? `${formatCount(cargo.unestimatedOrders ?? 0)} sipariş tahmin edilemedi` : ''].filter(Boolean).join(', ')}.` },
    { label: 'Sipariş Başına Kargo', value: cargo.averagePerOrder === null ? '—' : formatTl(cargo.averagePerOrder), hint: 'Son 30 günde en sık kesilen kargo tutarı.' }
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
  const rows = selected === 'TRENDYOL' && read && finance ? statementRows(finance).sort((a, b) => Number(!a.isCost) - Number(!b.isCost) || a.label.localeCompare(b.label, 'tr')) : []
  const toolbar = platforms.length > 1 && <div className="reports-segmented" role="group" aria-label="Pazaryeri">
    {platforms.map(code => <button key={code} type="button" aria-pressed={selected === code} onClick={() => setPicked(code)}>{platformName(code)}</button>)}
  </div>
  return <DataTable<CostLine> key={selected} title={`${platformName(selected)} Cari Hesap Kalemleri`} csvName={`rapor-${selected.toLowerCase()}-cari`} rows={rows} rowKey={row => row.category} toolbar={toolbar}
    empty={`${platformName(selected)} cari hesabı henüz okunmuyor; şimdilik yalnız Trendyol'un hesap ekstresi okunuyor.`}
    description="Cari hesap kalemleri: dönemin siparişlerine ait olanlar ve dönemde siparişsiz kesilenler ayrı sütunda; ikisi de kâra dahil. Eksi tutar satıcıya ödenendir."
    columns={[
      { key: 'label', header: 'Kalem', value: row => row.label, render: row => <span className="reports-cell-label">{row.label}{!row.isCost && <em className="reports-tag">Masraf Değil</em>}</span> },
      { key: 'amount', header: 'Siparişlere Ait', value: row => row.amount, render: row => formatTl(row.amount), numeric: true },
      { key: 'linked', header: 'Siparişe Bağlı', value: row => row.orderLinked, render: row => formatTl(row.orderLinked), numeric: true },
      { key: 'estimated', header: 'Tahmini', value: row => row.estimated, render: row => formatTl(row.estimated), numeric: true },
      { key: 'unlinked', header: 'Siparişsiz', value: row => row.unlinked, render: row => formatTl(row.unlinked), numeric: true },
      { key: 'records', header: 'Kayıt', value: row => row.records, render: row => formatCount(row.records), numeric: true }
    ]} />
}

// One row per category of the statement: the period's orders' records and estimates beside what was charged in the
// period but belongs to no order of it (taken off the profit in a line of its own).
type CostLine = { category: string; label: string; isCost: boolean; amount: number; orderLinked: number; estimated: number; unlinked: number; records: number }
function statementRows(costs: ReportCosts): CostLine[] {
  const rows = new Map<string, CostLine>(costs.lines.map(line => [line.category, { category: line.category, label: unlinkedLabels[line.category] ?? line.label, isCost: line.isCost, amount: line.amount, orderLinked: line.orderLinked, estimated: line.estimated ?? 0, unlinked: 0, records: line.records }]))
  for (const line of costs.unlinked ?? []) {
    const row = rows.get(line.category) ?? { category: line.category, label: unlinkedLabels[line.category] ?? line.label, isCost: line.isCost, amount: 0, orderLinked: 0, estimated: 0, unlinked: 0, records: 0 }
    rows.set(line.category, { ...row, unlinked: row.unlinked + line.amount, records: row.records + line.records })
  }
  return [...rows.values()]
}

// The returned bucket's short name: "İade – Fatura Gerekmez" did not fit a strip cell or a table header (14 Nov 2026).
const returnedShort = 'Fatura Gerekmez'
const invoiceLabel = (bucket: { key: string; label: string }) => bucket.key === 'RETURNED' ? returnedShort : bucket.label

// Fatura: the packages by invoice state as one bar beside each platform's counts.
export function Invoices({ data }: { data: ReportSales }) {
  const total = data.invoices.buckets.reduce((sum, bucket) => sum + bucket.packages, 0)
  const tone = (key: string) => key === 'INVOICED' ? 'left' : key === 'NOT_INVOICED' ? 'returned' : key === 'RETURNED' ? 'cargo' : 'fees'
  const parts: Part[] = data.invoices.buckets.map(bucket => ({ key: bucket.key, label: invoiceLabel(bucket), value: bucket.packages, tone: tone(bucket.key) }))
  return <div className="reports-grid">
    <div className="is-wide"><KpiStrip label="Faturanın göstergeleri" items={data.invoices.buckets.map(bucket => ({ key: bucket.key, label: invoiceLabel(bucket), value: bucket.packages, format: formatCount,
      note: `Paketlerin ${formatPercent(total > 0 ? bucket.packages / total : null)} kadarı`, tip: `${bucket.key === 'RETURNED' ? 'İade edilen, fatura gerekmeyen paketler' : 'Bu fatura durumundaki paketler'}; toplam tutarları ${formatTl(bucket.amount)}.\nTutar müşterinin ödediğidir: pazaryerinin kendi indirimi Net Ciro'ya eklenir, buraya eklenmez.`, tone: bucket.key === 'NOT_INVOICED' && bucket.packages > 0 ? 'warn' as const : undefined }))} /></div>
    <RevealPanel title="Fatura Durumu" description="Panelde kesilen fatura, elle seçilen ya da pazaryerinin bildirdiği duruma göre; hiçbiri yoksa Bilinmiyor. Ayrıntı Faturalar sayfasında.">
      {shown => total > 0 ? <>
        <StackedBar parts={parts} total={total} shown={shown} label={`Fatura durumu: ${parts.map(part => `${part.label} ${formatCount(part.value)}`).join(', ')}`} />
        <ul className="reports-legend-row">{parts.map(part => <li key={part.key}><i className={`is-${part.tone}`} aria-hidden="true" />{part.label}<b>{formatPercent(part.value / total)}</b></li>)}</ul>
      </> : <EmptyState icon="chart" title="Veri Yok">Bu dönemde paket yok.</EmptyState>}
    </RevealPanel>
    <div className="is-wide"><DataTable title="Platformlara Göre Fatura Durumu" csvName="rapor-fatura" rows={data.invoices.platforms} rowKey={row => row.platformCode} initialSort="invoiced"
      footer={data.invoices.platforms.length > 1 && <tr><td>Toplam</td>{(['invoiced', 'notInvoiced', 'returned', 'unknown'] as const).map(key => <td key={key} className="is-num">{formatCount(data.invoices.platforms.reduce((sum, row) => sum + row[key], 0))}</td>)}</tr>}
      columns={[
      { key: 'platform', header: 'Platform', value: row => platformName(row.platformCode) },
      { key: 'invoiced', header: 'Faturalı', value: row => row.invoiced, render: row => formatCount(row.invoiced), numeric: true },
      { key: 'notInvoiced', header: 'Faturasız', value: row => row.notInvoiced, render: row => formatCount(row.notInvoiced), numeric: true },
      { key: 'returned', header: returnedShort, value: row => row.returned, render: row => formatCount(row.returned), numeric: true },
      { key: 'unknown', header: unknownLabel, value: row => row.unknown, render: row => formatCount(row.unknown), numeric: true }
    ]} empty="Bu dönemde paket yok." /></div>
  </div>
}
