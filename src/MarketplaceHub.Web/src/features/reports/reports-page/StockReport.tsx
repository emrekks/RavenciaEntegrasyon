import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { EmptyState, ErrorBox } from '../../../shared/components'
import { formatCount, formatNumber, formatPercent, formatTl, formatTlShort, formatTlWhole, platformName, profitRateQuery, stockQuery, type ReportProfitRate, type ReportSales, type ReportStockGroup, type ReportStockItem, type ReportStockPlatform } from '../report-format'
import { PlatformSquareMark } from '../../../shared/platform-square-mark'
import { thumbnailUrl } from '../../../shared/product-thumbnail'
import { queryKeys } from '../../../shared/api/query-keys'
import { RevealPanel, type Column, DataTable, Bars, type Kpi, KpiStrip } from './report-parts'
import { BestSellerTables } from './SalesTables'

// The best sellers' stock in the sales period (once Operasyon's Stok sub-tab; owner 7 Nov 2026 moved it to the Stok tab).
function bestSellerKpis(data: ReportSales): Kpi[] {
  const low = data.stock.bestSellers.filter(row => row.daysLeft !== null && row.daysLeft < 7).length
  const unknownStock = data.stock.bestSellers.filter(row => row.available === null).length
  return [
      { key: 'low', label: "Kalan Günü 7'den Az", value: low, format: formatCount, note: `En çok satan ${formatCount(data.stock.bestSellers.length)} ürün içinde`, tip: 'Stoğu, dönemin satış hızıyla bir haftadan az yetecek çok satan ürünler.', tone: low > 0 ? 'warn' : undefined },
      { key: 'out', label: 'Satıp Stoğu Biten', value: data.stock.soldOut.length, format: formatCount, note: 'Mevcut stoğu sıfır', tip: 'Dönemde satılıp ana depoda kullanılabilir stoğu kalmayan ürünler.', tone: data.stock.soldOut.length > 0 ? 'warn' : undefined },
      { key: 'unknown', label: 'Stoğu Bilinmeyen', value: unknownStock, format: formatCount, note: 'En çok satanlar içinde', tip: 'Panelde stok kaydı olmayan çok satan ürünler.' }
  ]
}

// A month's Turkish name from its first day ("2026-08-01" → "Ağustos").
const monthName = new Intl.DateTimeFormat('tr-TR', { month: 'long', timeZone: 'UTC' })
function monthLabel(from: string) {
  const label = monthName.format(new Date(`${from}T00:00:00Z`))
  return label.charAt(0).toLocaleUpperCase('tr-TR') + label.slice(1)
}

// The real profit on cost of each of the last whole months, as Kârlılık works it out (sales less returns, marketplace
// deductions, VAT and the product cost; the server's ReportProfitRate, from the same monthly reports), and their plain
// average over the months that have one. Loading until it is in.
type MonthlyProfitRate = { loading: boolean; average: number | null; months: Array<{ label: string; rate: number | null }> }
function useMonthlyProfitRate(platforms: string[] | null): MonthlyProfitRate {
  const query = profitRateQuery(platforms)
  const result = useQuery({ queryKey: queryKeys.reportsProfitRate(query), queryFn: ({ signal }) => hubApi<ReportProfitRate>(`/reports/profit-rate${query ? `?${query}` : ''}`, { signal }), staleTime: 60_000 })
  const rates = (result.data?.months ?? []).map(month => ({ label: monthLabel(month.from), rate: month.rate }))
  const known = rates.flatMap(month => month.rate === null ? [] : [month.rate])
  // The average kept to a millionth, so three equal months average to the same rate on screen (no float tail).
  return { loading: result.isPending, average: known.length ? Math.round(known.reduce((sum, rate) => sum + rate, 0) / known.length * 1e6) / 1e6 : null, months: rates }
}

// Tahmini Kâr: the capital in stock at the average real profit on cost of the last whole months.
function stockProfitKpi(capital: number, rate: MonthlyProfitRate): Kpi {
  const counted = rate.months.filter(month => month.rate !== null)
  const span = counted.length ? `${counted[0].label}${counted.length > 1 ? ` – ${counted.at(-1)!.label}` : ''}` : ''
  const value = rate.average === null || capital <= 0 ? null : capital * rate.average
  return {
    key: 'profit', label: value !== null && value < 0 ? 'Tahmini Zarar' : 'Tahmini Kâr', value, format: formatTl, tone: value === null ? undefined : value < 0 ? 'bad' : 'good',
    note: value === null ? (rate.loading ? 'Hesaplanıyor' : 'Hesaplanamadı') : undefined,
    tip: `Stok, son ayların maliyete göre net kâr oranıyla satılırsa kalacak tahmini kâr.${rate.average === null
      ? (rate.loading ? ' Aylık kâr oranları alınıyor.' : ' Geçmiş aylarda kâr bilinmiyor: satılan adetlerin %95\'inden azının maliyeti girili.')
      : `\n${span}: ortalama ${formatPercent(rate.average)}`}`
  }
}

