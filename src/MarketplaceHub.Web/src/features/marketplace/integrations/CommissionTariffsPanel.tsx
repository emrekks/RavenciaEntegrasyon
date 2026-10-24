import { useRef, useState, type DragEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubFile } from '../../../shared/api'
import { Busy, ErrorBox, UiIcon, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { formatIstanbul } from '../../../shared/format-date'
import '../commission-tariffs.css'

// The marketplaces whose commission tariff export the panel reads (CommissionTariffPlatforms on the server).
export const commissionTariffPlatforms = new Set(['TRENDYOL'])

export type CommissionBand = { tier: number; min: number | null; max: number | null; rate: number }
export type CommissionTariff = { id: string; platformCode: string; periodStart: string; periodEnd: string; periodText: string; fileName: string; uploadedAt: string; rowCount: number; matchedRowCount: number; isCurrent: boolean }
export type CommissionTariffRow = {
  id: string; barcode: string; sellerSku: string | null; modelCode: string | null; productName: string | null; size: string | null
  basisPrice: number | null; currentSalePrice: number | null; currentRate: number | null; currentTier: number | null; currentBandRate: number | null
  bands: CommissionBand[]; nextLowerBand: CommissionBand | null; matchesPanel: boolean
  // The product cost entered on the panel, each band as a price to pick and the band that leaves the most (absent in
  // an older server's answer).
  costPrice?: number | null; choices?: CommissionChoice[] | null; recommendedTier?: number | null
}
// A band as a price the seller can pick: its commission and what the price leaves after it and the product cost.
export type CommissionChoice = { tier: number; price: number; rate: number; commission: number; remaining: number | null }
type RowPage = { items: CommissionTariffRow[]; totalCount: number; page: number; pageSize: number }

// A row's own pick: a band, a price of the seller's own, or neither (the row left out of the Excel).
export type TariffPick = { tier: number | null; price: number | null }
// The pick for every row: none, the recommended band, or one band number.
export type TariffBulk = '' | 'recommended' | '1' | '2' | '3' | '4'

// The band a row counts as picked: its own pick, else the pick for every row; 'own' for a price of the seller's own.
export function pickedTier(row: Pick<CommissionTariffRow, 'choices' | 'recommendedTier'>, pick: TariffPick | undefined, bulk: TariffBulk): number | 'own' | null {
  if (pick) return pick.price !== null ? 'own' : pick.tier
  if (bulk === 'recommended') return row.recommendedTier ?? null
  if (bulk) return row.choices?.some(choice => choice.tier === Number(bulk)) ? Number(bulk) : null
  return null
}

// A price of the seller's own, worked out as the server does: the band it falls in, its commission and what it leaves.
export function ownChoice(row: Pick<CommissionTariffRow, 'bands' | 'costPrice'>, price: number): CommissionChoice | null {
  const rounded = Math.round(price * 100) / 100
  const band = row.bands.find(item => (item.min === null || rounded >= item.min) && (item.max === null || rounded <= item.max))
  if (!band || rounded <= 0) return null
  const commission = Math.round(rounded * band.rate) / 100
  return { tier: band.tier, price: rounded, rate: band.rate, commission, remaining: row.costPrice ? Math.round((rounded - commission - row.costPrice) * 100) / 100 : null }
}

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
  const [page, setPage] = useState(0)
  const [message, setMessage] = useState<{ kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] } | null>(null)
  const [confirm, confirmDialog] = useConfirmDialog()
  const tariffs = useQuery({ queryKey: ['commission-tariffs', connectionId], queryFn: () => hubApi<CommissionTariff[]>(`/connections/${connectionId}/commission-tariffs`) })
  const active = tariffs.data?.find(tariff => tariff.id === selected) ?? tariffs.data?.find(tariff => tariff.isCurrent) ?? tariffs.data?.[0] ?? null
  const rows = useQuery({
    queryKey: ['commission-tariff-rows', connectionId, active?.id, search.trim(), page],
    queryFn: () => hubApi<RowPage>(`/connections/${connectionId}/commission-tariffs/${active!.id}/rows?${new URLSearchParams({ page: String(page), limit: '50', ...(search.trim() ? { search: search.trim() } : {}) })}`),
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
      search={search} onSearch={value => { setSearch(value); setPage(0) }} page={page} pageCount={pageCount} onPage={setPage} onMessage={setMessage} />}
    {confirmDialog}
  </section>
}

type PanelMessage = { kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] }
const tiers = [1, 2, 3, 4]
const bulkLabels: Record<Exclude<TariffBulk, ''>, string> = { recommended: 'Önerilen bant', 1: '1. bant', 2: '2. bant', 3: '3. bant', 4: '4. bant' }

