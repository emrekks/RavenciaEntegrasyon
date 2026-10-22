import { useRef, useState, type DragEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { Busy, ErrorBox, useConfirmDialog } from '../../../shared/components'
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
}
type RowPage = { items: CommissionTariffRow[]; totalCount: number; page: number; pageSize: number }
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

    {active && <div className="commission-rows">
      <div className="commission-rows-head">
        <strong>{active.periodText} Bantları</strong>
        <input type="search" className="commission-search" placeholder="Barkod, stok kodu ya da ürün ara" value={search} onChange={event => { setSearch(event.target.value); setPage(0) }} aria-label="Tarife satırlarında ara" />
      </div>
      {rows.isLoading && <Busy />}
      {rows.isError && <ErrorBox error={rows.error} onRetry={() => void rows.refetch()} />}
      {rows.data && rows.data.items.length === 0 && <p className="cs-empty">Aramaya uyan ürün yok.</p>}
      {rows.data && rows.data.items.length > 0 && <div className="commission-table-wrap"><table className="commission-table">
        <thead><tr><th scope="col">Ürün</th><th scope="col">Komisyona Esas Fiyat</th><th scope="col">Bant</th><th scope="col">Bantlar</th><th scope="col">Alt Banda İnmek İçin</th></tr></thead>
        <tbody>{rows.data.items.map(row => <tr key={row.id}>
          <th scope="row"><span className="commission-product">{row.productName ?? row.barcode}{row.size ? ` · ${row.size}` : ''}</span><small className="technical-text">{row.barcode}{row.sellerSku && row.sellerSku !== row.barcode ? ` · ${row.sellerSku}` : ''}</small>{!row.matchesPanel && <small className="commission-unmatched">Panelde bu barkodla ürün yok</small>}</th>
          <td>{row.basisPrice === null ? '—' : money(row.basisPrice)}{row.currentSalePrice !== null && row.currentSalePrice !== row.basisPrice && <small>Satış fiyatı {money(row.currentSalePrice)}</small>}</td>
          <td>{row.currentTier === null || row.currentBandRate === null ? <em className="commission-badge is-unknown">Bilinmiyor</em> : <em className={`commission-badge tier-${row.currentTier}`}>{rateText(row.currentBandRate)} · {row.currentTier}. bant</em>}
            {rateDisagrees(row) && <small className="commission-warn" title="Pazaryerinin dosyadaki güncel komisyonu, fiyatın bandından farklı.">Dosyada {rateText(row.currentRate!)}</small>}</td>
          <td><ul className="commission-bands">{row.bands.map(band => <li key={band.tier} className={band.tier === row.currentTier ? 'is-current' : undefined}><span>{bandRange(band)}</span><b>{rateText(band.rate)}</b></li>)}</ul></td>
          <td>{nextBandHint(row)}</td>
        </tr>)}</tbody>
      </table></div>}
      {rows.data && pageCount > 1 && <div className="commission-pager"><button type="button" className="secondary" disabled={page === 0} onClick={() => setPage(value => value - 1)}>Önceki</button><span>{(page + 1).toLocaleString('tr-TR')} / {pageCount.toLocaleString('tr-TR')}</span><button type="button" className="secondary" disabled={page + 1 >= pageCount} onClick={() => setPage(value => value + 1)}>Sonraki</button></div>}
    </div>}
    {confirmDialog}
  </section>
}