// Stok: the stock on hand now (the main warehouse) with the capital it binds at cost and its value at the panel price
// (as the product editor shows it), broken down by category, colour, size, price band and store; then how long it lasts at the
// period's pace and the stock that has not sold for deadDays. Stock is a point-in-time value: the period changes only the
// sales (cover days); the platforms pick the variants on sale there. Money is VAT included, as the panel stores it.
export function StockReport({ from, to, platforms, sales }: { from: string; to: string; platforms: string[] | null; sales: ReportSales }) {
  const query = stockQuery({ from, to, platforms })
  const result = useQuery({ queryKey: queryKeys.reportsStock(query), queryFn: ({ signal }) => hubApi<ApiAnswer<'getReportsStock'>>(`/reports/stock?${query}`, { signal }), staleTime: 60_000, placeholderData: previous => previous })
  const rate = useMonthlyProfitRate(platforms)
  const data = result.data
  if (result.isError && !data) return <ErrorBox error={result.error} onRetry={() => void result.refetch()} />
  if (!data) return <StockSkeleton />
  const summary = data.summary
  if (summary.units <= 0) return <div className="reports-summary">
    <EmptyState icon="chart" title="Veri Yok">{platforms?.length ? 'Seçilen platformlarda satışta olan stoklu ürün yok.' : 'Ana depoda stoğu olan ürün yok.'}</EmptyState>
  </div>
  // The cards show their label and value only; what the notes said is in each card's explanation.
  const money: Kpi[] = [
    { key: 'capital', label: 'Stoktaki Sermaye', value: summary.capital, format: formatTl,
      tip: `Stoktaki ürünlere bağlı para, girdiğiniz maliyetle (KDV dahil)${summary.capitalExVat === null ? '' : `; KDV hariç ${formatTlWhole(summary.capitalExVat)}`}.${summary.missingCostVariants > 0 ? ` Maliyeti girilmemiş ${formatCount(summary.missingCostVariants)} varyant dahil değil.` : ''}` },
    { key: 'value', label: 'Envanter Satış Değeri', value: summary.value, format: formatTl,
      tip: `Stoğun ürün kartındaki satış fiyatıyla tutarı (KDV dahil).${summary.missingPriceUnits > 0 ? ` Fiyatı olmayan ${formatNumber(summary.missingPriceUnits)} adet dahil değil.` : ''}` },
    stockProfitKpi(summary.capital, rate),
    { key: 'units', label: 'Toplam Stok Adedi', value: summary.units,
      tip: `Ana depodaki güncel stok adedi; satılabilir olanı ${formatNumber(summary.availableUnits)} adet.` },
    { key: 'cover', label: 'Stok Gün', value: summary.coverDays, format: value => `${formatNumber(Math.round(value))} gün`,
      tip: `Stoğun, dönemin günlük net satışıyla kaç gün yeteceği.${summary.dailySales === null ? ' Dönemde satış yok.' : ''}` }
  ]
  const counts: Kpi[] = [
    { key: 'stocked', label: 'Stoklu Varyant', value: summary.stockedVariants, tip: `Ana depoda en az bir adedi olan aktif varyantlar; toplam ${formatCount(summary.variants)} aktif varyant.` },
    { key: 'empty', label: 'Stoksuz Varyant', value: summary.emptyVariants, tip: 'Ana depoda hiç adedi kalmayan aktif varyantlar.' },
    { key: 'low', label: 'Beden Kırığı', value: summary.lowStockProducts, tone: summary.lowStockProducts > 0 ? 'warn' : undefined,
      tip: 'Modelinde stok süren, bedenlerinin yarısı ya da fazlası tükenmiş renkler; tamamen biten renkler de sayılır.' },
    { key: 'cost', label: 'Maliyeti Eksik', value: summary.missingCostVariants, tone: summary.missingCostVariants > 0 ? 'warn' : undefined,
      tip: 'Stoğu olup maliyeti girilmemiş varyantlar; sermaye ve kâr bunlarsız hesaplanır.' },
    { key: 'price', label: 'Fiyatı Eksik', value: summary.missingPriceUnits, tone: summary.missingPriceUnits > 0 ? 'warn' : undefined,
      tip: 'Ürün kartında satış fiyatı olmayan stok adedi; satış değerine girmez.' }
  ]
  const bars = (groups: ReportStockGroup[], value: (group: ReportStockGroup) => number, limit = 10) =>
    groups.slice(0, limit).map(group => ({ key: group.key, label: group.label, value: value(group), muted: !group.known }))
  const adet = (value: number) => `${formatNumber(value)} adet`
  return <div className="reports-summary">
    <KpiStrip label="Stoğun parası" items={money} long />
    <KpiStrip label="Stoğun durumu" items={counts} long />
    <KpiStrip label="Çok satanların stoğu" items={bestSellerKpis(sales)} long />
    <div className="reports-grid">
      <BestSellerTables data={sales} />
      <div className="is-wide"><DataTable title="Kategori Bazlı Stok" csvName="rapor-stok-kategori" rows={data.categories} rowKey={row => row.key} unknown={row => !row.known} initialSort="capital"
        description="Stoklu varyantlar kategorisine göre; satış, dönemin net satışıdır." columns={stockGroupColumns('Kategori', true)} /></div>
      <RevealPanel title="Renge Göre Adet" description="Stoklu varyantların rengine göre stok adedi.">{() =>
        <Bars label="Renge göre adet" items={bars(data.colors, group => group.units, 12)} format={adet} />}</RevealPanel>
      <RevealPanel title="Bedene Göre Adet" description="Stoklu varyantların bedenine göre stok adedi.">{() =>
        <Bars label="Bedene göre adet" items={bars(data.sizes, group => group.units, 12)} format={adet} />}</RevealPanel>
      <div className="is-wide"><RevealPanel title="Stok Gün Dağılımı" description="Sermayenin, stoğun kaç gün yeteceğine göre dağılımı; dönemde satmayanlar en sonda.">{() =>
        <Bars label="Stok gününe göre sermaye" items={bars(data.cover, group => group.capital)} format={formatTlShort} />}</RevealPanel></div>
      <div className="is-wide"><DataTable title="Platformlara Göre Stok" csvName="rapor-stok-platform" rows={data.platforms} rowKey={row => row.connectionId} initialSort="listed"
        description="Mağazada aktif teklifi olan varyantların stoğu; değer mağazanın kendi fiyatıyla."
        empty="Seçilen mağazalarda teklif yok." columns={stockPlatformColumns} /></div>
      <div className="is-wide"><DataTable title="Fiyat Bantları" csvName="rapor-stok-fiyat-bandi" rows={data.priceBands} rowKey={row => row.key} unknown={row => !row.known}
        description="Panel satış fiyatına göre; alt sınır dahil, üst sınır hariç." columns={stockGroupColumns('Fiyat Bandı', false)} /></div>
      <div className="is-wide"><DataTable title="En Çok Sermaye Bağlayan Ürünler" csvName="rapor-stok-sermaye" rows={data.topCapital} rowKey={row => row.key} initialSort="capital"
        description="Stoğuna en çok para bağlanan 20 ürün; stok gün, dönemin satış hızıyla." columns={stockItemColumns(data.deadDays)} /></div>
      <div className="is-wide"><DataTable title="Ölü Stok" csvName="rapor-stok-olu" rows={data.dead} rowKey={row => row.key} initialSort="capital"
        description={`Son ${data.deadDays} günde hiç net satışı olmayan stoklu varyantlar; sermayeye göre ilk 50.`}
        empty={`Son ${data.deadDays} günde satmayan stoklu varyant yok.`} columns={stockItemColumns(data.deadDays)} /></div>
    </div>
  </div>
}

