import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, Pager, TitleHint, UiIcon } from '../../shared/components'
import { copyText } from '../../shared/copy-text'
import { formatIstanbul } from '../../shared/format-date'
import { thumbnailUrl } from '../../shared/product-thumbnail'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { platformName } from './report-format'
import { KpiStrip, type Kpi } from './ReportsPage'

// Sipariş Kârlılık Analizi (ReportsContracts.cs, ReportOrderProfitView): each order of the period with what it leaves
// after product cost, commission, cargo, the platform's fees, withholding tax and net VAT, and its items one by one.
// amount null: unknown (a Trendyol cargo with no invoice and no tariff to estimate it at, a commission with no rate on
// the order nor of its store), and so the order's profit.
export type OrderProfitCost = { key: string; label: string; amount: number | null; estimated: boolean; source: string | null }
export type OrderProfitLine = { variantId: string | null; title: string; barcode: string | null; sku: string; quantity: number; returnedQuantity: number; amount: number
  commissionRate: number | null; commission: number | null; costPrice: number | null; vatRate: number; imageUrl: string | null; options: string | null }
export type OrderProfit = { orderId: string; orderNumber: string; platformCode: string; storeName: string; storeFront: string | null; microExport: boolean; orderedAt: string
  state: 'SOLD' | 'PARTLY_RETURNED' | 'RETURNED'; amount: number; cost: number | null; profit: number | null; profitOnCost: number | null; margin: number | null
  // Product lines the panel could not read: the order is marked and its cost and profit are unknown.
  costs: OrderProfitCost[]; lines: OrderProfitLine[]; unreadableLines?: number }
// The totals of every order the filter keeps: amount over all of them; profit, profitAmount (their amount) and margin
// only over the profitOrders whose profit is known (profit null when none is).
export type OrderProfitPage = { items: OrderProfit[]; totalCount: number; page: number; pageSize: number; amount: number; profit: number | null; missingCost: number
  profitAmount: number; profitOrders: number; margin: number | null; missingCargo: number; missingCommission?: number }
type Sort = 'date' | 'amount' | 'profit' | 'profitOnCost' | 'margin'

