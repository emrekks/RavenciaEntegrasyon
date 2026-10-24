// Ürün içe / dışa aktarma: the products export download and the upload that is previewed before anything is saved.
// The seller picks which fields an upload writes (cost, desi, stock, store prices); a change of the pick previews the
// same file again. Stock is saved like the product editor saves it, so it reaches the stores with stock sends on.
import { useRef, useState, type DragEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../shared/idempotency'
import { formatMoney } from '../../../shared/format-money'
import { RrIcon, RrModal } from '../../marketplace/returns-ui'
import './product-transfer.css'

export type ProductTransferSummary = { variantCount: number; missingCostCount: number; missingDesiCount: number }
export type ProductTransferField = 'COST' | 'DESI' | 'STOCK' | 'PRICES'
export type ProductTransferOutcome = 'UPDATE' | 'UNCHANGED' | 'SKIPPED' | 'UNMATCHED' | 'AMBIGUOUS' | 'ERROR'
export type ProductTransferFieldChange = 'SET' | 'CLEAR' | null
export type ProductTransferPrice = { connectionId: string; storeName: string; oldSalePrice: number; newSalePrice: number; oldListPrice: number; newListPrice: number }
export type ProductTransferRow = {
  rowNumber: number; sku: string | null; barcode: string | null; variantId: string | null; productTitle: string | null; variantLabel: string | null
  oldCost: number | null; newCost: number | null; costChange: ProductTransferFieldChange
  oldDesi: number | null; newDesi: number | null; desiChange: ProductTransferFieldChange
  oldStock: number | null; newStock: number | null; stockChange: ProductTransferFieldChange
  prices: ProductTransferPrice[]
  outcome: ProductTransferOutcome; message: string | null
}
export type ProductTransferChange = {
  variantId: string; version: number; setCost: boolean; cost: number | null; setDesi: boolean; desi: number | null
  stock?: { expectedOnHand: number; onHand: number } | null
  prices?: Array<{ offerId: string; version: number; salePrice: number; listPrice: number }> | null
}
export type ProductTransferPreview = {
  fileName: string; availableFields: ProductTransferField[]; fields: ProductTransferField[]; stores: Array<{ connectionId: string; name: string }>
  totalRows: number; updates: number; costUpdates: number; costClears: number; desiUpdates: number; desiClears: number; stockUpdates: number; priceUpdates: number
  unchanged: number; skipped: number; unmatched: number; ambiguous: number; errors: number
  rows: ProductTransferRow[]; rowsTruncated: boolean; changes: ProductTransferChange[]
}
export type ProductTransferApplyResult = { variants: number; costsUpdated: number; costsCleared: number; desiUpdated: number; desiCleared: number; stocksUpdated: number; pricesUpdated: number; products: number }
export type ProductTransferExportFilter = 'all' | 'cost' | 'desi'

export const productTransferSummaryKey = ['products', 'transfer-summary'] as const
const allFields: ProductTransferField[] = ['COST', 'DESI', 'STOCK', 'PRICES']
const fieldLabels: Record<ProductTransferField, string> = { COST: 'Maliyet', DESI: 'Desi', STOCK: 'Stok', PRICES: 'Mağaza Fiyatları' }
const defaultFields: ProductTransferField[] = ['COST', 'DESI']

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
const formatNumber = (value: number | null) => value == null ? '—' : value.toLocaleString('tr-TR', { maximumFractionDigits: 2 })

export function ProductTransferDialog({ onClose }: { onClose: () => void }) {
  const client = useQueryClient()
  const summary = useQuery({ queryKey: productTransferSummaryKey, queryFn: () => hubApi<ProductTransferSummary>('/products/transfer/summary') })
  const [filter, setFilter] = useState<ProductTransferExportFilter>('all')
  const [fields, setFields] = useState<ProductTransferField[]>(defaultFields)
  const [file, setFile] = useState<File | null>(null)
  const [dragging, setDragging] = useState(false)
  const [fileError, setFileError] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  // One id per preview: a repeated save of the same preview is answered once by the server, and a refused save gets a
  // fresh key on the next try.
  const previewId = useRef<string | null>(null)

  const preview = useMutation({
    mutationFn: ({ upload, picked }: { upload: File; picked: ProductTransferField[] }) => {
      const body = new FormData()
      body.append('file', upload)
      body.append('fields', picked.join(','))
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

  const choose = (chosen: File | undefined) => {
    if (!chosen) return
    const name = chosen.name.toLowerCase()
    if (!name.endsWith('.xlsx') && !name.endsWith('.csv')) { setFileError('Yalnız .xlsx ya da .csv dosyası yüklenebilir.'); return }
    if (chosen.size > 10 * 1024 * 1024) { setFileError('Dosya en fazla 10 MiB olabilir.'); return }
    setFileError(null)
    setFile(chosen)
    apply.reset()
    preview.mutate({ upload: chosen, picked: fields })
  }
  const toggleField = (field: ProductTransferField) => {
    const next = fields.includes(field) ? fields.filter(item => item !== field) : allFields.filter(item => item === field || fields.includes(item))
    setFields(next)
    if (file && next.length > 0) { apply.reset(); preview.mutate({ upload: file, picked: next }) }
  }
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  const restart = () => { preview.reset(); apply.reset(); setFile(null); setFileError(null) }

  const result = preview.data
  const done = apply.data
  const writeCount = result && fields.length > 0 ? result.changes.length : 0
  const problemCount = result ? result.unmatched + result.ambiguous + result.errors : 0
  const busy = preview.isPending || apply.isPending
  const missingCost = summary.data?.missingCostCount ?? 0
  const missingDesi = summary.data?.missingDesiCount ?? 0
  const picker = <FieldPicker fields={fields} available={result?.availableFields} disabled={busy} onToggle={toggleField} />

  const foot = done
    ? <button type="button" className="rr-btn rr-primary" onClick={onClose}>Kapat</button>
    : result
      ? <>
        <button type="button" className="rr-btn" disabled={busy} onClick={restart}>Başka Dosya Yükle</button>
        <button type="button" className="rr-btn rr-primary" disabled={busy || writeCount === 0} onClick={() => apply.mutate(result.changes)}><RrIcon name="check" />{apply.isPending ? 'Kaydediliyor…' : `Değişiklikleri Kaydet (${count(writeCount)})`}</button>
      </>
      : <button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button>

  return <RrModal crumb="Ürünler" title="Ürünleri İçe / Dışa Aktar" desc="Ürünlerinizi Excel'e aktarın, düzenleyip geri yükleyin ve hangi alanların panele yazılacağını seçin. Değişiklikler önce önizlenir; siz kaydetmeden hiçbir şey değişmez." size="lg" onClose={onClose} locked={apply.isPending} foot={foot} modalClass="pci-modal">
    {done ? <div className="pci-done" role="status">
      <span className="pci-done-mark"><RrIcon name="check" size={20} /></span>
      <strong>{done.variants === 0 ? 'Değerler zaten güncel' : 'Değişiklikler kaydedildi'}</strong>
      <p>{done.variants === 0 ? 'Dosyadaki değerler panelde zaten aynıydı; hiçbir şey değişmedi.' : appliedText(done)}</p>
    </div> : result ? <PreviewView preview={result} picker={picker} problemCount={problemCount} pending={preview.isPending} error={apply.error ?? preview.error} /> : <>
      {summary.data && (missingCost > 0 || missingDesi > 0) && <div className="pci-hint" role="note"><RrIcon name="info" /><span>Toplam {count(summary.data.variantCount)} varyantın <b>{count(missingCost)} tanesinde maliyet</b>, <b>{count(missingDesi)} tanesinde desi</b> eksik. Kâr ve kargo hesabı bu değerlerle yapılır.</span></div>}
      <ol className="pci-steps">
        <li className="pci-step">
          <span className="pci-step-n" aria-hidden="true">1</span>
          <div className="pci-step-b">
            <strong>Ürünleri Dışa Aktarın</strong>
            <p>Her varyant bir satırdır: stok kodu, barkod, ürün adı, varyant, satış fiyatı, KDV oranı, <b>stok</b>, <b>maliyet</b>, <b>desi</b> ve her mağazanın <b>satış ve liste fiyatı</b>. Boş hücre değişmez; bir maliyeti ya da desiyi silmek için <b>-</b> yazın.</p>
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
              <button type="button" className="rr-btn" disabled={preview.isPending || fields.length === 0} onClick={() => input.current?.click()}>Dosya Seç</button>
              <input ref={input} type="file" accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" hidden aria-label="Ürün dosyası" onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
            </div>
            {picker}
            {(fileError || preview.error) && <p className="pci-error" role="alert">{fileError ?? (preview.error instanceof Error ? preview.error.message : 'Dosya okunamadı.')}</p>}
          </div>
        </li>
      </ol>
    </>}
  </RrModal>
}

function FieldPicker({ fields, available, disabled, onToggle }: { fields: ProductTransferField[]; available?: ProductTransferField[]; disabled: boolean; onToggle: (field: ProductTransferField) => void }) {
  return <fieldset className="pci-fields">
    <legend>Panele Aktarılacak Alanlar</legend>
    <div className="pci-field-list">{allFields.map(field => {
      const missing = available != null && !available.includes(field)
      return <label key={field} className={`pci-check${missing ? ' is-missing' : ''}`} title={missing ? 'Dosyada bu sütun yok' : undefined}>
        <input type="checkbox" checked={fields.includes(field)} disabled={disabled || missing} onChange={() => onToggle(field)} />{fieldLabels[field]}
      </label>
    })}</div>
    {fields.length === 0 && <p className="pci-error" role="alert">En az bir alan seçin.</p>}
    {fields.includes('STOCK') && <p className="pci-note" role="note"><b>Stok</b>, ürün sayfasındaki stok değişikliği gibi kaydedilir: stok gönderimi açık mağazalara yeni stok gönderilir.</p>}
    {fields.includes('PRICES') && <p className="pci-note" role="note"><b>Mağaza fiyatları</b> panele kaydedilir; bu işlem kendisi fiyat göndermez, ancak sonraki stok ya da fiyat gönderimi yeni fiyatı mağazaya taşır. Yalnız fiyat kaydı olan mağazalar güncellenir.</p>}
  </fieldset>
}

function appliedText(done: ProductTransferApplyResult) {
  const parts = [
    done.costsUpdated ? `${count(done.costsUpdated)} maliyet güncellendi` : '',
    done.costsCleared ? `${count(done.costsCleared)} maliyet silindi` : '',
    done.desiUpdated ? `${count(done.desiUpdated)} desi güncellendi` : '',
    done.desiCleared ? `${count(done.desiCleared)} desi silindi` : '',
    done.stocksUpdated ? `${count(done.stocksUpdated)} stok güncellendi` : '',
    done.pricesUpdated ? `${count(done.pricesUpdated)} mağaza fiyatı güncellendi` : ''
  ].filter(Boolean)
  return `${count(done.variants)} varyant kaydedildi (${count(done.products)} ürün): ${parts.join(', ')}.`
}

function FieldCell({ change, oldValue, newValue, format }: { change: ProductTransferFieldChange; oldValue: number | null; newValue: number | null; format: (value: number | null) => string }) {
  if (change === 'SET') return <><span className="pci-old">{format(oldValue)}</span><span className="pci-arrow" aria-label="yeni değer">→</span><b>{format(newValue)}</b></>
  if (change === 'CLEAR') return <><span className="pci-old">{format(oldValue)}</span><span className="pci-arrow" aria-label="yeni değer">→</span><b className="pci-clear">Silinecek</b></>
  return <span className="pci-same">{oldValue == null ? '—' : format(oldValue)}</span>
}

function PricesCell({ prices }: { prices: ProductTransferPrice[] }) {
  if (prices.length === 0) return <span className="pci-same">—</span>
  return <>{prices.map(price => <span key={price.connectionId} className="pci-price">
    <small>{price.storeName}</small>
    {price.oldSalePrice !== price.newSalePrice && <span><span className="pci-old">{formatMoney(price.oldSalePrice)}</span><span className="pci-arrow" aria-label="yeni satış fiyatı">→</span><b>{formatMoney(price.newSalePrice)}</b></span>}
    {price.oldListPrice !== price.newListPrice && <span>Liste: <span className="pci-old">{formatMoney(price.oldListPrice)}</span><span className="pci-arrow" aria-label="yeni liste fiyatı">→</span><b>{formatMoney(price.newListPrice)}</b></span>}
  </span>)}</>
}

function PreviewView({ preview, picker, problemCount, pending, error }: { preview: ProductTransferPreview; picker: ReactNode; problemCount: number; pending: boolean; error: unknown }) {
  const picked = new Set(preview.fields)
  const changed = (value: number) => value ? 'ok' : 'muted'
  const stats: Array<[string, number, string]> = [
    ['Güncellenecek Varyant', preview.updates, 'ok'],
    ...(picked.has('COST') ? [['Maliyet Değişen', preview.costUpdates + preview.costClears, changed(preview.costUpdates + preview.costClears)] as [string, number, string]] : []),
    ...(picked.has('DESI') ? [['Desi Değişen', preview.desiUpdates + preview.desiClears, changed(preview.desiUpdates + preview.desiClears)] as [string, number, string]] : []),
    ...(picked.has('STOCK') ? [['Stok Değişen', preview.stockUpdates, changed(preview.stockUpdates)] as [string, number, string]] : []),
    ...(picked.has('PRICES') ? [['Fiyat Değişen', preview.priceUpdates, changed(preview.priceUpdates)] as [string, number, string]] : []),
    ['Değişmiyor', preview.unchanged, 'muted'],
    ['Boş / Atlanan', preview.skipped, 'muted'],
    ['Eşleşmedi', preview.unmatched, preview.unmatched ? 'warn' : 'muted'],
    ['Belirsiz', preview.ambiguous, preview.ambiguous ? 'bad' : 'muted'],
    ['Hatalı', preview.errors, preview.errors ? 'bad' : 'muted']
  ]
  const clears = (picked.has('COST') ? preview.costClears : 0) + (picked.has('DESI') ? preview.desiClears : 0)
  return <div className="pci-preview" aria-busy={pending}>
    <p className="pci-file"><RrIcon name="doc" /><span><b>{preview.fileName}</b> · {count(preview.totalRows)} satır okundu{pending ? ' · yeniden önizleniyor…' : ''}</span></p>
    <div className="pci-stats" role="list" aria-label="Önizleme özeti">{stats.map(([label, value, tone]) => <div key={label} role="listitem" className={`pci-stat is-${tone}`}><small>{label}</small><strong>{count(value)}</strong></div>)}</div>
    {picker}
    {clears > 0 && <p className="pci-note" role="note">{[picked.has('COST') && preview.costClears ? `${count(preview.costClears)} varyantın maliyeti` : '', picked.has('DESI') && preview.desiClears ? `${count(preview.desiClears)} varyantın desisi` : ''].filter(Boolean).join(' ve ')} silinecek.</p>}
    {problemCount > 0 && <p className="pci-note" role="note">Eşleşmeyen, belirsiz ve hatalı satırlar kaydedilmez. Düzeltmek için dosyayı değiştirip yeniden yükleyin; diğer satırları şimdi kaydedebilirsiniz.</p>}
    {error ? <p className="pci-error" role="alert">{error instanceof Error ? error.message : 'Değişiklikler kaydedilemedi.'}</p> : null}
    {preview.rows.length === 0 ? <p className="pci-empty">Seçtiğiniz alanlarda dolu satır yok; kaydedilecek bir şey bulunmadı.</p> : <div className="pci-tblwrap"><table className="pci-tbl">
      <thead><tr>
        <th scope="col">Satır</th><th scope="col">Ürün</th>
        {picked.has('COST') && <th scope="col">Maliyet</th>}
        {picked.has('DESI') && <th scope="col">Desi</th>}
        {picked.has('STOCK') && <th scope="col">Stok</th>}
        {picked.has('PRICES') && <th scope="col">Mağaza Fiyatları</th>}
        <th scope="col">Durum</th>
      </tr></thead>
      <tbody>{preview.rows.map(row => {
        const [label, tone] = outcomeLabels[row.outcome] ?? [row.outcome, 'muted']
        const matched = row.variantId != null
        return <tr key={`${row.rowNumber}-${row.outcome}`}>
          <td className="pci-num">{row.rowNumber}</td>
          <td><span className="pci-title">{row.productTitle ?? row.sku ?? row.barcode ?? '—'}</span><small>{[row.variantLabel, row.sku, row.barcode].filter(Boolean).join(' · ') || '—'}</small></td>
          {picked.has('COST') && <td className="pci-money">{matched ? <FieldCell change={row.costChange} oldValue={row.oldCost} newValue={row.newCost} format={formatMoney} /> : formatMoney(row.newCost)}</td>}
          {picked.has('DESI') && <td className="pci-money">{matched ? <FieldCell change={row.desiChange} oldValue={row.oldDesi} newValue={row.newDesi} format={formatNumber} /> : formatNumber(row.newDesi)}</td>}
          {picked.has('STOCK') && <td className="pci-money">{matched ? <FieldCell change={row.stockChange} oldValue={row.oldStock} newValue={row.newStock} format={formatNumber} /> : formatNumber(row.newStock)}</td>}
          {picked.has('PRICES') && <td className="pci-prices"><PricesCell prices={row.prices ?? []} /></td>}
          <td><span className={`pci-badge is-${tone}`}>{label}</span>{row.message && <small>{row.message}</small>}</td>
        </tr>
      })}</tbody>
    </table></div>}
    {preview.rowsTruncated && <p className="pci-more">İlk {count(preview.rows.length)} satır gösteriliyor; sorunlu satırlar listenin başında. Kaydet düğmesi tüm {count(preview.changes.length)} değişikliği kaydeder.</p>}
  </div>
}
