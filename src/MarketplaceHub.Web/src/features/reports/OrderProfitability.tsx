import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, UiIcon } from '../../shared/components'
import { copyText } from '../../shared/copy-text'
import { formatIstanbul } from '../../shared/format-date'
import { thumbnailUrl } from '../../shared/product-thumbnail'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { platformName } from './report-format'

// Sipariş Kârlılık Analizi (ReportsContracts.cs, ReportOrderProfitView): each order of the period with what it leaves
// after product cost, commission, cargo, the platform's fees, withholding tax and net VAT, and its items one by one.
export type OrderProfitCost = { key: string; label: string; amount: number; estimated: boolean; source: string | null }
export type OrderProfitLine = { variantId: string | null; title: string; barcode: string | null; sku: string; quantity: number; returnedQuantity: number; amount: number
  commissionRate: number | null; commission: number | null; costPrice: number | null; vatRate: number; imageUrl: string | null; options: string | null }
export type OrderProfit = { orderId: string; orderNumber: string; platformCode: string; storeName: string; storeFront: string | null; microExport: boolean; orderedAt: string
  state: 'SOLD' | 'PARTLY_RETURNED' | 'RETURNED'; amount: number; cost: number | null; profit: number | null; profitOnCost: number | null; margin: number | null
  costs: OrderProfitCost[]; lines: OrderProfitLine[] }
type Page = { items: OrderProfit[]; totalCount: number; page: number; pageSize: number; amount: number; profit: number; missingCost: number }
type Sort = 'date' | 'amount' | 'profit' | 'profitOnCost' | 'margin'

