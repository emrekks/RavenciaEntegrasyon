import { Fragment, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { Busy, ErrorBox, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { compareOptionValues } from '../../catalog/shared'
import { formatIstanbul } from '../../../shared/format-date'
import { thumbnailUrl } from '../../../shared/product-thumbnail'
import { amountRange, priceRange, rateText } from './CommissionTariffsPanel'
import { UiIcon } from '../../../shared/components'
import '../commission-tariffs.css'

// Trendyol's promotion offers the seller uploads (PromotionOfferContracts.cs): the Plus commission tariff, the advantage
// product label and flash products. Each offered price is shown with its commission and what it leaves after the
// commission and the product's cost, beside what the price leaves now. Nothing is sent to the marketplace.
export type PromotionKind = 'PLUS' | 'ADVANTAGE' | 'FLASH'
export type PromotionSummary = { kind: PromotionKind; fileName: string; uploadedAt: string; periodText: string | null; rowCount: number; matchedRowCount: number }
// Rate null: the commission at that price is not known (no commission tariff covers it). RemainingHigh: the most a
// model's variants leave when their costs differ (remaining is then the least).
export type PromotionPrice = { price: number; rate: number | null; commission: number | null; remaining: number | null; remainingHigh?: number | null }
type Product = { modelCode: string | null; productName: string | null; color: string | null; imageUrl: string | null; stock: number | null; promotions: string | null }
export type PlusOffer = Product & { barcode: string; sellerSku: string | null; size: string | null; costPrice: number | null; now: PromotionPrice | null; plus: PromotionPrice | null; gain: number | null }
export type AdvantageStar = { star: number; min: number | null; max: number; price: PromotionPrice }
export type AdvantageOffer = Product & { barcode: string; size: string | null; costPrice: number | null; inTariff: boolean | null; currentStars: number; now: PromotionPrice | null; stars: AdvantageStar[] }
export type FlashSlot = { slot: string; start: string | null; end: string | null; price: PromotionPrice }
export type FlashOffer = Product & { barcodes: string[]; costLow: number | null; costHigh: number | null; inTariff: boolean | null; campaign: boolean | null; now: PromotionPrice | null; slots: FlashSlot[] }
type Detail = { offer: PromotionSummary; plus: PlusOffer[] | null; advantage: AdvantageOffer[] | null; flash: FlashOffer[] | null }
type UploadResult = { offer: PromotionSummary; skippedRows: number; warnings: string[] }
type PanelMessage = { kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] }

export const promotionTitles: Record<PromotionKind, string> = { PLUS: 'Plus Komisyon Tarifesi', ADVANTAGE: 'Avantajlı Ürün Etiketi', FLASH: 'Flash Ürünler' }
const promotionHelp: Record<PromotionKind, string> = {
  PLUS: 'Plus fiyat üst limitinde Trendyol’un önerdiği komisyonla ürünün bıraktığı tutar, bugünkü fiyatın bıraktığıyla karşılaştırılır.',
  ADVANTAGE: 'Her yıldızın üst fiyatı, o fiyatın düştüğü komisyon tarifesi bandıyla hesaplanır; komisyon tarifesi yüklü değilse oran bilinmez.',
  FLASH: 'Flaş fiyatında komisyon, tarifeye dahil ürünlerde flaşın başladığı haftanın tarifesindeki banttan, dahil olmayanlarda mevcut komisyondan okunur.'
}
const pageSize = 50

const money = (value: number) => `${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₺`
const signed = (value: number) => `${value < 0 ? '− ' : ''}${money(Math.abs(value))}`

// What a price leaves, with its share of the price; a range when a model's variants' costs differ.
export function leavesText(price: PromotionPrice) {
  if (price.rate === null) return 'Komisyon bilinmiyor'
  if (price.remaining === null) return 'Maliyet girilmemiş'
  if (price.remainingHigh != null) return `${signed(price.remaining)} – ${signed(price.remainingHigh)} kalır`
  const share = price.price > 0 ? ` · %${(price.remaining / price.price * 100).toLocaleString('tr-TR', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}` : ''
  return `${signed(price.remaining)} kalır${share}`
}

// Rows by model, colour and size (S, M, L, XL …), as the commission tariff lists them.
export function byModel<T extends { modelCode: string | null; color: string | null; productName: string | null }>(rows: T[], size: (row: T) => string | null): T[] {
  return [...rows].sort((a, b) => (a.modelCode ?? a.productName ?? '').localeCompare(b.modelCode ?? b.productName ?? '', 'tr')
    || (a.color ?? '').localeCompare(b.color ?? '', 'tr') || compareOptionValues('Beden', size(a) ?? '', size(b) ?? ''))
}

// A row matches the search by its name, model code, seller SKU or one of its barcodes.
export function matches(row: Product & { barcode?: string; barcodes?: string[]; sellerSku?: string | null }, search: string) {
  const term = search.trim().toLocaleLowerCase('tr')
  if (!term) return true
  return [row.productName, row.modelCode, row.barcode, row.sellerSku, ...(row.barcodes ?? [])].some(value => value?.toLocaleLowerCase('tr').includes(term))
}

function PriceCard({ price, label, highlight }: { price: PromotionPrice; label?: ReactNode; highlight?: boolean }) {
  const loss = price.remaining !== null && price.remaining < 0
  const unknown = price.rate === null || price.remaining === null
  return <div className={`commission-choice is-static${highlight ? ' is-picked' : ''}${loss ? ' is-loss' : ''}${unknown ? ' is-unknown' : ''}`}>
    <strong>{money(price.price)}</strong>
    {label && <small>{label}</small>}
    <span>{price.rate === null ? 'Komisyon oranı bilinmiyor' : `${rateText(price.rate)} komisyon · ${money(price.commission ?? 0)}`}</span>
    <b>{leavesText(price)}</b>
  </div>
}

function ProductCell({ row, detail, code }: { row: Product; detail: ReactNode; code: ReactNode }) {
  return <div className="commission-product-cell">
    {row.imageUrl ? <img src={thumbnailUrl(row.imageUrl)} alt="" loading="lazy" className="commission-product-image" /> : <i className="commission-product-image" aria-hidden="true">{(row.productName ?? '?').slice(0, 1)}</i>}
    <span>
      <span className="promotion-product-name">{row.productName ?? '—'}</span>
      <small>{detail}</small>
      <small className="promotion-code">{code}</small>
    </span>
  </div>
}

const none = <span className="commission-choice-none">—</span>
const modelLine = (row: Product, size?: string | null) => [row.modelCode, row.color, size].filter(Boolean).join(' · ') || '—'

export function PromotionOffersPanel({ connectionId, kind }: { connectionId: string; kind: PromotionKind }) {
  const client = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [message, setMessage] = useState<PanelMessage | null>(null)
  const [confirm, confirmDialog] = useConfirmDialog()
  const offers = useQuery({ queryKey: ['promotion-offers', connectionId], queryFn: () => hubApi<PromotionSummary[]>(`/connections/${connectionId}/promotion-offers`) })
  const summary = offers.data?.find(offer => offer.kind === kind) ?? null
  const detail = useQuery({ queryKey: ['promotion-offer', connectionId, kind, summary?.uploadedAt], queryFn: () => hubApi<Detail>(`/connections/${connectionId}/promotion-offers/${kind}`), enabled: summary !== null })
  const upload = useMutation({
    mutationFn: (file: File) => {
      const body = new FormData()
      body.append('file', file)
      return hubApi<UploadResult>(`/connections/${connectionId}/promotion-offers`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('promotion-offer') }, body })
    },
    onSuccess: async result => {
      const other = result.offer.kind !== kind ? ` Bu bir ${promotionTitles[result.offer.kind]} dosyası; o sayfada gösteriliyor.` : ''
      setMessage({ kind: result.skippedRows || other ? 'info' : 'success', text: `${promotionTitles[result.offer.kind]}: ${result.offer.rowCount.toLocaleString('tr-TR')} satır yüklendi${result.skippedRows ? `, ${result.skippedRows.toLocaleString('tr-TR')} satır okunamadı` : ''}.${other}`, warnings: result.warnings })
      await client.invalidateQueries({ queryKey: ['promotion-offers', connectionId] })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Dosya yüklenemedi.' })
  })
  const remove = useMutation({
    mutationFn: () => hubApi<boolean>(`/connections/${connectionId}/promotion-offers/${kind}`, { method: 'DELETE', headers: { 'Idempotency-Key': newIdempotencyKey('promotion-offer-delete') } }),
    onSuccess: async () => { setMessage({ kind: 'success', text: 'Dosya silindi.' }); await client.invalidateQueries({ queryKey: ['promotion-offers', connectionId] }) },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Dosya silinemedi.' })
  })
  const choose = (file: File | undefined) => {
    if (!file) return
    if (!file.name.toLowerCase().endsWith('.xlsx')) { setMessage({ kind: 'error', text: 'Yalnız Trendyol’dan indirilen .xlsx dosyası yüklenebilir.' }); return }
    setMessage(null)
    upload.mutate(file)
  }
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  const askDelete = async () => {
    if (!summary || !(await confirm({ title: 'Dosya Silinsin mi?', message: `${promotionTitles[kind]} dosyası (${summary.fileName}) ve ${summary.rowCount.toLocaleString('tr-TR')} satırı silinecek.`, confirmLabel: 'Dosyayı Sil', destructive: true }))) return
    remove.mutate()
  }

  return <section className="panel commission-tariffs promotion-offers" aria-labelledby={`promotion-${kind}-title`}>
    <div className="cs-sec-head"><h3 id={`promotion-${kind}-title`}>{promotionTitles[kind]}</h3><p>{promotionHelp[kind]} Kargo, hizmet bedeli ve stopaj ürün bazında bilinmediği için düşülmez; pazaryerine hiçbir şey gönderilmez.</p></div>
    <div className={`commission-dropzone${dragging ? ' is-dragging' : ''}`} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
      <strong>{upload.isPending ? 'Dosya okunuyor…' : `${promotionTitles[kind]} dosyasını buraya bırakın`}</strong>
      <span>Trendyol satıcı panelinden indirilen .xlsx dosyası · en fazla 10 MiB · yeni dosya öncekinin yerine geçer</span>
      <button type="button" className="secondary" disabled={upload.isPending} onClick={() => input.current?.click()}>Dosya Seç</button>
      <input ref={input} type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" hidden onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
    </div>
    {message && <div className={`commission-message is-${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}><p>{message.text}</p>{message.warnings?.length ? <ul>{message.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul> : null}</div>}
    {offers.isLoading && <Busy />}
    {offers.isError && <ErrorBox error={offers.error} onRetry={() => void offers.refetch()} />}
    {offers.data && !summary && <p className="cs-empty">Henüz {promotionTitles[kind]} dosyası yüklenmedi.</p>}
    {summary && <div className="commission-period is-on">
      <div className="commission-period-main">
        <span className="commission-period-title">{summary.periodText ?? summary.fileName}</span>
        <small>{summary.rowCount.toLocaleString('tr-TR')} {kind === 'FLASH' ? 'model' : 'ürün'} · {summary.matchedRowCount.toLocaleString('tr-TR')} barkod panelde eşleşti · {summary.fileName} · {formatIstanbul(summary.uploadedAt, { dateStyle: 'medium', timeStyle: 'short' })}</small>
      </div>
      <button type="button" className="secondary commission-period-delete" disabled={remove.isPending} onClick={() => void askDelete()}>Sil</button>
    </div>}
    {summary && detail.isLoading && <Busy />}
    {detail.isError && <ErrorBox error={detail.error} onRetry={() => void detail.refetch()} />}
    {detail.data?.plus && <PlusTable rows={detail.data.plus} />}
    {detail.data?.advantage && <AdvantageTable rows={detail.data.advantage} />}
    {detail.data?.flash && <FlashTable rows={detail.data.flash} />}
    {confirmDialog}
  </section>
}

// A product's rows by model and colour (a row without a model code alone), in the order they come.
export function groupOffers<T extends { modelCode: string | null; color: string | null }>(rows: T[], keyOf: (row: T) => string): Array<[string, T[]]> {
  const groups = new Map<string, T[]>()
  for (const row of rows) {
    const model = row.modelCode?.trim()
    const key = model ? `${model}|${row.color?.trim() ?? ''}` : keyOf(row)
    groups.set(key, [...(groups.get(key) ?? []), row])
  }
  return [...groups]
}

// Search, a filter and a page of 50 model groups over a list.
function useGroupedListing<T extends { modelCode: string | null; color: string | null }>(rows: T[], keep: (row: T, search: string, filter: string) => boolean, keyOf: (row: T) => string) {
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState('')
  const [page, setPage] = useState(0)
  const shown = useMemo(() => rows.filter(row => keep(row, search, filter)), [rows, search, filter, keep])
  const groups = useMemo(() => groupOffers(shown, keyOf), [shown, keyOf])
  const pageCount = Math.max(1, Math.ceil(groups.length / pageSize))
  const current = Math.min(page, pageCount - 1)
  return {
    search, filter, shown, groups: groups.slice(current * pageSize, current * pageSize + pageSize), page: current, pageCount, items: shown,
    onSearch: (value: string) => { setSearch(value); setPage(0) }, onFilter: (value: string) => { setFilter(value); setPage(0) }, onPage: setPage
  }
}
type Listing = { search: string; filter: string; page: number; pageCount: number; onSearch: (value: string) => void; onFilter: (value: string) => void; onPage: (page: number) => void }

// A model's prices in one card: the range of its sizes' prices, rates and what they leave.
function RangeCard({ prices, label }: { prices: PromotionPrice[]; label?: ReactNode }) {
  const rates = [...new Set(prices.flatMap(price => price.rate === null ? [] : [price.rate]))]
  const lefts = prices.flatMap(price => price.remaining === null ? [] : [price.remaining, ...(price.remainingHigh != null ? [price.remainingHigh] : [])])
  const unknown = rates.length === 0 || lefts.length === 0
  return <div className={`commission-choice is-static is-model${unknown ? ' is-unknown' : Math.min(...lefts) < 0 ? ' is-loss' : ''}`}>
    <strong>{priceRange(prices.map(price => price.price))}</strong>
    {label && <small>{label}</small>}
    <span>{rates.length === 0 ? 'Komisyon oranı bilinmiyor' : rates.length === 1 ? `${rateText(rates[0])} komisyon` : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))} komisyon`}</span>
    <b>{rates.length === 0 ? 'Komisyon bilinmiyor' : lefts.length ? `${amountRange(lefts)} kalır` : 'Maliyet girilmemiş'}</b>
  </div>
}

// The stock and the current price, as plain figures: the price alone, or the range of a model's sizes.
const stockCell = (stocks: Array<number | null>) => <td className="commission-stock">{stocks.some(stock => stock != null) ? stocks.reduce<number>((sum, stock) => sum + (stock ?? 0), 0).toLocaleString('tr-TR', { maximumFractionDigits: 0 }) : '—'}</td>
const priceCell = (prices: Array<number | null | undefined>, extra?: ReactNode) => {
  const known = prices.filter((price): price is number => price != null)
  return <td><span className="commission-price">{known.length ? priceRange(known) : '—'}</span>{extra}</td>
}

// How a row of a model is told apart from the model's other rows (a size, or a flash offer's dates).
type RowLabel<T> = { key: (row: T) => string; title: (row: T) => ReactNode; code: (row: T) => ReactNode; unit: string }

// The rows of a page: a model with more rows is one row (its picture, name, colour and its rows' ranges) that opens
// onto them; a model of one row is that row.
function GroupedRows<T extends Product>({ groups, label, cells, groupCells }: {
  groups: Array<[string, T[]]>; label: RowLabel<T>; cells: (row: T) => ReactNode; groupCells: (rows: T[]) => ReactNode
}) {
  const [opened, setOpened] = useState<Set<string>>(() => new Set())
  return <>{groups.map(([key, rows]) => {
    if (rows.length === 1) return <tr key={key}><th scope="row"><ProductCell row={rows[0]} detail={<>{modelLine(rows[0])} · {label.title(rows[0])}</>} code={label.code(rows[0])} /></th>{cells(rows[0])}</tr>
    const first = rows.find(row => row.imageUrl) ?? rows[0]
    const open = opened.has(key)
    return <Fragment key={key}>
      <tr className="is-model">
        <th scope="row"><ProductCell row={first} detail={modelLine(first)} code={
          <button type="button" className="commission-model-toggle" aria-expanded={open} onClick={() => setOpened(current => { const next = new Set(current); if (open) next.delete(key); else next.add(key); return next })}>
            <UiIcon name={open ? 'chevronDown' : 'chevronRight'} size={14} />{rows.length.toLocaleString('tr-TR')} {label.unit}
          </button>} /></th>
        {groupCells(rows)}
      </tr>
      {open && rows.map(row => <tr key={label.key(row)} className="is-variant">
        <th scope="row"><span className="commission-variant-cell"><span className="commission-product">{label.title(row)}</span><small>{label.code(row)}</small></span></th>
        {cells(row)}
      </tr>)}
    </Fragment>
  })}</>
}

function ListingBar({ listing, filters, summary }: { listing: Listing; filters: Array<[string, string]>; summary: string }) {
  return <div className="commission-plan-bar">
    <input className="commission-search" type="search" value={listing.search} onChange={event => listing.onSearch(event.target.value)} placeholder="Barkod, model kodu ya da ürün ara" aria-label="Ürün ara" />
    <label className="commission-plan-field"><span>Göster</span>
      <select value={listing.filter} onChange={event => listing.onFilter(event.target.value)}>{filters.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
    </label>
    <span className="commission-plan-count" role="status">{summary}</span>
  </div>
}

function Pager({ listing }: { listing: Listing }) {
  if (listing.pageCount <= 1) return null
  return <div className="commission-pager">
    <button type="button" className="secondary" disabled={listing.page === 0} onClick={() => listing.onPage(listing.page - 1)}>Önceki</button>
    <span>Sayfa {listing.page + 1} / {listing.pageCount}</span>
    <button type="button" className="secondary" disabled={listing.page + 1 >= listing.pageCount} onClick={() => listing.onPage(listing.page + 1)}>Sonraki</button>
  </div>
}

// A size of a Plus or advantage file: its size, barcode and cost.
const sizeLabel: RowLabel<{ barcode: string; size: string | null; costPrice: number | null }> = {
  key: row => row.barcode, unit: 'beden',
  title: row => row.size ?? row.barcode,
  code: row => <>{row.barcode} · {row.costPrice !== null ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</>
}
const byBarcode = (row: { barcode: string }) => row.barcode

// Which Plus rows a filter keeps: Plus leaving more than now, less, or no cost to tell.
export function keepPlus(row: PlusOffer, search: string, filter: string) {
  if (!matches(row, search)) return false
  if (filter === 'gain') return row.gain !== null && row.gain > 0
  if (filter === 'loss') return row.gain !== null && row.gain <= 0
  if (filter === 'nocost') return row.costPrice === null
  return true
}

function PlusTable({ rows }: { rows: PlusOffer[] }) {
  const sorted = useMemo(() => byModel(rows, row => row.size), [rows])
  const listing = useGroupedListing(sorted, keepPlus, byBarcode)
  const gains = rows.filter(row => row.gain !== null && row.gain > 0).length
  const losses = rows.filter(row => row.gain !== null && row.gain <= 0).length
  return <div className="commission-rows">
    <ListingBar listing={listing} filters={[['', 'Tüm ürünler'], ['gain', 'Plus’ta daha çok kalan'], ['loss', 'Plus’ta daha az kalan'], ['nocost', 'Maliyeti girilmemiş']]}
      summary={`${listing.shown.length.toLocaleString('tr-TR')} ürün · Plus’ta ${gains.toLocaleString('tr-TR')} ürün daha çok, ${losses.toLocaleString('tr-TR')} ürün daha az bırakıyor`} />
    <div className="commission-table-wrap"><table className="commission-table is-plan promotion-table">
      <thead><tr><th>Ürün</th><th>Stok</th><th>Güncel Fiyat</th><th>Plus Teklifi</th><th>Fark</th></tr></thead>
      <tbody><GroupedRows groups={listing.groups} label={sizeLabel} cells={row => <>
        {stockCell([row.stock])}
        {priceCell([row.now?.price])}
        <td>{row.plus ? <PriceCard price={row.plus} label="Plus fiyat üst limiti" highlight={row.gain !== null && row.gain > 0} /> : none}</td>
        <td>{row.gain === null ? none : <em className={`promotion-gain ${row.gain > 0 ? 'is-up' : 'is-down'}`}>{row.gain > 0 ? '+ ' : ''}{signed(row.gain)}</em>}</td>
      </>} groupCells={group => {
        const gains = group.flatMap(row => row.gain === null ? [] : [row.gain])
        return <>
          {stockCell(group.map(row => row.stock))}
          {priceCell(group.map(row => row.now?.price))}
          <td>{group.some(row => row.plus) ? <RangeCard prices={group.flatMap(row => row.plus ? [row.plus] : [])} label="Plus fiyat üst limiti" /> : none}</td>
          <td>{gains.length === 0 ? none : <em className={`promotion-gain ${Math.max(...gains) > 0 ? 'is-up' : 'is-down'}`}>{amountRange(gains)}</em>}</td>
        </>
      }} /></tbody>
    </table></div>
    <Pager listing={listing} />
  </div>
}

export const starText = (stars: number) => stars === 0 ? 'Yıldız yok' : `${'★'.repeat(stars)} ${stars} yıldız`

export function keepAdvantage(row: AdvantageOffer, search: string, filter: string) {
  return matches(row, search) && (filter === '' || String(row.currentStars) === filter)
}

function AdvantageTable({ rows }: { rows: AdvantageOffer[] }) {
  const sorted = useMemo(() => byModel(rows, row => row.size), [rows])
  const listing = useGroupedListing(sorted, keepAdvantage, byBarcode)
  const unstarred = rows.filter(row => row.currentStars === 0).length
  const stars = (values: number[]) => {
    const distinct = [...new Set(values)].sort()
    return <small className={`promotion-stars is-${Math.max(...distinct)}`}>{distinct.length === 1 ? starText(distinct[0]) : `${distinct.join('–')} yıldız`}</small>
  }
  return <div className="commission-rows">
    <ListingBar listing={listing} filters={[['', 'Tüm ürünler'], ['0', 'Yıldızsız'], ['1', '1 yıldızlı'], ['2', '2 yıldızlı'], ['3', '3 yıldızlı']]}
      summary={`${listing.shown.length.toLocaleString('tr-TR')} ürün · şu an ${(rows.length - unstarred).toLocaleString('tr-TR')} ürün etiketli, ${unstarred.toLocaleString('tr-TR')} ürün yıldızsız`} />
    <div className="commission-table-wrap"><table className="commission-table is-plan promotion-table">
      <thead><tr><th>Ürün</th><th>Stok</th><th>Güncel Fiyat</th><th>1 Yıldız</th><th>2 Yıldız</th><th>3 Yıldız</th></tr></thead>
      <tbody><GroupedRows groups={listing.groups} label={sizeLabel} cells={row => <>
        {stockCell([row.stock])}
        {priceCell([row.now?.price], stars([row.currentStars]))}
        {[1, 2, 3].map(star => {
          const offer = row.stars.find(item => item.star === star)
          return <td key={star}>{offer ? <PriceCard price={offer.price} label={offer.min !== null ? `${money(offer.min)} – ${money(offer.max)}` : `En fazla ${money(offer.max)}`} highlight={row.currentStars === star} /> : none}</td>
        })}
      </>} groupCells={group => <>
        {stockCell(group.map(row => row.stock))}
        {priceCell(group.map(row => row.now?.price), stars(group.map(row => row.currentStars)))}
        {[1, 2, 3].map(star => {
          const offers = group.flatMap(row => row.stars.filter(item => item.star === star))
          return <td key={star}>{offers.length ? <RangeCard prices={offers.map(item => item.price)} label={`En fazla ${priceRange(offers.map(item => item.max))}`} /> : none}</td>
        })}
      </>} /></tbody>
    </table></div>
    <Pager listing={listing} />
  </div>
}

const losesInFlash = (row: FlashOffer) => row.slots.some(slot => slot.price.remaining !== null && slot.price.remaining < 0)

export function keepFlash(row: FlashOffer, search: string, filter: string) {
  if (!matches(row, search)) return false
  if (filter === 'loss') return losesInFlash(row)
  if (filter === 'nocost') return row.costHigh === null
  return true
}

// A flash offer of a model's colour: its days (a colour can be offered on several), its sizes and cost.
const flashKey = (row: FlashOffer) => `${row.barcodes.join(',')}|${row.slots.map(slot => slot.start).join(',')}`
const flashDays = (row: FlashOffer) => [...new Set(row.slots.map(slot => slot.start?.slice(0, 10)).filter(Boolean))].join(', ') || 'Tarih yok'
const flashLabel: RowLabel<FlashOffer> = {
  key: flashKey, unit: 'flaş teklifi',
  title: flashDays,
  code: row => <>{row.barcodes.length} beden{row.campaign ? ' · Kampanyalı' : ''} · {row.costLow !== null && row.costHigh !== null ? `Maliyet ${row.costLow === row.costHigh ? money(row.costLow) : `${money(row.costLow)} – ${money(row.costHigh)}`}` : 'Maliyet eksik'}</>
}

function FlashTable({ rows }: { rows: FlashOffer[] }) {
  const sorted = useMemo(() => byModel(rows, () => null), [rows])
  const listing = useGroupedListing(sorted, keepFlash, flashKey)
  const slots = ['24 Saat', ...(rows.some(row => row.slots.some(slot => slot.slot === '3 Saat')) ? ['3 Saat'] : [])]
  const losses = rows.filter(losesInFlash).length
  return <div className="commission-rows">
    <ListingBar listing={listing} filters={[['', 'Tüm teklifler'], ['loss', 'Flaşta zarar eden'], ['nocost', 'Maliyeti eksik']]}
      summary={`${listing.shown.length.toLocaleString('tr-TR')} flaş teklifi · ${losses.toLocaleString('tr-TR')} teklif flaş fiyatında zarar ediyor`} />
    <div className="commission-table-wrap"><table className="commission-table is-plan promotion-table">
      <thead><tr><th>Ürün</th><th>Stok</th><th>Güncel Fiyat</th>{slots.map(name => <th key={name}>{name} Flaş</th>)}</tr></thead>
      <tbody><GroupedRows groups={listing.groups} label={flashLabel} cells={row => <>
        {stockCell([row.stock])}
        {priceCell([row.now?.price])}
        {slots.map(name => {
          const slot = row.slots.find(item => item.slot === name)
          return <td key={name}>{slot ? <PriceCard price={slot.price} label={[slot.start, slot.end].filter(Boolean).join(' – ') || undefined} /> : none}</td>
        })}
      </>} groupCells={group => <>
        {/* The colour's stock is the same on each of its offers: counted once. */}
        {stockCell([group[0].stock])}
        {priceCell(group.map(row => row.now?.price))}
        {slots.map(name => {
          const prices = group.flatMap(row => row.slots.filter(item => item.slot === name).map(item => item.price))
          return <td key={name}>{prices.length ? <RangeCard prices={prices} label={`${prices.length} gün`} /> : none}</td>
        })}
      </>} /></tbody>
    </table></div>
    <Pager listing={listing} />
  </div>
}