// One period's products with each band as a price to pick: the band's price, its commission and what the price leaves
// after the commission and the product cost, the one that leaves the most marked "Önerilen". A pick is made row by row
// (a band, or a price of the seller's own) or for every row at once; the picks go out as an Excel sheet the server
// writes. Picks are kept across pages and searches until the period changes.
function TariffPlan({ connectionId, tariff, rows, loading, error, onRetry, search, onSearch, page, pageCount, onPage, onMessage }: {
  connectionId: string; tariff: CommissionTariff; rows: RowPage | undefined; loading: boolean; error: unknown; onRetry: () => void
  search: string; onSearch: (value: string) => void; page: number; pageCount: number; onPage: (page: number) => void; onMessage: (message: PanelMessage | null) => void
}) {
  const [picks, setPicks] = useState<Map<string, TariffPick>>(() => new Map())
  const [bulk, setBulk] = useState<TariffBulk>('')
  const setPick = (rowId: string, pick: TariffPick | null) => setPicks(current => {
    const next = new Map(current)
    if (pick) next.set(rowId, pick); else next.delete(rowId)
    return next
  })
  // A click on the picked band takes it back (under a pick for every row, the row is then left out on purpose).
  const toggle = (row: CommissionTariffRow, tier: number, picked: boolean) => setPick(row.id, picked ? (bulk ? { tier: null, price: null } : null) : { tier, price: null })
  const ownPicks = [...picks.values()].filter(pick => pick.tier !== null || pick.price !== null).length
  const download = useMutation({
    mutationFn: () => hubFile(`/connections/${connectionId}/commission-tariffs/${tariff.id}/export`, {
      method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-export') },
      body: JSON.stringify({ bulk: bulk === 'recommended' ? 'recommended' : bulk ? 'tier' : null, tier: bulk && bulk !== 'recommended' ? Number(bulk) : null,
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
  const summary = bulk ? `${bulkLabels[bulk]} tüm ürünlerde${picks.size ? `; ${picks.size.toLocaleString('tr-TR')} ürün elle değişti` : ''}` : `${ownPicks.toLocaleString('tr-TR')} ürün seçildi`

  return <div className="commission-rows">
    <div className="commission-rows-head">
      <strong>{tariff.periodText} Bantları</strong>
      <input type="search" className="commission-search" placeholder="Barkod, stok kodu ya da ürün ara" value={search} onChange={event => onSearch(event.target.value)} aria-label="Tarife satırlarında ara" />
    </div>
    <div className="commission-plan-bar">
      <label className="commission-plan-bulk"><span>Toplu Seçim</span>
        <select value={bulk} onChange={event => setBulk(event.target.value as TariffBulk)}>
          <option value="">Yok</option><option value="recommended">Önerilen bantlar</option>
          {tiers.map(tier => <option key={tier} value={String(tier)}>Tüm ürünlerde {tier}. bant</option>)}
        </select>
      </label>
      <span className="commission-plan-count" role="status">{summary}</span>
      <button type="button" className="secondary" disabled={!bulk && picks.size === 0} onClick={() => { setBulk(''); setPicks(new Map()) }}>Seçimleri Temizle</button>
      <button type="button" className="rv-button-primary commission-plan-download" disabled={(!bulk && ownPicks === 0) || download.isPending} onClick={() => download.mutate()}>
        <UiIcon name="download" size={16} />{download.isPending ? 'Hazırlanıyor…' : 'Excel İndir'}
      </button>
    </div>
    <p className="commission-plan-note">Kalan: fiyattan komisyon ve ürün maliyeti düştükten sonra kalan tutar; kargo, hizmet bedeli ve stopaj ürün bazında bilinmediği için düşülmedi. Excel yalnız seçilen ürünleri yeni fiyatlarıyla içerir; pazaryerine hiçbir şey gönderilmez.</p>
    {loading && <Busy />}
    {error !== null && <ErrorBox error={error} onRetry={onRetry} />}
    {rows && rows.items.length === 0 && <p className="cs-empty">Aramaya uyan ürün yok.</p>}
    {rows && rows.items.length > 0 && <div className="commission-table-wrap"><table className="commission-table is-plan">
      <thead><tr><th scope="col">Ürün</th><th scope="col">Güncel Fiyat</th>{tiers.map(tier => <th key={tier} scope="col">{tier}. Bant</th>)}<th scope="col">Kendi Fiyatın</th></tr></thead>
      <tbody>{rows.items.map(row => {
        const pick = picks.get(row.id)
        const chosen = pickedTier(row, pick, bulk)
        return <tr key={row.id} className={chosen !== null ? 'is-picked' : undefined}>
          <th scope="row">
            <span className="commission-product">{row.productName ?? row.barcode}{row.size ? ` · ${row.size}` : ''}</span>
            <small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>
            <small>{row.costPrice ? `Maliyet ${money(row.costPrice)}` : 'Maliyet girilmemiş'}</small>
            {!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}
          </th>
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
                className={`commission-choice${picked ? ' is-picked' : ''}${row.recommendedTier === tier ? ' is-recommended' : ''}${choice.remaining === null ? ' is-unknown' : choice.remaining < 0 ? ' is-loss' : ''}`}>
                {row.recommendedTier === tier && <em className="commission-choice-flag">Önerilen</em>}
                <strong>{money(choice.price)}</strong>
                <small>{band?.max === null ? 've üzeri' : 've altı'}{row.currentTier === tier ? ' · şu an' : ''}</small>
                <span>{rateText(choice.rate)} komisyon · {money(choice.commission)}</span>
                <b>{remainingText(choice)}</b>
                <i aria-hidden="true">{picked ? 'Seçildi' : 'Seç'}</i>
              </button>
            </td>
          })}
          <td><OwnPrice row={row} pick={pick} picked={chosen === 'own'} onPick={price => setPick(row.id, price === null ? null : { tier: null, price })} /></td>
        </tr>
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