const money = (value: number) => `${value < 0 ? '−' : ''}₺${Math.abs(value).toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const percent = (value: number | null) => value === null ? '—' : `%${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const tone = (value: number | null) => value === null ? 'is-unknown' : value < 0 ? 'is-loss' : 'is-gain'
const count = (value: number) => value.toLocaleString('tr-TR')
const orders = (value: number) => `${count(value)} sipariş`

// The summary over the list in the KPI boxes of the other tabs (owner, 7 Nov 2026), the values as before: the orders'
// own charges only, the deductions tied to no order are taken off Kârlılık's Net Kâr, not here.
function summaryKpis(data: OrderProfitPage): Kpi[] {
  const profitTone = data.profit === null ? undefined : data.profit < 0 ? 'bad' : 'good'
  const missing = (key: string, label: string, value: number, tip: string): Kpi[] => value > 0
    ? [{ key, label, value, format: orders, tone: 'warn', icon: 'alertTriangle', note: 'kârı hesaplanmaz', tip }] : []
  return [
    { key: 'orders', label: 'Sipariş', value: data.totalCount, format: count, icon: 'orders', note: `${count(data.profitOrders)} siparişin kârı bilinir`,
      tip: 'Seçilen dönemde ve aramada listelenen siparişler.\nKârı bilinen siparişler, ürün maliyeti, komisyonu ve kargosu bilinenlerdir.' },
    { key: 'amount', label: 'Sipariş Tutarı', value: data.amount, format: money, icon: 'lira', note: data.totalCount > 0 ? `sipariş başına ${money(data.amount / data.totalCount)}` : undefined,
      tip: 'Listelenen siparişlerin toplam tutarı.\nSipariş başına: toplam tutar ÷ sipariş sayısı.' },
    { key: 'profit', label: 'Siparişlerin Kârı', value: data.profit, format: money, icon: 'wallet', tone: profitTone, note: 'reklam ve cezalar hariç',
      tip: 'Kârı bilinen siparişlerin toplam kârı.\nSiparişe bağlı olmayan kesintiler (reklam, ceza…) hariç; Kârlılık\'taki Net Kâr bunları da düşer.' },
    { key: 'margin', label: 'Kâr Marjı', value: data.margin, format: value => percent(value), icon: 'percent', note: `${money(data.profitAmount)} tutar üzerinden`,
      tip: 'Siparişlerin kârının tutarına oranı.\nSiparişlerin kârı ÷ kârı bilinen siparişlerin tutarı.' },
    ...missing('cost', 'Maliyeti Eksik', data.missingCost, 'Ürün maliyeti girilmemiş siparişler.\nBu siparişlerin kârı hesaplanmaz; maliyeti ürün kartından girin.'),
    ...missing('cargo', 'Kargosu Bilinmeyen', data.missingCargo, 'Kargo faturası gelmemiş ve tarifeyle tahmin edilemeyen siparişler.\nBu siparişlerin kârı hesaplanmaz.'),
    ...missing('commission', 'Komisyonu Eksik', data.missingCommission ?? 0, 'Siparişte ve mağazanın bu dönemdeki satışlarında komisyon oranı yok.\nBu siparişlerin kârı hesaplanmaz.')
  ]
}
export const stateText: Record<OrderProfit['state'], string | null> = { SOLD: null, PARTLY_RETURNED: 'Kısmi İade', RETURNED: 'İade' }

// Why an order's profit is unknown: a line the panel could not read, a line without a product cost, a cargo that could
// not be estimated, or a commission nobody knows.
export const unknownProfit = (order: OrderProfit) => (order.unreadableLines ?? 0) > 0 ? 'Satır okunamadı' : order.cost === null ? 'Maliyet eksik'
  : order.costs.some(cost => cost.key === 'commission' && cost.amount === null) ? 'Komisyon eksik' : 'Kargo bilinmiyor'


// The query of a page: the period and platforms of the report, the search, the order and the page.
export function orderProfitQuery(filters: { from: string; to: string; platforms: string[] | null; search: string; sort: Sort; descending: boolean; page: number }) {
  const query = new URLSearchParams({ from: filters.from, to: filters.to, sort: filters.sort, dir: filters.descending ? 'desc' : 'asc', page: String(filters.page), limit: '50' })
  if (filters.platforms?.length) query.set('platformCodes', filters.platforms.join(','))
  if (filters.search.trim()) query.set('search', filters.search.trim())
  return query.toString()
}

// "Renk: Siyah | Beden: M | Kumaş: Pamuk" as "Siyah · M": only the colour and the size; a variant's other attributes stay out.
const shownOption = /^(renk|beden|numara|color|colour|size|web color)$/i
export const optionText = (options: string | null) => options?.split('|')
  .map(part => { const [key, ...rest] = part.split(':'); return shownOption.test(key.trim()) ? rest.join(':').trim() : '' })
  .filter(Boolean).join(' · ') ?? ''

export function OrderProfitability({ from, to, platforms }: { from: string; to: string; platforms: string[] | null }) {
  const [search, setSearch] = useState('')
  const [applied, setApplied] = useState('')
  const [sort, setSort] = useState<Sort>('date')
  const [descending, setDescending] = useState(true)
  const [page, setPage] = useState(0)
  // What is typed searches a moment after typing stops (Enter at once), as the other lists do.
  useEffect(() => {
    if (search === applied) return undefined
    const timer = window.setTimeout(() => { setApplied(search); setPage(0) }, 350)
    return () => window.clearTimeout(timer)
  }, [search, applied])
  const [open, setOpen] = useState<OrderProfit | null>(null)
  // Another period or platform choice is another list: it opens on its first page, as a new search or order does.
  const scope = `${from}|${to}|${platforms?.join(',') ?? ''}`
  const [pageScope, setPageScope] = useState(scope)
  if (pageScope !== scope) { setPageScope(scope); setPage(0) }
  const query = orderProfitQuery({ from, to, platforms, search: applied, sort, descending, page: pageScope === scope ? page : 0 })
  const result = useQuery({ queryKey: ['report-order-profit', query], queryFn: () => hubApi<OrderProfitPage>(`/reports/order-profitability?${query}`), staleTime: 60_000, placeholderData: previous => previous })
  const data = result.data
  const pageCount = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1
  // A page past the end (the list shrank since it was opened) moves to the last page there is.
  if (data && !result.isPlaceholderData && page > pageCount - 1) setPage(pageCount - 1)
  // A header click orders by it, highest first; a second click turns the order round.
  const order = (key: Sort) => { if (sort === key) setDescending(value => !value); else { setSort(key); setDescending(true) } setPage(0) }
  const header = (key: Sort, label: string) => <th scope="col" aria-sort={sort === key ? descending ? 'descending' : 'ascending' : 'none'}>
    <button type="button" className={`reports-order-sort${sort === key ? ' is-on' : ''}`} onClick={() => order(key)}>{label}<span aria-hidden="true">{sort === key ? descending ? '↓' : '↑' : '↕'}</span></button>
  </th>

  return <section className="reports-panel reports-order-profit" aria-labelledby="order-profit-title">
    <header className="reports-order-profit-head">
      <h2 id="order-profit-title">Sipariş Kârlılık Analizi<TitleHint label="Sipariş Kârlılık Analizi">Her sipariş için ürün maliyeti, komisyon, kargo, hizmet bedeli, stopaj ve net KDV düştükten sonra kalan kâr. Henüz kesinleşmeyen kalemler tahminidir. Reklam, ceza gibi siparişe bağlı olmayan kesintiler bir siparişe dağıtılamadığı için burada yoktur; Siparişlerin Kârı bu yüzden Kârlılık'taki Net Kâr'dan farklıdır (o, bu kesintileri de düşer).</TitleHint></h2>
      <label className="rv-search-field reports-order-search"><UiIcon name="search" size={16} />
        <input type="search" value={search} onChange={event => setSearch(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { setApplied(search); setPage(0) } }} placeholder="Sipariş no, ürün, barkod" aria-label="Sipariş ara" />
      </label>
    </header>
    {data && <div className="reports-order-summary"><KpiStrip label="Siparişlerin özeti" items={summaryKpis(data)} long /></div>}
    {result.isLoading && <Busy />}
    {result.isError && <ErrorBox error={result.error} onRetry={() => void result.refetch()} />}
    {data && data.items.length === 0 && <p className="reports-empty">Bu dönemde sipariş yok.</p>}
    {data && data.items.length > 0 && <div className="reports-table-wrap"><table className="reports-table reports-order-table">
      <thead><tr><th scope="col">Sipariş Numarası</th>{header('date', 'Sipariş Tarihi')}{header('amount', 'Sipariş Tutarı')}{header('profit', 'Kâr Tutarı')}{header('profitOnCost', 'Kâr Oranı')}{header('margin', 'Kâr Marjı')}<th scope="col"><span className="reports-sr">Ayrıntı</span></th></tr></thead>
      <tbody>{data.items.map(item => <tr key={item.orderId}>
        <th scope="row"><span className="reports-order-number"><PlatformSquareMark code={item.platformCode} name={platformName(item.platformCode)} />{item.orderNumber}<CopyNumber value={item.orderNumber} />
          {item.microExport && <em className="reports-order-badge is-micro" title={item.storeFront ?? undefined}>Mikro</em>}
          {stateText[item.state] && <em className="reports-order-badge is-return">{stateText[item.state]}</em>}
          {(item.unreadableLines ?? 0) > 0 && <em className="reports-order-badge is-return" title={`${item.unreadableLines} ürün satırı okunamadı; sipariş yeniden okunuyor, kârı hesaplanmaz`}>Okunamayan Satır</em>}</span></th>
        <td>{formatIstanbul(item.orderedAt, { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })}</td>
        <td className="is-num">{money(item.amount)}</td>
        <td className={`is-num ${tone(item.profit)}`}>{item.profit === null ? unknownProfit(item) : money(item.profit)}</td>
        <td className={`is-num ${tone(item.profitOnCost)}`}>{percent(item.profitOnCost)}</td>
        <td className={`is-num ${tone(item.margin)}`}>{percent(item.margin)}</td>
        <td><button type="button" className="secondary reports-order-detail" onClick={() => setOpen(item)}>Detaylar</button></td>
      </tr>)}</tbody>
    </table></div>}
    {pageCount > 1 && <div className="reports-order-pager"><Pager page={page + 1} totalPages={pageCount} onPageChange={next => setPage(next - 1)} ariaLabel="Sayfalama" /></div>}
    {open && <OrderProfitModal order={open} onClose={() => setOpen(null)} />}
  </section>
}

// The order number's copy button: a check for a moment once it is on the clipboard.
function CopyNumber({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => { if (!await copyText(value)) return; setCopied(true); window.setTimeout(() => setCopied(false), 1400) }
  return <button type="button" className="reports-order-copy" title={copied ? 'Kopyalandı' : 'Kopyala'}
    aria-label={copied ? `Sipariş numarası ${value} kopyalandı` : `Sipariş numarası ${value} kopyala`} onClick={() => void copy()}>
    <UiIcon name={copied ? 'check' : 'copy'} size={14} />
  </button>
}

// Sipariş Analizi: the order's items and every charge with where it comes from, and what is left.
function OrderProfitModal({ order, onClose }: { order: OrderProfit; onClose: () => void }) {
  return <Modal open onClose={onClose} className="reports-order-modal" title={`Sipariş Analizi · #${order.orderNumber}`}
    description={`${formatIstanbul(order.orderedAt, { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })} · ${order.storeName}${order.microExport ? ` · Mikro ihracat (${order.storeFront})` : ''}`}>
    <div className="reports-order-columns">
      <section>
        <h3>Siparişteki Ürünler <em>{order.lines.length} kalem</em></h3>
        <ul className="reports-order-lines">{order.lines.map((line, index) => <li key={`${line.sku}-${index}`}>
          {line.imageUrl ? <img src={thumbnailUrl(line.imageUrl)} alt="" loading="lazy" /> : <i aria-hidden="true">{line.title.slice(0, 1)}</i>}
          <span>
            <b>{line.title}</b>
            <small>{[optionText(line.options), line.barcode ?? line.sku].filter(Boolean).join(' · ')}</small>
            <small>{line.quantity.toLocaleString('tr-TR')} adet{line.returnedQuantity > 0 ? ` · ${line.returnedQuantity.toLocaleString('tr-TR')} adet iade` : ''}
              {line.commissionRate !== null ? ` · Komisyon %${line.commissionRate.toLocaleString('tr-TR')} (${money(line.commission ?? 0)})` : ''}</small>
            <small>{line.costPrice ? `Maliyet ${money(line.costPrice)} · Maliyet KDV'si %${line.vatRate.toLocaleString('tr-TR')}` : 'Maliyet girilmemiş'}</small>
          </span>
          <strong>{money(line.amount)}</strong>
        </li>)}</ul>
      </section>
      <section>
        <h3>Gider Kalemleri</h3>
        <ul className="reports-order-costs">
          <li><span><b>Sipariş Tutarı</b><small>Satıcı indirimi ve iadeler düşülmüş</small></span><strong>{money(order.amount)}</strong></li>
          <li><span><b>Ürün Maliyeti</b><small>Ürün ayarlarındaki maliyet</small></span><strong>{order.cost === null ? 'Eksik' : money(-order.cost)}</strong></li>
          {order.costs.map(cost => <li key={cost.key}>
            <span><b>{cost.label}{cost.estimated && cost.amount !== null && !cost.label.startsWith('Tahmini') && <em>Tahmini</em>}</b>{cost.source && <small>{cost.source}</small>}</span>
            <strong>{cost.amount === null ? 'Bilinmiyor' : money(-cost.amount)}</strong>
          </li>)}
        </ul>
      </section>
    </div>
    {/* What is left, at the bottom, under the charges it is worked out from. */}
    <div className={`reports-order-result ${tone(order.profit)}`}>
      <span>Bu siparişteki net kârınız<small>Tüm giderler çıkarıldıktan sonra</small></span>
      <strong>{order.profit === null ? unknownProfit(order) : money(order.profit)}</strong>
    </div>
  </Modal>
}
