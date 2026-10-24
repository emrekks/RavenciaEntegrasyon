// Ürün içe / dışa aktarma: the products export download and the upload of cost and desi that is previewed before
// anything is saved. Price, stock and VAT are in the export for reference only; nothing is sent to a marketplace.
import { useRef, useState, type DragEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../shared/idempotency'
import { formatMoney } from '../../../shared/format-money'
import { RrIcon, RrModal } from '../../marketplace/returns-ui'
import './product-transfer.css'

export type ProductTransferSummary = { variantCount: number; missingCostCount: number; missingDesiCount: number }
export type ProductTransferOutcome = 'UPDATE' | 'UNCHANGED' | 'SKIPPED' | 'UNMATCHED' | 'AMBIGUOUS' | 'ERROR'
export type ProductTransferFieldChange = 'SET' | 'CLEAR' | null
export type ProductTransferRow = {
  rowNumber: number; sku: string | null; barcode: string | null; variantId: string | null; productTitle: string | null; variantLabel: string | null
  oldCost: number | null; newCost: number | null; costChange: ProductTransferFieldChange
  oldDesi: number | null; newDesi: number | null; desiChange: ProductTransferFieldChange
  outcome: ProductTransferOutcome; message: string | null
}
export type ProductTransferChange = { variantId: string; version: number; setCost: boolean; cost: number | null; setDesi: boolean; desi: number | null }
export type ProductTransferPreview = {
  fileName: string; totalRows: number; updates: number; costUpdates: number; costClears: number; desiUpdates: number; desiClears: number
  unchanged: number; skipped: number; unmatched: number; ambiguous: number; errors: number
  rows: ProductTransferRow[]; rowsTruncated: boolean; changes: ProductTransferChange[]
}
export type ProductTransferApplyResult = { variants: number; costsUpdated: number; costsCleared: number; desiUpdated: number; desiCleared: number; products: number }
export type ProductTransferExportFilter = 'all' | 'cost' | 'desi'

export const productTransferSummaryKey = ['products', 'transfer-summary'] as const

export function productTransferExportHref(filter: ProductTransferExportFilter) {
  return `/api/v1/products/transfer/export.xlsx${filter === 'all' ? '' : `?missing=${filter}`}`
}

const outcomeLabels: Record<ProductTransferOutcome, [string, 'ok' | 'warn' | 'bad' | 'info' | 'muted']> = {
  UPDATE: ['Güncellenecek', 'ok'],
  UNCHANGED: ['Değişmiyor', 'muted'],
  SKIPPED: ['Atlandı', 'muted'],
  UNMATCHED: ['Eşleşmedi', 'warn'],
  AMBIGUOUS: ['Belirsiz', 'bad'],
  ERROR: ['Hatalı', 'bad']
}
export function productTransferOutcomeLabel(outcome: ProductTransferOutcome) { return outcomeLabels[outcome]?.[0] ?? outcome }

const count = (value: number) => value.toLocaleString('tr-TR')
const formatDesi = (value: number | null) => value == null ? '—' : value.toLocaleString('tr-TR', { maximumFractionDigits: 2 })

export function ProductTransferDialog({ onClose }: { onClose: () => void }) {
  const client = useQueryClient()
  const summary = useQuery({ queryKey: productTransferSummaryKey, queryFn: () => hubApi<ProductTransferSummary>('/products/transfer/summary') })
  const [filter, setFilter] = useState<ProductTransferExportFilter>('all')
  const [dragging, setDragging] = useState(false)
  const [fileError, setFileError] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  // One id per previewed file: a repeated save of the same preview is answered once by the server, and a refused save
  // gets a fresh key on the next try.
  const previewId = useRef<string | null>(null)

  const preview = useMutation({
    mutationFn: (file: File) => {
      const body = new FormData()
      body.append('file', file)
      return hubApi<ProductTransferPreview>('/products/transfer/import', { method: 'POST', body })
    },
    onMutate: () => { previewId.current = null },
    onSuccess: () => { previewId.current = newIdempotencyKey() }
  })
  const apply = useMutation({
    mutationFn: (changes: ProductTransferChange[]) => {
      const body = JSON.stringify({ changes })
      const key = versionedIdempotencyKey('product-transfer', previewId.current ?? newIdempotencyKey(), 1, body)
      return sendWithVersionedIdempotencyKey(key, idempotencyKey => hubApi<ProductTransferApplyResult>('/products/transfer/apply', { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey }, body }))
    },
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: ['products'] }),
        client.invalidateQueries({ queryKey: ['product'] }),
        client.invalidateQueries({ queryKey: productTransferSummaryKey })
      ])
    }
  })

  const choose = (file: File | undefined) => {
    if (!file) return
    const name = file.name.toLowerCase()
    if (!name.endsWith('.xlsx') && !name.endsWith('.csv')) { setFileError('Yalnız .xlsx ya da .csv dosyası yüklenebilir.'); return }
    if (file.size > 10 * 1024 * 1024) { setFileError('Dosya en fazla 10 MiB olabilir.'); return }
    setFileError(null)
    apply.reset()
    preview.mutate(file)
  }
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  const restart = () => { preview.reset(); apply.reset(); setFileError(null) }

  const result = preview.data
  const done = apply.data
  const writeCount = result ? result.changes.length : 0
  const problemCount = result ? result.unmatched + result.ambiguous + result.errors : 0
  const busy = preview.isPending || apply.isPending
  const missingCost = summary.data?.missingCostCount ?? 0
  const missingDesi = summary.data?.missingDesiCount ?? 0

  const foot = done
    ? <button type="button" className="rr-btn rr-primary" onClick={onClose}>Kapat</button>
    : result
      ? <>
        <button type="button" className="rr-btn" disabled={busy} onClick={restart}>Başka Dosya Yükle</button>
        <button type="button" className="rr-btn rr-primary" disabled={busy || writeCount === 0} onClick={() => apply.mutate(result.changes)}><RrIcon name="check" />{apply.isPending ? 'Kaydediliyor…' : `Değişiklikleri Kaydet (${count(writeCount)})`}</button>
      </>
      : <button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button>

  return <RrModal crumb="Ürünler" title="Ürünleri İçe / Dışa Aktar" desc="Ürünlerinizi Excel'e aktarın, maliyet ve desi sütunlarını düzenleyip geri yükleyin. Değişiklikler önce önizlenir; siz kaydetmeden hiçbir şey değişmez ve pazaryerlerine hiçbir şey gönderilmez." size="lg" onClose={onClose} locked={apply.isPending} foot={foot} modalClass="pci-modal">
    {done ? <div className="pci-done" role="status">
      <span className="pci-done-mark"><RrIcon name="check" size={20} /></span>
      <strong>{done.variants === 0 ? 'Değerler zaten güncel' : 'Değişiklikler kaydedildi'}</strong>
      <p>{done.variants === 0 ? 'Dosyadaki maliyet ve desiler katalogda zaten aynıydı; hiçbir şey değişmedi.' : appliedText(done)}</p>
    </div> : result ? <PreviewView preview={result} problemCount={problemCount} error={apply.error} /> : <>
      {summary.data && (missingCost > 0 || missingDesi > 0) && <div className="pci-hint" role="note"><RrIcon name="info" /><span>Toplam {count(summary.data.variantCount)} varyantın <b>{count(missingCost)} tanesinde maliyet</b>, <b>{count(missingDesi)} tanesinde desi</b> eksik. Kâr ve kargo hesabı bu değerlerle yapılır.</span></div>}
      <ol className="pci-steps">
        <li className="pci-step">
          <span className="pci-step-n" aria-hidden="true">1</span>
          <div className="pci-step-b">
            <strong>Ürünleri Dışa Aktarın</strong>
            <p>Her varyant bir satırdır: stok kodu, barkod, ürün adı, varyant, satış fiyatı, stok, KDV oranı, <b>maliyet</b> ve <b>desi</b>. Geri yüklediğinizde yalnız <b>Maliyet</b> ve <b>Desi</b> sütunları okunur; fiyat ve stok bilgi içindir. Boş hücre değişmez; bir değeri silmek için <b>-</b> yazın.</p>
            <div className="pci-step-acts">
              <label className="pci-field"><span>Varyantlar</span>
                <select className="pci-select" value={filter} onChange={event => setFilter(event.target.value as ProductTransferExportFilter)}>
                  <option value="all">Tüm Varyantlar{summary.data ? ` (${count(summary.data.variantCount)})` : ''}</option>
                  <option value="cost">Maliyeti Eksik Olanlar{summary.data ? ` (${count(missingCost)})` : ''}</option>
                  <option value="desi">Desisi Eksik Olanlar{summary.data ? ` (${count(missingDesi)})` : ''}</option>
                </select>
              </label>
              <a className="rr-btn" href={productTransferExportHref(filter)} download><RrIcon name="export" />Excel'e Aktar</a>
            </div>
          </div>
        </li>
        <li className="pci-step">
          <span className="pci-step-n" aria-hidden="true">2</span>
          <div className="pci-step-b">
            <strong>Düzenlediğiniz Dosyayı İçe Aktarın</strong>
            <div className={`pci-drop${dragging ? ' is-dragging' : ''}`} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
              <RrIcon name="upload" size={20} />
              <b>{preview.isPending ? 'Dosya okunuyor…' : 'Dosyayı buraya bırakın'}</b>
              <span>.xlsx ya da .csv · en fazla 10 MiB, 20.000 satır</span>
              <button type="button" className="rr-btn" disabled={preview.isPending} onClick={() => input.current?.click()}>Dosya Seç</button>
              <input ref={input} type="file" accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" hidden aria-label="Ürün dosyası" onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
            </div>
            {(fileError || preview.error) && <p className="pci-error" role="alert">{fileError ?? (preview.error instanceof Error ? preview.error.message : 'Dosya okunamadı.')}</p>}
          </div>
        </li>
      </ol>
    </>}
  </RrModal>
}