// The tab's shape while it loads, so nothing moves when the figures come in.
function StockSkeleton() {
  return <div className="reports-summary reports-stock-skeleton" role="status" aria-label="Stok raporu hazırlanıyor">
    {[5, 5, 3].map((cells, strip) => <div key={strip} className={`reports-kpis is-${cells}${strip < 2 ? ' is-long' : ''}`}>{Array.from({ length: cells }, (_, index) => <div key={index}>
      <span className="reports-skeleton is-label" /><span className="reports-skeleton is-figure" />
    </div>)}</div>)}
    <div className="reports-grid">{[0, 1].map(index => <div key={index} className="reports-panel"><span className="reports-skeleton is-label" /><span className="reports-skeleton is-block" /></div>)}</div>
  </div>
}

const coverText = (days: number | null) => days === null ? '—' : `${formatNumber(Math.round(days))} gün`

function stockGroupColumns(header: string, withProducts: boolean): Column<ReportStockGroup>[] {
  const products: Column<ReportStockGroup>[] = withProducts ? [{ key: 'products', header: 'Ürün', numeric: true, value: row => row.products, render: row => formatCount(row.products) }] : []
  return [
    { key: 'label', header, value: row => row.label },
    ...products,
    { key: 'variants', header: 'Varyant', numeric: true, value: row => row.variants, render: row => formatCount(row.variants) },
    { key: 'units', header: 'Adet', numeric: true, value: row => row.units, render: row => formatNumber(row.units) },
    { key: 'capital', header: 'Sermaye', numeric: true, value: row => row.capital, render: row => formatTl(row.capital) },
    { key: 'value', header: 'Satış Değeri', numeric: true, value: row => row.value, render: row => formatTl(row.value) },
    { key: 'margin', header: 'Marj', numeric: true, value: stockMargin, render: row => formatPercent(stockMargin(row)) },
    { key: 'sold', header: 'Dönem Satışı', numeric: true, value: row => row.soldUnits, render: row => formatNumber(row.soldUnits) },
    { key: 'cover', header: 'Stok Gün', numeric: true, value: row => row.coverDays, render: row => coverText(row.coverDays) }
  ]
}

