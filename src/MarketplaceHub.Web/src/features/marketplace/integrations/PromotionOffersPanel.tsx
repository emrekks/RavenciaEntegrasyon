import { Fragment, useMemo, useRef, useState, type DragEvent, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { Busy, ErrorBox, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { compareOptionValues } from '../../catalog/shared'
import { formatIstanbul } from '../../../shared/format-date'
import { amountRange, FilterActions, FilterField, FilterRange, priceRange, ProductImage, rateText } from './CommissionTariffsPanel'
import { onCost, profitText, ProfitBadge, type UnitProfitCosts } from './unit-profit'
import { UiIcon } from '../../../shared/components'
import '../commission-tariffs.css'

// Trendyol's promotion offers the seller uploads (PromotionOfferContracts.cs): the Plus commission tariff, the advantage
// product label and flash products. Each offered price is shown with its commission and what it leaves after the
// commission and the product's cost, beside what the price leaves now. Nothing is sent to the marketplace.
export type PromotionKind = 'PLUS' | 'ADVANTAGE' | 'FLASH'
export type PromotionSummary = { kind: PromotionKind; fileName: string; uploadedAt: string; periodText: string | null; rowCount: number; matchedRowCount: number }
// Rate null: the commission at that price is not known (no commission tariff covers it). Remaining: the profit after the
// commission and costs (the product cost, cargo, service fee, withholding tax and net VAT). RemainingHigh: the most a
// model's variants leave when their costs differ (remaining and costs are then the dearest variant's).
export type PromotionPrice = { price: number; rate: number | null; commission: number | null; remaining: number | null; remainingHigh?: number | null; costs?: UnitProfitCosts | null }
type Product = { modelCode: string | null; productName: string | null; color: string | null; imageUrl: string | null; stock: number | null; promotions: string | null; category?: string | null; brand?: string | null }
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
  ADVANTAGE: 'Her etiketin üst fiyatı, o fiyatın düştüğü komisyon tarifesi bandıyla hesaplanır; komisyon tarifesi yüklü değilse oran bilinmez.',
  FLASH: 'Flaş fiyatında komisyon, tarifeye dahil ürünlerde flaşın başladığı haftanın tarifesindeki banttan, dahil olmayanlarda mevcut komisyondan okunur.'
}
const pageSize = 50

const money = (value: number) => `${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₺`
const signed = (value: number) => `${value < 0 ? '− ' : ''}${money(Math.abs(value))}`

// A price's profit with its share of the cost; a range when a model's variants' costs differ.
export function leavesText(price: PromotionPrice) {
  if (price.rate === null) return 'Komisyon bilinmiyor'
  if (price.remaining === null) return 'Maliyet girilmemiş'
  if (price.remainingHigh != null) return `${signed(price.remaining)} – ${signed(price.remainingHigh)} Kâr`
  return profitText(price.remaining, price.costs)
}

// The profit badge of a price, opening its calculation; the reason it is not known otherwise.
function PriceProfit({ price, title, tag }: { price: PromotionPrice; title?: string; tag?: string }) {
  if (price.rate === null) return <b className="unit-profit-badge is-unknown">Komisyon bilinmiyor</b>
  return <ProfitBadge price={price} title={title} tag={tag} missing="Maliyet girilmemiş" label={price.remainingHigh != null ? leavesText(price) : undefined} />
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
    <PriceProfit price={price} title={money(price.price)} tag={typeof label === 'string' ? label : undefined} />
  </div>
}

function ProductCell({ row, detail, code }: { row: Product; detail: ReactNode; code: ReactNode }) {
  return <div className="commission-product-cell">
    <ProductImage url={row.imageUrl} title={row.productName ?? '—'} initial={(row.productName ?? '?').slice(0, 1)} />
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

// The filter of a promotion page, as the commission tariff's: name, barcode, model code, a stock range, category and
// brand, and a column (an offered price) with a range of what it leaves in lira and as a share of the price.
export type OfferFilter = { name: string; barcode: string; model: string; minStock: string; maxStock: string; category: string; brand: string; column: string; minProfit: string; maxProfit: string; minShare: string; maxShare: string }
export const noOfferFilter: OfferFilter = { name: '', barcode: '', model: '', minStock: '', maxStock: '', category: '', brand: '', column: '', minProfit: '', maxProfit: '', minShare: '', maxShare: '' }
type OfferRow = Product & { barcode?: string; barcodes?: string[]; sellerSku?: string | null }

const number = (text: string) => {
  const cleaned = text.trim().replace(',', '.').replace('−', '-')
  const value = Number(cleaned)
  return cleaned && Number.isFinite(value) ? value : null
}
const has = (value: string | null | undefined, term: string) => !term.trim() || (value ?? '').toLocaleLowerCase('tr').includes(term.trim().toLocaleLowerCase('tr'))

// Whether a row passes the filter; prices(row, column) are the offered prices of the picked column (a model's sizes may
// leave different amounts: the least counts). A profit range needs a known commission and cost.
export function keepOffer<T extends OfferRow>(row: T, filter: OfferFilter, prices: (row: T, column: string) => PromotionPrice[]): boolean {
  if (!has(row.productName, filter.name) || !has(row.modelCode, filter.model)) return false
  if (filter.barcode.trim() && ![row.barcode, row.sellerSku, ...(row.barcodes ?? [])].some(code => code && has(code, filter.barcode))) return false
  if (filter.category && row.category !== filter.category) return false
  if (filter.brand && row.brand !== filter.brand) return false
  const minStock = number(filter.minStock), maxStock = number(filter.maxStock)
  if ((minStock !== null || maxStock !== null) && (row.stock == null || (minStock !== null && row.stock < minStock) || (maxStock !== null && row.stock > maxStock))) return false
  const ranges = [number(filter.minProfit), number(filter.maxProfit), number(filter.minShare), number(filter.maxShare)] as const
  if (!filter.column || ranges.every(value => value === null)) return true
  const [minProfit, maxProfit, minShare, maxShare] = ranges
  return prices(row, filter.column).some(price => {
    if (price.remaining === null || price.rate === null) return false
    const share = onCost(price.remaining, price.costs)
    return (minProfit === null || price.remaining >= minProfit) && (maxProfit === null || price.remaining <= maxProfit)
      && (minShare === null || (share !== null && share >= minShare)) && (maxShare === null || (share !== null && share <= maxShare))
  })
}

// The filter panel: the fields are typed first and applied with Filtrele (Enter too); Temizle empties them.
function OfferFilters({ value, rows, columns, onApply }: { value: OfferFilter; rows: OfferRow[]; columns: Array<[string, string]>; onApply: (filter: OfferFilter) => void }) {
  const [draft, setDraft] = useState(value)
  const set = (key: keyof OfferFilter) => (event: { target: { value: string } }) => setDraft(current => ({ ...current, [key]: event.target.value }))
  const categories = useMemo(() => [...new Set(rows.flatMap(row => row.category ? [row.category] : []))].sort((a, b) => a.localeCompare(b, 'tr')), [rows])
  const brands = useMemo(() => [...new Set(rows.flatMap(row => row.brand ? [row.brand] : []))].sort((a, b) => a.localeCompare(b, 'tr')), [rows])
  const apply = (event?: FormEvent) => { event?.preventDefault(); onApply(draft) }
  return <form className="commission-filters" onSubmit={apply} aria-label="Ürün filtreleri">
    <FilterField label="Ürün Adı"><input type="search" value={draft.name} onChange={set('name')} /></FilterField>
    <FilterField label="Barkod"><input type="search" value={draft.barcode} onChange={set('barcode')} /></FilterField>
    <FilterField label="Model Kodu"><input type="search" value={draft.model} onChange={set('model')} /></FilterField>
    <FilterRange label="Stok" inputMode="numeric" min={draft.minStock} max={draft.maxStock} onMin={set('minStock')} onMax={set('maxStock')} minLabel="En az stok" maxLabel="En çok stok" />
    <FilterField label="Kategori"><select value={draft.category} onChange={set('category')} disabled={!categories.length}>
      <option value="">Tümü</option>{categories.map(item => <option key={item} value={item}>{item}</option>)}
    </select></FilterField>
    <FilterField label="Marka"><select value={draft.brand} onChange={set('brand')} disabled={!brands.length}>
      <option value="">Tümü</option>{brands.map(item => <option key={item} value={item}>{item}</option>)}
    </select></FilterField>
    <FilterField label="Kolon Seçimi" className="commission-filter-band"><select value={draft.column} onChange={set('column')}>
      <option value="">Seçilmedi</option>{columns.map(([key, label]) => <option key={key} value={key}>{label}</option>)}
    </select></FilterField>
    {draft.column && <div className="commission-filter-profit">
      <FilterRange label="Kâr Tutarı (₺)" inputMode="decimal" min={draft.minProfit} max={draft.maxProfit} onMin={set('minProfit')} onMax={set('maxProfit')} minLabel="En az kâr tutarı (₺)" maxLabel="En çok kâr tutarı (₺)" />
      <FilterRange label="Kâr Oranı (%)" inputMode="decimal" min={draft.minShare} max={draft.maxShare} onMin={set('minShare')} onMax={set('maxShare')} minLabel="En az kâr oranı (%)" maxLabel="En çok kâr oranı (%)" />
    </div>}
    {rows.length > 0 && !categories.length && !brands.length && <p className="commission-filter-note">Kategori ve marka bu dosyada okunmadı: dosya önceden yüklendi. Aynı dosyayı yeniden yüklediğinizde dolar.</p>}
    <FilterActions onClear={() => { setDraft(noOfferFilter); onApply(noOfferFilter) }} />
  </form>
}

// The filter and a page of 50 model groups over a list.
function useGroupedListing<T extends OfferRow>(rows: T[], prices: (row: T, column: string) => PromotionPrice[], keyOf: (row: T) => string) {
  const [filter, setFilter] = useState<OfferFilter>(noOfferFilter)
  const [page, setPage] = useState(0)
  const shown = useMemo(() => rows.filter(row => keepOffer(row, filter, prices)), [rows, filter, prices])
  const groups = useMemo(() => groupOffers(shown, keyOf), [shown, keyOf])
  const pageCount = Math.max(1, Math.ceil(groups.length / pageSize))
  const current = Math.min(page, pageCount - 1)
  return { filter, shown, groups: groups.slice(current * pageSize, current * pageSize + pageSize), page: current, pageCount, onFilter: (value: OfferFilter) => { setFilter(value); setPage(0) }, onPage: setPage }
}
type Listing = { page: number; pageCount: number; onPage: (page: number) => void }

// The panel and the line of counts over a table.
function ListingHead<T extends OfferRow>({ listing, rows, columns, summary }: { listing: { filter: OfferFilter; onFilter: (filter: OfferFilter) => void }; rows: T[]; columns: Array<[string, string]>; summary: string }) {
  return <>
    <OfferFilters key={JSON.stringify(listing.filter)} value={listing.filter} rows={rows} columns={columns} onApply={listing.onFilter} />
    <div className="commission-plan-bar"><span className="commission-plan-count" role="status">{summary}</span></div>
  </>
}

// A model's prices in one card: the range of its sizes' prices, rates and what they leave.
function RangeCard({ prices, label }: { prices: PromotionPrice[]; label?: ReactNode }) {
  const rates = [...new Set(prices.flatMap(price => price.rate === null ? [] : [price.rate]))]
  const lefts = prices.flatMap(price => price.remaining === null ? [] : [price.remaining, ...(price.remainingHigh != null ? [price.remainingHigh] : [])])
  const unknown = rates.length === 0 || lefts.length === 0
  return <div className={`commission-choice is-static is-model${unknown ? ' is-unknown' : Math.min(...lefts) < 0 ? ' is-loss' : ''}`}>
    <strong>{priceRange(prices.map(price => price.price))}</strong>
    {label && <small>{label}</small>}
    <span>{rates.length === 0 ? 'Komisyon oranı bilinmiyor' : rates.length === 1 ? `${rateText(rates[0])} komisyon` : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))} komisyon`}</span>
    {(() => {
      // The badge opens the price leaving the least.
      const least = prices.filter(price => price.remaining !== null && price.rate !== null).sort((a, b) => a.remaining! - b.remaining!)[0]
      if (rates.length === 0) return <b className="unit-profit-badge is-unknown">Komisyon bilinmiyor</b>
      return <ProfitBadge price={least ?? prices[0]} missing="Maliyet girilmemiş" label={lefts.length && Math.min(...lefts) !== Math.max(...lefts) ? `${amountRange(lefts)} Kâr` : undefined}
        aside={lefts.length && Math.min(...lefts) !== Math.max(...lefts) ? 'En az kâr bırakan beden' : undefined} />
    })()}
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
    if (rows.length === 1) return <tr key={key}><td className="commission-expand" /><th scope="row"><ProductCell row={rows[0]} detail={<>{modelLine(rows[0])} · {label.title(rows[0])}</>} code={label.code(rows[0])} /></th>{cells(rows[0])}</tr>
    const first = rows.find(row => row.imageUrl) ?? rows[0]
    const open = opened.has(key)
    return <Fragment key={key}>
      <tr className="is-model">
        <td className="commission-expand"><button type="button" className="commission-model-toggle" aria-expanded={open} aria-label={`${rows.length.toLocaleString('tr-TR')} ${label.unit} ${open ? 'gizle' : 'göster'}`}
          onClick={() => setOpened(current => { const next = new Set(current); if (open) next.delete(key); else next.add(key); return next })}><UiIcon name="chevronRight" size={16} /></button></td>
        <th scope="row"><ProductCell row={first} detail={modelLine(first)} code={<span className="commission-model-count">{rows.length.toLocaleString('tr-TR')} {label.unit}</span>} /></th>
        {groupCells(rows)}
      </tr>
      {open && rows.map(row => <tr key={label.key(row)} className="is-variant">
        <td className="commission-expand" />
        <th scope="row"><span className="commission-variant-cell"><span className="commission-product">{label.title(row)}</span><small>{label.code(row)}</small></span></th>
        {cells(row)}
      </tr>)}
    </Fragment>
  })}</>
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

const plusColumns: Array<[string, string]> = [['plus', 'Plus Teklifi']]
export const plusPrices = (row: PlusOffer) => row.plus ? [row.plus] : []

function PlusTable({ rows }: { rows: PlusOffer[] }) {
  const sorted = useMemo(() => byModel(rows, row => row.size), [rows])
  const listing = useGroupedListing(sorted, plusPrices, byBarcode)
  const gains = listing.shown.filter(row => row.gain !== null && row.gain > 0).length
  const losses = listing.shown.filter(row => row.gain !== null && row.gain <= 0).length
  return <div className="commission-rows">
    <ListingHead listing={listing} rows={rows} columns={plusColumns}
      summary={`${listing.shown.length.toLocaleString('tr-TR')} ürün · Plus’ta ${gains.toLocaleString('tr-TR')} ürün daha çok, ${losses.toLocaleString('tr-TR')} ürün daha az bırakıyor`} />
    <div className="commission-table-wrap"><table className="commission-table is-plan promotion-table">
      <thead><tr><th scope="col" className="commission-expand" aria-label="Bedenler" /><th scope="col">Ürün</th><th scope="col" className="commission-stock">Stok</th><th scope="col">Güncel Fiyat</th><th scope="col">Plus Teklifi</th><th scope="col">Fark</th></tr></thead>
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

// Trendyol's names of the three advantage labels, one star to three.
export const starNames: Record<number, string> = { 1: 'Avantajlı Ürün', 2: 'Çok Avantajlı Ürün', 3: 'Süper Avantajlı Ürün' }
export const starText = (stars: number) => stars === 0 ? 'Etiket yok' : `${'★'.repeat(stars)} ${starNames[stars]}`
const advantageColumns: Array<[string, string]> = [1, 2, 3].map(star => [String(star), starNames[star]])
export const advantagePrices = (row: AdvantageOffer, column: string) => row.stars.filter(item => String(item.star) === column).map(item => item.price)

function AdvantageTable({ rows }: { rows: AdvantageOffer[] }) {
  const sorted = useMemo(() => byModel(rows, row => row.size), [rows])
  const listing = useGroupedListing(sorted, advantagePrices, byBarcode)
  const unstarred = listing.shown.filter(row => row.currentStars === 0).length
  const stars = (values: number[]) => {
    const distinct = [...new Set(values)].sort()
    return <small className={`promotion-stars is-${Math.max(...distinct)}`}>{distinct.length === 1 ? starText(distinct[0]) : distinct.map(star => starNames[star] ?? 'Etiket yok').join(' – ')}</small>
  }
  return <div className="commission-rows">
    <ListingHead listing={listing} rows={rows} columns={advantageColumns}
      summary={`${listing.shown.length.toLocaleString('tr-TR')} ürün · şu an ${(listing.shown.length - unstarred).toLocaleString('tr-TR')} ürün etiketli, ${unstarred.toLocaleString('tr-TR')} ürün etiketsiz`} />
    <div className="commission-table-wrap"><table className="commission-table is-plan promotion-table">
      <thead><tr><th scope="col" className="commission-expand" aria-label="Bedenler" /><th scope="col">Ürün</th><th scope="col" className="commission-stock">Stok</th><th scope="col">Güncel Fiyat</th>{[1, 2, 3].map(star => <th scope="col" key={star}>{starNames[star]}</th>)}</tr></thead>
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
export const flashPrices = (row: FlashOffer, column: string) => row.slots.filter(slot => slot.slot === column).map(slot => slot.price)

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
  const listing = useGroupedListing(sorted, flashPrices, flashKey)
  const slots = ['24 Saat', ...(rows.some(row => row.slots.some(slot => slot.slot === '3 Saat')) ? ['3 Saat'] : [])]
  const losses = listing.shown.filter(losesInFlash).length
  return <div className="commission-rows">
    <ListingHead listing={listing} rows={rows} columns={slots.map(name => [name, `${name} Flaş`] as [string, string])}
      summary={`${listing.shown.length.toLocaleString('tr-TR')} flaş teklifi · ${losses.toLocaleString('tr-TR')} teklif flaş fiyatında zarar ediyor`} />
    <div className="commission-table-wrap"><table className="commission-table is-plan promotion-table">
      <thead><tr><th scope="col" className="commission-expand" aria-label="Bedenler" /><th scope="col">Ürün</th><th scope="col" className="commission-stock">Stok</th><th scope="col">Güncel Fiyat</th>{slots.map(name => <th scope="col" key={name}>{name} Flaş</th>)}</tr></thead>
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