function appliedText(done: ProductTransferApplyResult) {
  const parts = [
    done.costsUpdated ? `${count(done.costsUpdated)} maliyet güncellendi` : '',
    done.costsCleared ? `${count(done.costsCleared)} maliyet silindi` : '',
    done.desiUpdated ? `${count(done.desiUpdated)} desi güncellendi` : '',
    done.desiCleared ? `${count(done.desiCleared)} desi silindi` : ''
  ].filter(Boolean)
  return `${count(done.variants)} varyant kaydedildi (${count(done.products)} ürün): ${parts.join(', ')}.`
}

function FieldCell({ change, oldValue, newValue, format }: { change: ProductTransferFieldChange; oldValue: number | null; newValue: number | null; format: (value: number | null) => string }) {
  if (change === 'SET') return <><span className="pci-old">{format(oldValue)}</span><span className="pci-arrow" aria-label="yeni değer">→</span><b>{format(newValue)}</b></>
  if (change === 'CLEAR') return <><span className="pci-old">{format(oldValue)}</span><span className="pci-arrow" aria-label="yeni değer">→</span><b className="pci-clear">Silinecek</b></>
  return <span className="pci-same">{oldValue == null ? '—' : format(oldValue)}</span>
}

function PreviewView({ preview, problemCount, error }: { preview: ProductTransferPreview; problemCount: number; error: unknown }) {
  const stats: Array<[string, number, string]> = [
    ['Güncellenecek Varyant', preview.updates, 'ok'],
    ['Maliyet Değişen', preview.costUpdates + preview.costClears, preview.costUpdates + preview.costClears ? 'ok' : 'muted'],
    ['Desi Değişen', preview.desiUpdates + preview.desiClears, preview.desiUpdates + preview.desiClears ? 'ok' : 'muted'],
    ['Değişmiyor', preview.unchanged, 'muted'],
    ['Boş / Atlanan', preview.skipped, 'muted'],
    ['Eşleşmedi', preview.unmatched, preview.unmatched ? 'warn' : 'muted'],
    ['Belirsiz', preview.ambiguous, preview.ambiguous ? 'bad' : 'muted'],
    ['Hatalı', preview.errors, preview.errors ? 'bad' : 'muted']
  ]
  const clears = preview.costClears + preview.desiClears
  return <div className="pci-preview">
    <p className="pci-file"><RrIcon name="doc" /><span><b>{preview.fileName}</b> · {count(preview.totalRows)} satır okundu</span></p>
    <div className="pci-stats" role="list" aria-label="Önizleme özeti">{stats.map(([label, value, tone]) => <div key={label} role="listitem" className={`pci-stat is-${tone}`}><small>{label}</small><strong>{count(value)}</strong></div>)}</div>
    {clears > 0 && <p className="pci-note" role="note">{[preview.costClears ? `${count(preview.costClears)} varyantın maliyeti` : '', preview.desiClears ? `${count(preview.desiClears)} varyantın desisi` : ''].filter(Boolean).join(' ve ')} silinecek.</p>}
    {problemCount > 0 && <p className="pci-note" role="note">Eşleşmeyen, belirsiz ve hatalı satırlar kaydedilmez. Düzeltmek için dosyayı değiştirip yeniden yükleyin; diğer satırları şimdi kaydedebilirsiniz.</p>}
    {error ? <p className="pci-error" role="alert">{error instanceof Error ? error.message : 'Değişiklikler kaydedilemedi.'}</p> : null}
    {preview.rows.length === 0 ? <p className="pci-empty">Maliyet ya da desi sütunu dolu satır yok; kaydedilecek bir şey bulunmadı.</p> : <div className="pci-tblwrap"><table className="pci-tbl">
      <thead><tr><th scope="col">Satır</th><th scope="col">Ürün</th><th scope="col">Maliyet</th><th scope="col">Desi</th><th scope="col">Durum</th></tr></thead>
      <tbody>{preview.rows.map(row => {
        const [label, tone] = outcomeLabels[row.outcome] ?? [row.outcome, 'muted']
        const matched = row.variantId != null
        return <tr key={`${row.rowNumber}-${row.outcome}`}>
          <td className="pci-num">{row.rowNumber}</td>
          <td><span className="pci-title">{row.productTitle ?? row.sku ?? row.barcode ?? '—'}</span><small>{[row.variantLabel, row.sku, row.barcode].filter(Boolean).join(' · ') || '—'}</small></td>
          <td className="pci-money">{matched ? <FieldCell change={row.costChange} oldValue={row.oldCost} newValue={row.newCost} format={formatMoney} /> : formatMoney(row.newCost)}</td>
          <td className="pci-money">{matched ? <FieldCell change={row.desiChange} oldValue={row.oldDesi} newValue={row.newDesi} format={formatDesi} /> : formatDesi(row.newDesi)}</td>
          <td><span className={`pci-badge is-${tone}`}>{label}</span>{row.message && <small>{row.message}</small>}</td>
        </tr>
      })}</tbody>
    </table></div>}
    {preview.rowsTruncated && <p className="pci-more">İlk {count(preview.rows.length)} satır gösteriliyor; sorunlu satırlar listenin başında. Kaydet düğmesi tüm {count(preview.changes.length)} değişikliği kaydeder.</p>}
  </div>
}
