import { Fragment, useEffect, useRef, useState, type DragEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubFile } from '../../../shared/api'
import { Busy, ErrorBox, UiIcon, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { compareOptionValues } from '../../catalog/shared'
import { formatIstanbul } from '../../../shared/format-date'
import { thumbnailUrl } from '../../../shared/product-thumbnail'
import '../commission-tariffs.css'

// The marketplaces whose commission tariff export the panel reads (CommissionTariffPlatforms on the server).
export const commissionTariffPlatforms = new Set(['TRENDYOL'])

export type CommissionBand = { tier: number; min: number | null; max: number | null; rate: number }
export type CommissionTariff = { id: string; platformCode: string; periodStart: string; periodEnd: string; periodText: string; fileName: string; uploadedAt: string; rowCount: number; matchedRowCount: number; isCurrent: boolean }
export type CommissionTariffRow = {
  id: string; barcode: string; sellerSku: string | null; modelCode: string | null; productName: string | null; size: string | null
  basisPrice: number | null; currentSalePrice: number | null; currentRate: number | null; currentTier: number | null; currentBandRate: number | null
  bands: CommissionBand[]; nextLowerBand: CommissionBand | null; matchesPanel: boolean
  // The product cost entered on the panel, each band as a price to pick, the band that leaves the most and the
  // panel product's picture (absent in an older server's answer).
  costPrice?: number | null; choices?: CommissionChoice[] | null; recommendedTier?: number | null; imageUrl?: string | null
  // The panel variant's colour ("Bej"); a model's rows are grouped by it.
  color?: string | null
}
// A band as a price the seller can pick: its commission and what the price leaves after it and the product cost.
export type CommissionChoice = { tier: number; price: number; rate: number; commission: number; remaining: number | null }
type RowPage = { items: CommissionTariffRow[]; totalCount: number; page: number; pageSize: number }

// A row's own pick: a band, a price of the seller's own, or neither (the row left out of the Excel).
export type TariffPick = { tier: number | null; price: number | null }
// The pick for every row: one band ('tier'), or the band leaving the most money ('amount') or the largest share of its
// price ('rate'); with a least share (per cent) or amount (lira) a row whose pick leaves less is left out, with a most
// share or amount one whose pick leaves more.
export type TariffBulk = { kind: '' | 'tier' | 'amount' | 'rate'; tier: number; minRate: number | null; minAmount: number | null; maxRate?: number | null; maxAmount?: number | null }
export const noBulk: TariffBulk = { kind: '', tier: 1, minRate: null, minAmount: null, maxRate: null, maxAmount: null }

const share = (choice: CommissionChoice) => choice.remaining === null || choice.price <= 0 ? null : choice.remaining / choice.price

// The pick for every row in one row, worked out as the server's CommissionTariffChoices.Pick does.
export function bulkChoice(choices: CommissionChoice[], bulk: TariffBulk): CommissionChoice | null {
  const known = choices.filter(choice => choice.remaining !== null)
  const choice = bulk.kind === 'tier' ? choices.find(item => item.tier === bulk.tier) ?? null
    : bulk.kind === 'amount' ? [...known].sort((a, b) => (b.remaining ?? 0) - (a.remaining ?? 0) || a.rate - b.rate)[0] ?? null
    : bulk.kind === 'rate' ? [...known].filter(item => item.price > 0).sort((a, b) => (share(b) ?? 0) - (share(a) ?? 0) || a.rate - b.rate)[0] ?? null
    : null
  if (!choice) return null
  if (bulk.minRate !== null && (share(choice) === null || (share(choice) ?? 0) * 100 < bulk.minRate)) return null
  if (bulk.minAmount !== null && (choice.remaining === null || choice.remaining < bulk.minAmount)) return null
  if (bulk.maxRate != null && (share(choice) === null || (share(choice) ?? 0) * 100 > bulk.maxRate)) return null
  if (bulk.maxAmount != null && (choice.remaining === null || choice.remaining > bulk.maxAmount)) return null
  return choice
}

// The band a row counts as picked: its own pick, else the pick for every row; 'own' for a price of the seller's own.
export function pickedTier(row: Pick<CommissionTariffRow, 'choices'>, pick: TariffPick | undefined, bulk: TariffBulk): number | 'own' | null {
  if (pick) return pick.price !== null ? 'own' : pick.tier
  return bulkChoice(row.choices ?? [], bulk)?.tier ?? null
}

// A price of the seller's own, worked out as the server does: the band it falls in, its commission and what it leaves.
export function ownChoice(row: Pick<CommissionTariffRow, 'bands' | 'costPrice'>, price: number): CommissionChoice | null {
  const rounded = Math.round(price * 100) / 100
  const band = row.bands.find(item => (item.min === null || rounded >= item.min) && (item.max === null || rounded <= item.max))
  if (!band || rounded <= 0) return null
  const commission = Math.round(rounded * band.rate) / 100
  return { tier: band.tier, price: rounded, rate: band.rate, commission, remaining: row.costPrice ? Math.round((rounded - commission - row.costPrice) * 100) / 100 : null }
}

// The page's rows by model and colour, in their order: a model's sizes of one colour make a group, and a row without
// a model code is a group of its own.
export function groupByModel(rows: CommissionTariffRow[]): Array<[string, CommissionTariffRow[]]> {
  const groups = new Map<string, CommissionTariffRow[]>()
  for (const row of rows) {
    const model = row.modelCode?.trim()
    const key = model ? `${model}|${row.color?.trim() || ''}` : row.barcode
    groups.set(key, [...(groups.get(key) ?? []), row])
  }
  // Inside a colour its sizes run in size order (S, M, L, XL, 2XL …), not in the file's order.
  return [...groups].map(([key, rows]) => [key, [...rows].sort((a, b) => compareOptionValues('Beden', a.size ?? '', b.size ?? ''))] as [string, CommissionTariffRow[]])
}

const amount = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
// "752,64 – 791,01 ₺", or one price when all are the same.
export function priceRange(values: number[]) {
  const low = Math.min(...values), high = Math.max(...values)
  return low === high ? money(low) : `${amount(low)} – ${money(high)}`
}
// What a model's variants leave, with its sign: "266,09 – 375,71 ₺".
export function amountRange(values: number[]) {
  const signed = (value: number) => `${value < 0 ? '− ' : ''}${amount(Math.abs(value))}`
  const low = Math.min(...values), high = Math.max(...values)
  return low === high ? `${signed(low)} ₺` : `${signed(low)} – ${signed(high)} ₺`
}

const productImage = (row: CommissionTariffRow) => row.imageUrl
  ? <img src={thumbnailUrl(row.imageUrl)} alt="" loading="lazy" className="commission-product-image" />
  : <i className="commission-product-image" aria-hidden="true">{(row.productName ?? row.barcode).slice(0, 1)}</i>

// What a price leaves, with its share of the price.
export function remainingText(choice: CommissionChoice) {
  if (choice.remaining === null) return 'Maliyet girilmemiş'
  const share = choice.price > 0 ? ` · %${(choice.remaining / choice.price * 100).toLocaleString('tr-TR', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}` : ''
  return `${choice.remaining < 0 ? '− ' : ''}${money(Math.abs(choice.remaining))} kalır${share}`
}
type UploadResult = { tariffs: CommissionTariff[]; importedRows: number; skippedRows: number; alreadyUploaded: boolean; warnings: string[] }

const money = (value: number) => `${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₺`
export const rateText = (rate: number) => `%${rate.toLocaleString('tr-TR', { maximumFractionDigits: 2 })}`

// "≥ 470,64 ₺", "435,98 – 470,63 ₺" or "≤ 388,63 ₺": the price range a band covers.
export function bandRange(band: CommissionBand) {
  if (band.min !== null && band.max !== null) return `${band.min.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} – ${money(band.max)}`
  if (band.min !== null) return `≥ ${money(band.min)}`
  if (band.max !== null) return `≤ ${money(band.max)}`
  return '—'
}

// The price that earns the next lower commission, or why there is none.
export function nextBandHint(row: Pick<CommissionTariffRow, 'nextLowerBand' | 'currentTier' | 'basisPrice'>) {
  if (row.basisPrice === null) return 'Komisyona esas fiyat yok'
  if (row.currentTier === null) return 'Fiyat hiçbir banda düşmüyor'
  if (!row.nextLowerBand?.max) return 'En düşük komisyon bandında'
  return `${rateText(row.nextLowerBand.rate)} için ≤ ${money(row.nextLowerBand.max)}`
}

// The marketplace's own "Güncel Komisyon" is shown beside the band the panel computes; a difference is flagged, never hidden.
export function rateDisagrees(row: Pick<CommissionTariffRow, 'currentRate' | 'currentBandRate'>) {
  return row.currentRate !== null && row.currentBandRate !== null && Math.abs(row.currentRate - row.currentBandRate) > 0.0001
}

// The stored end is exclusive (the minute after the file's last one); the label shows the file's own last minute.
const periodLabel = (tariff: CommissionTariff) => `${formatIstanbul(tariff.periodStart, { day: '2-digit', month: 'long', hour: '2-digit', minute: '2-digit' })} – ${formatIstanbul(new Date(new Date(tariff.periodEnd).getTime() - 60_000), { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })}`

// Weekly commission tariffs the seller downloads from the marketplace and uploads here. The panel reads a sale's
// commission from the band its price falls in when the order does not carry it, and shows which price earns a lower
// band. Nothing is sent to the marketplace.
export function CommissionTariffsPanel({ connectionId, platformName }: { connectionId: string; platformName: string }) {
  const client = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<TariffFilter>({ tier: '', cost: '' })
  const [page, setPage] = useState(0)
  const [message, setMessage] = useState<{ kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] } | null>(null)
  const [confirm, confirmDialog] = useConfirmDialog()
  const tariffs = useQuery({ queryKey: ['commission-tariffs', connectionId], queryFn: () => hubApi<CommissionTariff[]>(`/connections/${connectionId}/commission-tariffs`) })
  const active = tariffs.data?.find(tariff => tariff.id === selected) ?? tariffs.data?.find(tariff => tariff.isCurrent) ?? tariffs.data?.[0] ?? null
  const rows = useQuery({
    queryKey: ['commission-tariff-rows', connectionId, active?.id, search.trim(), filter.tier, filter.cost, page],
    queryFn: () => hubApi<RowPage>(`/connections/${connectionId}/commission-tariffs/${active!.id}/rows?${new URLSearchParams({ page: String(page), limit: '50',
      ...(search.trim() ? { search: search.trim() } : {}), ...(filter.tier ? { tier: filter.tier } : {}), ...(filter.cost ? { cost: filter.cost } : {}) })}`),
    enabled: active !== null
  })
  const upload = useMutation({
    mutationFn: (file: File) => {
      const body = new FormData()
      body.append('file', file)
      return hubApi<UploadResult>(`/connections/${connectionId}/commission-tariffs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff') }, body })
    },
    onSuccess: async result => {
      setSelected(result.tariffs[0]?.id ?? null)
      setPage(0)
      setMessage(result.alreadyUploaded
        ? { kind: 'info', text: 'Bu dosya bu dönem için daha önce yüklenmiş; mevcut tarife gösteriliyor.' }
        : { kind: result.skippedRows ? 'info' : 'success', text: `${result.importedRows.toLocaleString('tr-TR')} ürünün tarifesi yüklendi${result.skippedRows ? `; ${result.skippedRows.toLocaleString('tr-TR')} satır okunamadı` : ''}.`, warnings: result.warnings })
      await client.invalidateQueries({ queryKey: ['commission-tariffs', connectionId] })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarife yüklenemedi.' })
  })
  const remove = useMutation({
    mutationFn: (tariff: CommissionTariff) => hubApi<boolean>(`/connections/${connectionId}/commission-tariffs/${tariff.id}`, { method: 'DELETE', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-delete') } }),
    onSuccess: async () => {
      setSelected(null)
      setMessage({ kind: 'success', text: 'Tarife silindi.' })
      await client.invalidateQueries({ queryKey: ['commission-tariffs', connectionId] })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarife silinemedi.' })
  })
  const choose = (file: File | undefined) => {
    if (!file) return
    if (!file.name.toLowerCase().endsWith('.xlsx')) { setMessage({ kind: 'error', text: 'Yalnız pazaryerinden indirilen .xlsx tarife dosyası yüklenebilir.' }); return }
    setMessage(null)
    upload.mutate(file)
  }
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  const askDelete = async (tariff: CommissionTariff) => {
    if (!(await confirm({ title: 'Tarife Silinsin mi?', message: `${tariff.periodText} dönemine ait tarife ve ${tariff.rowCount.toLocaleString('tr-TR')} ürün satırı silinecek. Bu dönemin satışlarında komisyon bu tarifeden okunamaz.`, confirmLabel: 'Tarifeyi Sil', destructive: true }))) return
    remove.mutate(tariff)
  }
  const pageCount = rows.data ? Math.max(1, Math.ceil(rows.data.totalCount / rows.data.pageSize)) : 1

  return <section className="panel commission-tariffs" aria-labelledby="commission-tariffs-title">
    <div className="cs-sec-head"><h3 id="commission-tariffs-title">Komisyon Tarifeleri</h3><p>{platformName}’un haftalık komisyon tarifesi dosyasını yükleyin. Siparişte komisyon bilgisi yoksa satışın tarihine ve fiyatına göre komisyon bu tarifeden bulunur; fiyatınızın hangi banda düştüğünü ve bir alt banda inmek için gereken fiyatı görürsünüz. Pazaryerine hiçbir şey gönderilmez.</p></div>
    <div className={`commission-dropzone${dragging ? ' is-dragging' : ''}`} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
      <strong>{upload.isPending ? 'Tarife okunuyor…' : 'Tarife dosyasını buraya bırakın'}</strong>
      <span>{platformName} satıcı panelinden indirilen .xlsx dosyası · en fazla 10 MiB</span>
      <button type="button" className="secondary" disabled={upload.isPending} onClick={() => input.current?.click()}>Dosya Seç</button>
      <input ref={input} type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" hidden onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
    </div>
    {message && <div className={`commission-message is-${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}><p>{message.text}</p>{message.warnings?.length ? <ul>{message.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul> : null}</div>}

    {tariffs.isLoading && <Busy />}
    {tariffs.isError && <ErrorBox error={tariffs.error} onRetry={() => void tariffs.refetch()} />}
    {tariffs.data && tariffs.data.length === 0 && <p className="cs-empty">Henüz tarife yüklenmedi. Komisyon yalnız siparişin kendi bilgisinden okunuyor.</p>}
    {tariffs.data && tariffs.data.length > 0 && <div className="commission-periods" role="list">
      {tariffs.data.map(tariff => <div key={tariff.id} role="listitem" className={`commission-period${tariff.id === active?.id ? ' is-on' : ''}`}>
        <button type="button" className="commission-period-main" aria-pressed={tariff.id === active?.id} onClick={() => { setSelected(tariff.id); setPage(0) }}>
          <span className="commission-period-title">{periodLabel(tariff)}{tariff.isCurrent && <em className="commission-badge is-current">Geçerli</em>}</span>
          <small>{tariff.rowCount.toLocaleString('tr-TR')} ürün · {tariff.matchedRowCount.toLocaleString('tr-TR')} panelde eşleşti · {tariff.fileName} · {formatIstanbul(tariff.uploadedAt, { dateStyle: 'medium', timeStyle: 'short' })}</small>
        </button>
        <button type="button" className="secondary commission-period-delete" disabled={remove.isPending} onClick={() => void askDelete(tariff)} aria-label={`${tariff.periodText} tarifesini sil`}>Sil</button>
      </div>)}
    </div>}

    {active && <TariffPlan key={active.id} connectionId={connectionId} tariff={active} rows={rows.data} loading={rows.isLoading} error={rows.isError ? rows.error : null} onRetry={() => void rows.refetch()}
      search={search} onSearch={value => { setSearch(value); setPage(0) }} filter={filter} onFilter={value => { setFilter(value); setPage(0) }} page={page} pageCount={pageCount} onPage={setPage} onMessage={setMessage} />}
    {confirmDialog}
  </section>
}

type PanelMessage = { kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] }
const tiers = [1, 2, 3, 4]
// Which rows the list shows: by the band their price is in now, and whether their cost is entered.
type TariffFilter = { tier: '' | '1' | '2' | '3' | '4'; cost: '' | 'with' | 'without' }
const bulkLabel = (bulk: TariffBulk) => bulk.kind === 'tier' ? `${bulk.tier}. bant` : bulk.kind === 'amount' ? 'En yüksek kâr tutarı' : 'En yüksek kâr oranı'

// Toplu Seçim: a column (a band, or the band leaving the most money or share), a criterion on what the pick leaves
// (its share of the price or its amount) above or below a value, and the buttons that apply it, take that column's
// picks back or take every pick back.
function BulkPicker({ bulk, onApply, onClearColumn, onClearAll }: { bulk: TariffBulk; onApply: (bulk: TariffBulk) => void; onClearColumn: (column: string) => void; onClearAll: () => void }) {
  const [open, setOpen] = useState(false)
  const [column, setColumn] = useState(bulk.kind === 'tier' ? String(bulk.tier) : bulk.kind || '1')
  const [criterion, setCriterion] = useState<'' | 'rate' | 'amount'>(bulk.minRate != null || bulk.maxRate != null ? 'rate' : bulk.minAmount != null || bulk.maxAmount != null ? 'amount' : '')
  const [above, setAbove] = useState(bulk.maxRate == null && bulk.maxAmount == null)
  const [value, setValue] = useState(String(bulk.minRate ?? bulk.minAmount ?? bulk.maxRate ?? bulk.maxAmount ?? ''))
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent) => { if (ref.current && !ref.current.contains(event.target as Node)) setOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', escape) }
  }, [open])
  const limit = Number(value.replace(',', '.'))
  const valid = !criterion || (value.trim() !== '' && Number.isFinite(limit))
  const apply = () => {
    const tierColumn = /^[1-4]$/.test(column)
    const at = criterion && valid ? limit : null
    onApply({ kind: tierColumn ? 'tier' : column as TariffBulk['kind'], tier: tierColumn ? Number(column) : 1,
      minRate: criterion === 'rate' && above ? at : null, minAmount: criterion === 'amount' && above ? at : null,
      maxRate: criterion === 'rate' && !above ? at : null, maxAmount: criterion === 'amount' && !above ? at : null })
    setOpen(false)
  }
  const active = bulk.kind !== ''
  return <div className="commission-bulk" ref={ref}>
    <button type="button" className={`commission-bulk-trigger${active ? ' is-active' : ''}`} aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      {active && <em>Filtre aktif</em>}Toplu Seçim <UiIcon name="chevronDown" size={14} />
    </button>
    {open && <div className="commission-bulk-popover" role="dialog" aria-label="Toplu seçim">
      <label><span>Sütun</span>
        <select value={column} onChange={event => setColumn(event.target.value)}>
          {tiers.map(tier => <option key={tier} value={String(tier)}>{tier}. Bant</option>)}
          <option value="amount">En yüksek kâr tutarı</option><option value="rate">En yüksek kâr oranı</option>
        </select>
      </label>
      <hr />
      <label><span>Ölçüt</span>
        <select value={criterion} onChange={event => setCriterion(event.target.value as '' | 'rate' | 'amount')}>
          <option value="">Koşulsuz, tüm ürünler</option><option value="rate">Kâr / zarar oranına göre (%)</option><option value="amount">Kâr / zarar tutarına göre (₺)</option>
        </select>
      </label>
      <label><span>Karşılaştırma</span>
        <select value={above ? 'above' : 'below'} disabled={!criterion} onChange={event => setAbove(event.target.value === 'above')}>
          <option value="above">Verilenden büyük değerler</option><option value="below">Verilenden küçük değerler</option>
        </select>
      </label>
      <label><span>Değer</span>
        <input type="text" inputMode="decimal" value={value} disabled={!criterion} onChange={event => setValue(event.target.value)} placeholder={criterion === 'rate' ? 'ör. 30' : criterion === 'amount' ? 'ör. 100' : 'Ölçüt seçin'} />
      </label>
      <hr />
      <button type="button" className="rv-button-primary" disabled={!valid} onClick={apply}>Belirlenen Kriterlere Göre Seçimleri Yap</button>
      <button type="button" className="secondary is-danger" onClick={() => { onClearColumn(column); setOpen(false) }}>Bu Sütundaki Seçimleri Kaldır</button>
      <hr />
      <button type="button" className="secondary" onClick={() => { onClearAll(); setCriterion(''); setValue(''); setOpen(false) }}>Tüm Sütunlardan Seçimleri Kaldır</button>
    </div>}
  </div>
}

// One period's products with each band as a price to pick: the band's price, its commission and what the price leaves
// after the commission and the product cost, the one that leaves the most marked "Önerilen". A pick is made row by row
// (a band, or a price of the seller's own) or for every row at once; the picks go out as an Excel sheet the server
// writes. Picks are kept across pages and searches until the period changes.
function TariffPlan({ connectionId, tariff, rows, loading, error, onRetry, search, onSearch, filter, onFilter, page, pageCount, onPage, onMessage }: {
  connectionId: string; tariff: CommissionTariff; rows: RowPage | undefined; loading: boolean; error: unknown; onRetry: () => void
  search: string; onSearch: (value: string) => void; filter: TariffFilter; onFilter: (filter: TariffFilter) => void
  page: number; pageCount: number; onPage: (page: number) => void; onMessage: (message: PanelMessage | null) => void
}) {
  const [picks, setPicks] = useState<Map<string, TariffPick>>(() => new Map())
  // The pick for every row, set in the Toplu Seçim popover.
  const [bulk, setBulk] = useState<TariffBulk>(noBulk)
  const applied = bulk
  // A column's picks taken back: the pick for every row when it is that column, and the rows picked for that band.
  const clearColumn = (column: string) => {
    const tier = /^[1-4]$/.test(column) ? Number(column) : null
    if (tier === null ? bulk.kind === column : bulk.kind === 'tier' && bulk.tier === tier) setBulk(noBulk)
    if (tier !== null) setPicks(current => new Map([...current].filter(([, pick]) => pick.tier !== tier)))
  }
  const bulkOn = applied.kind !== ''
  const setPick = (rowId: string, pick: TariffPick | null) => setPicks(current => {
    const next = new Map(current)
    if (pick) next.set(rowId, pick); else next.delete(rowId)
    return next
  })
  // A click on the picked band takes it back (under a pick for every row, the row is then left out on purpose).
  const toggle = (row: CommissionTariffRow, tier: number, picked: boolean) => setPick(row.id, picked ? (bulkOn ? { tier: null, price: null } : null) : { tier, price: null })
  // A model's switch picks the band for all its variants that have it, or takes it back from all of them.
  const pickModel = (owners: CommissionTariffRow[], tier: number, all: boolean) => setPicks(current => {
    const next = new Map(current)
    for (const row of owners) {
      if (!all) next.set(row.id, { tier, price: null })
      else if (bulkOn) next.set(row.id, { tier: null, price: null })
      else next.delete(row.id)
    }
    return next
  })
  // The models whose variants are folded away.
  const [closed, setClosed] = useState<Set<string>>(() => new Set())
  const ownPicks = [...picks.values()].filter(pick => pick.tier !== null || pick.price !== null).length
  const download = useMutation({
    mutationFn: () => hubFile(`/connections/${connectionId}/commission-tariffs/${tariff.id}/export`, {
      method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-export') },
      body: JSON.stringify({ bulk: applied.kind || null, tier: applied.kind === 'tier' ? applied.tier : null, minRate: applied.minRate, minAmount: applied.minAmount, maxRate: applied.maxRate ?? null, maxAmount: applied.maxAmount ?? null,
        choices: [...picks].map(([rowId, pick]) => ({ rowId, tier: pick.tier, price: pick.price })) })
    }),
    onSuccess: ({ blob, fileName }) => {
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = fileName ?? 'komisyon-tarifesi-secimler.xlsx'
      link.click()
      setTimeout(() => URL.revokeObjectURL(url), 0)
      onMessage({ kind: 'success', text: 'Seçimler Excel olarak indirildi.' })
    },
    onError: reason => onMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Excel hazırlanamadı.' })
  })
  const conditionText = applied.minRate !== null ? `, kâr oranı %${applied.minRate.toLocaleString('tr-TR')} üstü` : applied.minAmount !== null ? `, kâr ${money(applied.minAmount)} üstü`
    : applied.maxRate != null ? `, kâr oranı %${applied.maxRate.toLocaleString('tr-TR')} altı` : applied.maxAmount != null ? `, kâr ${money(applied.maxAmount)} altı` : ''
  const summary = bulkOn ? `${bulkLabel(applied)} tüm ürünlerde${conditionText}${picks.size ? `; ${picks.size.toLocaleString('tr-TR')} ürün elle değişti` : ''}` : `${ownPicks.toLocaleString('tr-TR')} ürün seçildi`

  return <div className="commission-rows">
    <div className="commission-rows-head">
      <strong>{tariff.periodText} Bantları</strong>
    </div>
    {/* Which products the list shows. */}
    <div className="commission-plan-bar">
      <input type="search" className="commission-search" placeholder="Barkod, stok kodu ya da ürün ara" value={search} onChange={event => onSearch(event.target.value)} aria-label="Tarife satırlarında ara" />
      <label className="commission-plan-field"><span>Güncel Bant</span>
        <select value={filter.tier} onChange={event => onFilter({ ...filter, tier: event.target.value as TariffFilter['tier'] })}>
          <option value="">Tümü</option>{tiers.map(tier => <option key={tier} value={String(tier)}>{tier}. bant</option>)}
        </select>
      </label>
      <label className="commission-plan-field"><span>Maliyet</span>
        <select value={filter.cost} onChange={event => onFilter({ ...filter, cost: event.target.value as TariffFilter['cost'] })}>
          <option value="">Tümü</option><option value="with">Girili</option><option value="without">Girilmemiş</option>
        </select>
      </label>
    </div>
    {/* The pick for every product: a band, or the best band by money or by share; with a condition on what it leaves. */}
    <div className="commission-plan-bar">
      <BulkPicker bulk={bulk} onApply={setBulk} onClearColumn={clearColumn} onClearAll={() => { setBulk(noBulk); setPicks(new Map()) }} />
      <span className="commission-plan-count" role="status">{summary}</span>
      <button type="button" className="rv-button-primary commission-plan-download" disabled={(!bulkOn && ownPicks === 0) || download.isPending} onClick={() => download.mutate()}>
        <UiIcon name="download" size={16} />{download.isPending ? 'Hazırlanıyor…' : 'Excel İndir'}
      </button>
    </div>
    <p className="commission-plan-note">Kalan: fiyattan komisyon ve ürün maliyeti düştükten sonra kalan tutar; kargo, hizmet bedeli ve stopaj ürün bazında bilinmediği için düşülmedi. Excel yalnız seçilen ürünleri yeni fiyatlarıyla içerir; pazaryerine hiçbir şey gönderilmez.</p>
    {loading && <Busy />}
    {error !== null && <ErrorBox error={error} onRetry={onRetry} />}
    {rows && rows.items.length === 0 && <p className="cs-empty">Aramaya uyan ürün yok.</p>}
    {rows && rows.items.length > 0 && <div className="commission-table-wrap"><table className="commission-table is-plan">
      <thead><tr><th scope="col">Ürün</th><th scope="col">Güncel Fiyat</th>{tiers.map(tier => <th key={tier} scope="col">{tier}. Bant</th>)}<th scope="col">Kendi Fiyatın</th></tr></thead>
      <tbody>{groupByModel(rows.items).map(([key, group]) => {
        // A variant's row; under its model it shows its size, codes and cost, the model's name and picture being above.
        const variantRow = (row: CommissionTariffRow, nested: boolean) => {
          const pick = picks.get(row.id)
          const chosen = pickedTier(row, pick, applied)
          return <tr key={row.id} className={`${nested ? 'is-variant' : ''}${chosen !== null ? ' is-picked' : ''}`.trim() || undefined}>
            <th scope="row">{nested
              ? <span className="commission-variant-cell">
                <span className="commission-product">{row.size ?? row.productName ?? row.barcode}</span>
                <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
                <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
                {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
              </span>
              : <span className="commission-product-cell">
                {productImage(row)}
                <span>
                  <span className="commission-product">{row.productName ?? row.barcode}{row.size ? ` · ${row.size}` : ''}</span>
                  <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
                  <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
                  {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
                </span>
              </span>}</th>
            <td>
              <span className="commission-price">{row.basisPrice === null ? '—' : money(row.basisPrice)}</span>
              {row.currentSalePrice !== null && row.currentSalePrice !== row.basisPrice && <small>Satış fiyatı {money(row.currentSalePrice)}</small>}
              {row.currentTier === null || row.currentBandRate === null ? <em className="commission-badge is-unknown">Bant bilinmiyor</em> : <em className={`commission-badge tier-${row.currentTier}`}>{rateText(row.currentBandRate)} · {row.currentTier}. bant</em>}
              {rateDisagrees(row) && <small className="commission-warn" title="Pazaryerinin dosyadaki güncel komisyonu, fiyatın bandından farklı.">Dosyada {rateText(row.currentRate!)}</small>}
            </td>
            {tiers.map(tier => {
              const choice = row.choices?.find(item => item.tier === tier)
              if (!choice) return <td key={tier} className="commission-choice-none">—</td>
              const band = row.bands.find(item => item.tier === tier)
              const picked = chosen === tier
              return <td key={tier}>
                <button type="button" aria-pressed={picked} onClick={() => toggle(row, tier, picked)}
                  className={`commission-choice${picked ? ' is-picked' : ''}${choice.remaining === null ? ' is-unknown' : choice.remaining < 0 ? ' is-loss' : ''}`}>
                  <strong>{money(choice.price)}</strong>
                  <small>{band?.max === null ? 've üzeri' : 've altı'}{row.currentTier === tier ? ' · şu an' : ''}</small>
                  <span>{rateText(choice.rate)} komisyon · {money(choice.commission)}</span>
                  <b>{remainingText(choice)}</b>
                  <i className="commission-switch" aria-hidden="true"><span />{picked ? 'Seçili' : 'Seç'}</i>
                </button>
              </td>
            })}
            <td><OwnPrice row={row} pick={pick} picked={chosen === 'own'} onPick={price => setPick(row.id, price === null ? null : { tier: null, price })} /></td>
          </tr>
        }
        if (group.length === 1) return variantRow(group[0], false)
        const first = group.find(row => row.imageUrl) ?? group[0]
        const open = !closed.has(key)
        const prices = group.flatMap(row => row.basisPrice === null ? [] : [row.basisPrice])
        // The model's row: its picture, name and code, how many variants it has, and each band over all of them.
        return <Fragment key={key}>
          <tr className="is-model">
            <th scope="row"><span className="commission-product-cell">
              {productImage(first)}
              <span>
                <span className="commission-product">{first.productName ?? first.barcode}</span>
                <small className="technical-text">{group[0].modelCode}{first.color ? ` · ${first.color}` : ''}</small>
                <button type="button" className="commission-model-toggle" aria-expanded={open} onClick={() => setClosed(current => { const next = new Set(current); if (open) next.add(key); else next.delete(key); return next })}>
                  <UiIcon name={open ? 'chevronDown' : 'chevronRight'} size={14} />{group.length.toLocaleString('tr-TR')} {first.color ? 'beden' : 'varyant'}
                </button>
              </span>
            </span></th>
            <td><span className="commission-price">{prices.length ? priceRange(prices) : '—'}</span><small>{group.length.toLocaleString('tr-TR')} {first.color ? 'bedenin' : 'varyantın'} fiyatı</small></td>
            {tiers.map(tier => {
              const choices = group.flatMap(row => row.choices?.filter(item => item.tier === tier) ?? [])
              if (!choices.length) return <td key={tier} className="commission-choice-none">—</td>
              const owners = group.filter(row => row.choices?.some(item => item.tier === tier))
              const all = owners.every(row => pickedTier(row, picks.get(row.id), applied) === tier)
              const rates = [...new Set(choices.map(item => item.rate))]
              const lefts = choices.flatMap(item => item.remaining === null ? [] : [item.remaining])
              return <td key={tier}>
                <button type="button" aria-pressed={all} onClick={() => pickModel(owners, tier, all)}
                  className={`commission-choice is-model${all ? ' is-picked' : ''}${!lefts.length ? ' is-unknown' : Math.min(...lefts) < 0 ? ' is-loss' : ''}`}>
                  <strong>{priceRange(choices.map(item => item.price))}</strong>
                  <span>{rates.length === 1 ? rateText(rates[0]) : `${rateText(Math.min(...rates))} – ${rateText(Math.max(...rates))}`} komisyon</span>
                  <b>{lefts.length ? `${amountRange(lefts)} kalır` : 'Maliyet girilmemiş'}</b>
                  {lefts.length > 0 && lefts.length < choices.length && <small>{(choices.length - lefts.length).toLocaleString('tr-TR')} varyantın maliyeti yok</small>}
                  <i className="commission-switch" aria-hidden="true"><span />{all ? 'Tümü seçili' : 'Tümünü seç'}</i>
                </button>
              </td>
            })}
            <td />
          </tr>
          {open && group.map(row => variantRow(row, true))}
        </Fragment>
      })}</tbody>
    </table></div>}
    {rows && pageCount > 1 && <div className="commission-pager"><button type="button" className="secondary" disabled={page === 0} onClick={() => onPage(page - 1)}>Önceki</button><span>{(page + 1).toLocaleString('tr-TR')} / {pageCount.toLocaleString('tr-TR')}</span><button type="button" className="secondary" disabled={page + 1 >= pageCount} onClick={() => onPage(page + 1)}>Sonraki</button></div>}
  </div>
}

// A price of the seller's own for one product: typed with a comma or a point, it shows the band it falls in and what
// it leaves, and becomes the row's pick.
function OwnPrice({ row, pick, picked, onPick }: { row: CommissionTariffRow; pick: TariffPick | undefined; picked: boolean; onPick: (price: number | null) => void }) {
  const [text, setText] = useState(() => pick?.price ? String(pick.price).replace('.', ',') : '')
  const parse = (value: string) => { const number = Number(value.trim().replace(/\.(?=\d{3}(\D|$))/g, '').replace(',', '.')); return value.trim() && Number.isFinite(number) && number > 0 ? number : null }
  const price = parse(text)
  const choice = price === null ? null : ownChoice(row, price)
  return <div className={`commission-own${picked ? ' is-picked' : ''}`}>
    <input type="text" inputMode="decimal" placeholder="Fiyat girin" value={text} aria-label={`${row.productName ?? row.barcode} için kendi fiyatın`}
      onChange={event => { setText(event.target.value); onPick(parse(event.target.value)) }} />
    {text.trim() !== '' && (choice ? <small>{choice.tier}. bant · {rateText(choice.rate)}<b>{remainingText(choice)}</b></small> : <small className="commission-warn">Hiçbir banda düşmüyor</small>)}
  </div>
}