// A bucket's margin over its value, only where every stocked unit of it is priced (the capital of unpriced units would
// lower it).
const stockMargin = (row: ReportStockGroup) => row.value !== null && row.value > 0 && row.pricedUnits === row.units ? (row.value - row.capital) / row.value : null

const stockPlatformColumns: Column<ReportStockPlatform>[] = [
  { key: 'store', header: 'Mağaza', value: row => row.storeName, render: row => <span className="reports-cell-label"><PlatformSquareMark code={row.platformCode} name={platformName(row.platformCode)} />{row.storeName}
    {row.isTest && <em className="reports-tag">Test</em>}</span> },
  { key: 'offers', header: 'Teklif', numeric: true, value: row => row.offers, render: row => formatCount(row.offers) },
  { key: 'active', header: 'Aktif Teklif', numeric: true, value: row => row.activeOffers, render: row => formatCount(row.activeOffers) },
  { key: 'listed', header: 'Listelenen Adet', numeric: true, value: row => row.listedUnits, render: row => formatNumber(row.listedUnits) },
  { key: 'unlisted', header: 'Listelenmeyen Adet', numeric: true, value: row => row.unlistedUnits, render: row => formatNumber(row.unlistedUnits) },
  { key: 'value', header: 'Satış Değeri', numeric: true, value: row => row.value, render: row => formatTl(row.value) },
  { key: 'average', header: 'Ort. Fiyat', numeric: true, value: row => row.averagePrice, render: row => formatTl(row.averagePrice) }
]

function stockItemColumns(deadDays: number): Column<ReportStockItem>[] {
  return [
    { key: 'title', header: 'Ürün', value: row => row.title, csv: row => [row.title, row.modelCode, row.options].filter(Boolean).join(' · '), render: row => <span className="reports-product">
      {row.imageUrl ? <img src={thumbnailUrl(row.imageUrl)} alt="" loading="lazy" className="reports-product-image" /> : <i className="reports-product-image" aria-hidden="true">{row.title.slice(0, 1)}</i>}
      <span className="reports-stock-item"><strong>{row.title}</strong>{(row.options || row.modelCode) && <small>{[row.modelCode, row.options].filter(Boolean).join(' · ')}</small>}</span>
    </span> },
    { key: 'category', header: 'Kategori', value: row => row.category },
    { key: 'units', header: 'Adet', numeric: true, value: row => row.units, render: row => formatNumber(row.units) },
    { key: 'capital', header: 'Sermaye', numeric: true, value: row => row.capital, render: row => formatTl(row.capital) },
    { key: 'value', header: 'Satış Değeri', numeric: true, value: row => row.value, render: row => formatTl(row.value) },
    { key: 'sold', header: 'Dönem Satışı', numeric: true, value: row => row.soldUnits, render: row => formatNumber(row.soldUnits) },
    { key: 'recent', header: `Son ${deadDays} Gün`, numeric: true, value: row => row.recentSoldUnits, render: row => formatNumber(row.recentSoldUnits) },
    { key: 'cover', header: 'Stok Gün', numeric: true, value: row => row.coverDays, render: row => coverText(row.coverDays) }
  ]
}