const money = (value: number) => `${value < 0 ? '−' : ''}₺${Math.abs(value).toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const percent = (value: number | null) => value === null ? '—' : `%${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const tone = (value: number | null) => value === null ? 'is-unknown' : value < 0 ? 'is-loss' : 'is-gain'
export const stateText: Record<OrderProfit['state'], string | null> = { SOLD: null, PARTLY_RETURNED: 'Kısmi İade', RETURNED: 'İade' }

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
  const [open, setOpen] = useState<OrderProfit | null>(null)
  const query = orderProfitQuery({ from, to, platforms, search: applied, sort, descending, page })
  const result = useQuery({ queryKey: ['report-order-profit', query], queryFn: () => hubApi<Page>(`/reports/order-profitability?${query}`), staleTime: 60_000, placeholderData: previous => previous })
  const data = result.data
  const pageCount = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1
  // A header click orders by it, highest first; a second click turns the order round.
  const order = (key: Sort) => { if (sort === key) setDescending(value => !value); else { setSort(key); setDescending(true) } setPage(0) }
  const header = (key: Sort, label: string) => <th scope="col" aria-sort={sort === key ? descending ? 'descending' : 'ascending' : 'none'}>
    <button type="button" className={`reports-order-sort${sort === key ? ' is-on' : ''}`} onClick={() => order(key)}>{label}<span aria-hidden="true">{sort === key ? descending ? '↓' : '↑' : '↕'}</span></button>
  </th>

  return <section className="reports-panel reports-order-profit" aria-labelledby="order-profit-title">
    <header className="reports-order-profit-head">
      <div><h2 id="order-profit-title">Sipariş Kârlılık Analizi</h2><p>Her sipariş için ürün maliyeti, komisyon, kargo, hizmet bedeli, stopaj ve net KDV düştükten sonra kalan kâr. Henüz kesinleşmeyen kalemler tahminidir.</p></div>
      <form className="reports-order-search" onSubmit={event => { event.preventDefault(); setApplied(search); setPage(0) }}>
        <input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Sipariş no, ürün, barkod" aria-label="Sipariş ara" />
        <button type="submit" className="secondary">Ara</button>
      </form>
    </header>
    {data && <dl className="reports-order-summary">
      <div><dt>Sipariş</dt><dd>{data.totalCount.toLocaleString('tr-TR')}</dd></div>
      <div><dt>Sipariş Tutarı</dt><dd>{money(data.amount)}</dd></div>
      <div><dt>Kâr</dt><dd className={tone(data.profit)}>{money(data.profit)}</dd></div>
      <div><dt>Kâr Marjı</dt><dd>{percent(data.amount > 0 ? Math.round(data.profit / data.amount * 10000) / 100 : null)}</dd></div>
      {data.missingCost > 0 && <div className="is-warn"><dt>Maliyeti Eksik</dt><dd>{data.missingCost.toLocaleString('tr-TR')} sipariş</dd></div>}
    </dl>}
    {result.isLoading && <Busy />}
    {result.isError && <ErrorBox error={result.error} onRetry={() => void result.refetch()} />}
    {data && data.items.length === 0 && <p className="reports-empty">Bu dönemde sipariş yok.</p>}
    {data && data.items.length > 0 && <div className="reports-table-wrap"><table className="reports-table reports-order-table">
      <thead><tr><th scope="col">Sipariş Numarası</th>{header('date', 'Sipariş Tarihi')}{header('amount', 'Sipariş Tutarı')}{header('profit', 'Kâr Tutarı')}{header('profitOnCost', 'Kâr Oranı')}{header('margin', 'Kâr Marjı')}<th scope="col"><span className="reports-sr">Ayrıntı</span></th></tr></thead>
      <tbody>{data.items.map(item => <tr key={item.orderId}>
        <th scope="row"><span className="reports-order-number"><PlatformSquareMark code={item.platformCode} name={platformName(item.platformCode)} />{item.orderNumber}<CopyNumber value={item.orderNumber} />
          {item.microExport && <em className="reports-order-badge is-micro" title={item.storeFront ?? undefined}>Mikro</em>}
          {stateText[item.state] && <em className="reports-order-badge is-return">{stateText[item.state]}</em>}</span></th>
        <td>{formatIstanbul(item.orderedAt, { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })}</td>
        <td className="is-num">{money(item.amount)}</td>
        <td className={`is-num ${tone(item.profit)}`}>{item.profit === null ? 'Maliyet eksik' : money(item.profit)}</td>
        <td className={`is-num ${tone(item.profitOnCost)}`}>{percent(item.profitOnCost)}</td>
        <td className={`is-num ${tone(item.margin)}`}>{percent(item.margin)}</td>
        <td><button type="button" className="secondary reports-order-detail" onClick={() => setOpen(item)}>Detaylar</button></td>
      </tr>)}</tbody>
    </table></div>}
    {pageCount > 1 && <div className="reports-order-pager">
      <button type="button" className="secondary" disabled={page === 0} onClick={() => setPage(page - 1)}>Önceki</button>
      <span>Sayfa {page + 1} / {pageCount}</span>
      <button type="button" className="secondary" disabled={page + 1 >= pageCount} onClick={() => setPage(page + 1)}>Sonraki</button>
    </div>}
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
    <div className={`reports-order-result ${tone(order.profit)}`}>
      <span>Bu siparişteki net kârınız<small>Tüm giderler çıkarıldıktan sonra</small></span>
      <strong>{order.profit === null ? 'Maliyet eksik' : money(order.profit)}</strong>
    </div>
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
          <li><span><b>Sipariş Tutarı</b><small>Müşterinin ödediği, iadeler düşülmüş</small></span><strong>{money(order.amount)}</strong></li>
          <li><span><b>Ürün Maliyeti</b><small>Ürün ayarlarındaki maliyet</small></span><strong>{order.cost === null ? 'Eksik' : money(-order.cost)}</strong></li>
          {order.costs.map(cost => <li key={cost.key}>
            <span><b>{cost.label}{cost.estimated && <em>Tahmini</em>}</b>{cost.source && <small>{cost.source}</small>}</span>
            <strong>{money(-cost.amount)}</strong>
          </li>)}
        </ul>
      </section>
    </div>
  </Modal>
}
