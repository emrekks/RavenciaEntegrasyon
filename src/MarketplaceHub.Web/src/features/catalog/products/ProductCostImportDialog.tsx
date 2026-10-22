// Maliyet Excel: the cost template download and the upload that is previewed before any cost is saved.
import { useRef, useState, type DragEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { formatMoney } from '../../../shared/format-money'
import { RrIcon, RrModal } from '../../marketplace/returns-ui'
import './product-cost-import.css'

export type ProductCostSummary = { variantCount: number; missingCostCount: number }
export type ProductCostOutcome = 'UPDATE' | 'CLEAR' | 'UNCHANGED' | 'SKIPPED' | 'UNMATCHED' | 'AMBIGUOUS' | 'ERROR'
export type ProductCostRow = { rowNumber: number; sku: string | null; barcode: string | null; variantId: string | null; productTitle: string | null; variantLabel: string | null; oldCost: number | null; newCost: number | null; outcome: ProductCostOutcome; message: string | null }
export type ProductCostChange = { variantId: string; version: number; cost: number | null }
export type ProductCostPreview = { fileName: string; totalRows: number; updates: number; clears: number; unchanged: number; skipped: number; unmatched: number; ambiguous: number; errors: number; rows: ProductCostRow[]; rowsTruncated: boolean; changes: ProductCostChange[] }
export type ProductCostApplyResult = { updated: number; cleared: number; products: number }

export const productCostSummaryKey = ['products', 'cost-summary'] as const

export function productCostTemplateHref(missingOnly: boolean) {
  return `/api/v1/products/costs/template.xlsx${missingOnly ? '?missingOnly=true' : ''}`
}

const outcomeLabels: Record<ProductCostOutcome, [string, 'ok' | 'warn' | 'bad' | 'info' | 'muted']> = {
  UPDATE: ['Güncellenecek', 'ok'],
  CLEAR: ['Silinecek', 'warn'],
  UNCHANGED: ['Değişmiyor', 'muted'],
  SKIPPED: ['Atlandı', 'muted'],
  UNMATCHED: ['Eşleşmedi', 'warn'],
  AMBIGUOUS: ['Belirsiz', 'bad'],
  ERROR: ['Hatalı', 'bad']
}
export function productCostOutcomeLabel(outcome: ProductCostOutcome) { return outcomeLabels[outcome]?.[0] ?? outcome }

const count = (value: number) => value.toLocaleString('tr-TR')

export function ProductCostImportDialog({ onClose }: { onClose: () => void }) {
  const client = useQueryClient()
  const summary = useQuery({ queryKey: productCostSummaryKey, queryFn: () => hubApi<ProductCostSummary>('/products/costs/summary') })
  const missing = summary.data?.missingCostCount ?? 0
  const [missingOnly, setMissingOnly] = useState<boolean | null>(null)
  const templateMissingOnly = missingOnly ?? missing > 0
  const [dragging, setDragging] = useState(false)
  const [fileError, setFileError] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  // One key per previewed file: a repeated save of the same preview is answered once by the server.
  const applyKey = useRef<string | null>(null)

  const preview = useMutation({
    mutationFn: (file: File) => {
      const body = new FormData()
      body.append('file', file)
      return hubApi<ProductCostPreview>('/products/costs/import', { method: 'POST', body })
    },
    onMutate: () => { applyKey.current = null },
    onSuccess: () => { applyKey.current = newIdempotencyKey('product-costs') }
  })
  const apply = useMutation({
    mutationFn: (changes: ProductCostChange[]) => hubApi<ProductCostApplyResult>('/products/costs/apply', { method: 'POST', headers: { 'Idempotency-Key': applyKey.current ?? newIdempotencyKey('product-costs') }, body: JSON.stringify({ changes }) }),
    onSuccess: async () => {
      await Promise.all([client.invalidateQueries({ queryKey: ['products'] }), client.invalidateQueries({ queryKey: ['product'] })])
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

  const foot = done
    ? <button type="button" className="rr-btn rr-primary" onClick={onClose}>Kapat</button>
    : result
      ? <>
        <button type="button" className="rr-btn" disabled={busy} onClick={restart}>Başka Dosya Yükle</button>
        <button type="button" className="rr-btn rr-primary" disabled={busy || writeCount === 0} onClick={() => apply.mutate(result.changes)}><RrIcon name="check" />{apply.isPending ? 'Kaydediliyor…' : `Maliyetleri Kaydet (${count(writeCount)})`}</button>
      </>
      : <button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button>

  return <RrModal crumb="Ürünler" title="Maliyet Excel" desc="Varyant maliyetlerini Excel şablonuyla toplu girin. Değişiklikler önce önizlenir; siz kaydetmeden hiçbir maliyet değişmez ve pazaryerlerine hiçbir şey gönderilmez." size="lg" onClose={onClose} locked={apply.isPending} foot={foot} modalClass="pci-modal">
    {done ? <div className="pci-done" role="status">
      <span className="pci-done-mark"><RrIcon name="check" size={20} /></span>
      <strong>{done.updated + done.cleared === 0 ? 'Maliyetler zaten güncel' : 'Maliyetler kaydedildi'}</strong>
      <p>{done.updated + done.cleared === 0
        ? 'Dosyadaki maliyetler katalogda zaten aynıydı; hiçbir şey değişmedi.'
        : `${count(done.updated)} varyantın maliyeti güncellendi${done.cleared ? `, ${count(done.cleared)} varyantın maliyeti silindi` : ''} (${count(done.products)} ürün).`}</p>
    </div> : result ? <PreviewView preview={result} problemCount={problemCount} error={apply.error} /> : <>
      {summary.data && missing > 0 && <div className="pci-hint" role="note"><RrIcon name="info" /><span><b>Maliyeti eksik {count(missing)} varyant</b> var (toplam {count(summary.data.variantCount)}). Kâr hesabı için bu varyantlara maliyet girin.</span><a className="rr-btn rr-link" href={productCostTemplateHref(true)} download>Eksikleri İndir</a></div>}
      <ol className="pci-steps">
        <li className="pci-step">
          <span className="pci-step-n" aria-hidden="true">1</span>
          <div className="pci-step-b">
            <strong>Şablonu İndirin</strong>
            <p>Her varyant bir satırdır. <b>Yeni Maliyet</b> sütununa 1 adetin Türk lirası maliyetini yazın: ürün kartındaki “Maliyet” alanıyla aynı değer. Boş bıraktığınız satırlar değişmez; maliyeti silmek için <b>-</b> yazın.</p>
            <div className="pci-step-acts">
              <label className="pci-check"><input type="checkbox" checked={templateMissingOnly} onChange={event => setMissingOnly(event.target.checked)} />Yalnız Maliyeti Eksik Varyantlar</label>
              <a className="rr-btn" href={productCostTemplateHref(templateMissingOnly)} download><RrIcon name="export" />Şablonu İndir</a>
            </div>
          </div>
        </li>
        <li className="pci-step">
          <span className="pci-step-n" aria-hidden="true">2</span>
          <div className="pci-step-b">
            <strong>Doldurduğunuz Dosyayı Yükleyin</strong>
            <div className={`pci-drop${dragging ? ' is-dragging' : ''}`} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
              <RrIcon name="upload" size={20} />
              <b>{preview.isPending ? 'Dosya okunuyor…' : 'Dosyayı buraya bırakın'}</b>
              <span>.xlsx ya da .csv · en fazla 10 MiB, 20.000 satır</span>
              <button type="button" className="rr-btn" disabled={preview.isPending} onClick={() => input.current?.click()}>Dosya Seç</button>
              <input ref={input} type="file" accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" hidden aria-label="Maliyet dosyası" onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
            </div>
            {(fileError || preview.error) && <p className="pci-error" role="alert">{fileError ?? (preview.error instanceof Error ? preview.error.message : 'Dosya okunamadı.')}</p>}
          </div>
        </li>
      </ol>
    </>}
  </RrModal>
}

function PreviewView({ preview, problemCount, error }: { preview: ProductCostPreview; problemCount: number; error: unknown }) {
  const stats: Array<[string, number, string]> = [
    ['Güncellenecek', preview.updates, 'ok'],
    ['Silinecek', preview.clears, 'warn'],
    ['Değişmiyor', preview.unchanged, 'muted'],
    ['Boş / Atlanan', preview.skipped, 'muted'],
    ['Eşleşmedi', preview.unmatched, preview.unmatched ? 'warn' : 'muted'],
    ['Belirsiz', preview.ambiguous, preview.ambiguous ? 'bad' : 'muted'],
    ['Hatalı', preview.errors, preview.errors ? 'bad' : 'muted']
  ]
  return <div className="pci-preview">
    <p className="pci-file"><RrIcon name="doc" /><span><b>{preview.fileName}</b> · {count(preview.totalRows)} satır okundu</span></p>
    <div className="pci-stats" role="list" aria-label="Önizleme özeti">{stats.map(([label, value, tone]) => <div key={label} role="listitem" className={`pci-stat is-${tone}`}><small>{label}</small><strong>{count(value)}</strong></div>)}</div>
    {problemCount > 0 && <p className="pci-note" role="note">Eşleşmeyen, belirsiz ve hatalı satırlar kaydedilmez. Düzeltmek için dosyayı değiştirip yeniden yükleyin; diğer satırları şimdi kaydedebilirsiniz.</p>}
    {error ? <p className="pci-error" role="alert">{error instanceof Error ? error.message : 'Maliyetler kaydedilemedi.'}</p> : null}
    {preview.rows.length === 0 ? <p className="pci-empty">Yeni Maliyet sütunu dolu satır yok; kaydedilecek bir şey bulunmadı.</p> : <div className="pci-tblwrap"><table className="pci-tbl">
      <thead><tr><th scope="col">Satır</th><th scope="col">Ürün</th><th scope="col">Eski Maliyet</th><th scope="col">Yeni Maliyet</th><th scope="col">Durum</th></tr></thead>
      <tbody>{preview.rows.map(row => {
        const [label, tone] = outcomeLabels[row.outcome] ?? [row.outcome, 'muted']
        return <tr key={`${row.rowNumber}-${row.outcome}`}>
          <td className="pci-num">{row.rowNumber}</td>
          <td><span className="pci-title">{row.productTitle ?? row.sku ?? row.barcode ?? '—'}</span><small>{[row.variantLabel, row.sku, row.barcode].filter(Boolean).join(' · ') || '—'}</small></td>
          <td className="pci-money">{formatMoney(row.oldCost)}</td>
          <td className="pci-money">{row.outcome === 'CLEAR' ? 'Silinecek' : formatMoney(row.newCost)}</td>
          <td><span className={`pci-badge is-${tone}`}>{label}</span>{row.message && <small>{row.message}</small>}</td>
        </tr>
      })}</tbody>
    </table></div>}
    {preview.rowsTruncated && <p className="pci-more">İlk {count(preview.rows.length)} satır gösteriliyor; sorunlu satırlar listenin başında. Kaydet düğmesi tüm {count(preview.changes.length)} değişikliği kaydeder.</p>}
  </div>
}
