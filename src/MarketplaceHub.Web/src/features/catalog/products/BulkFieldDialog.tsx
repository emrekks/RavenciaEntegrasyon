// Ürünler › Toplu İşlemler › Toplu Değer Ata (owner, 12.11.2026): at the top the field to set (Atanacak Alan), below it
// that field's control with the product editor's checks, in the footer one line naming what will be written. Model
// fields go to every colour card of the selected models, desi to every variant, güvenlik stoğu to every marketplace
// record of every variant. Only that field is written; nothing is sent to a marketplace.
import { useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { TitleHint } from '../../../shared/components'
import { PanelSelect } from '../../../shared/PanelSelect'
import { sanitizeRichText } from '../../../shared/security/sanitizeHtml'
import { RrModal } from '../../marketplace/returns-ui'
import type { ChunkRunResult } from '../bulk-status-run'
import { TURKISH_VAT_RATES } from '../product-editor-state'
import type { Brand, Category } from '../shared'
import {
  bulkFieldChunks, bulkFieldInitialValues, bulkFieldIssue, bulkFieldLabel, bulkFieldOptions, bulkFieldPayloadValue, bulkFieldResultMessage, bulkFieldSummary,
  bulkFieldValueLabel, bulkStatusOptions, bulkVatInclusionOptions, runBulkFieldChunks, type BulkFieldKey, type BulkFieldResponse
} from '../bulk-field-run'
import './bulk-field-dialog.css'

const FIELD_HINTS: Partial<Record<BulkFieldKey, string>> = {
  vatRate: 'Ürün kartlarının KDV oranı; pazar yerlerindeki ilanların KDV’si ve fiyatı değişmez.',
  vatInclusion: 'Ürün kartlarının KDV dahil bilgisi; ilanların fiyatı değişmez.',
  desi: 'Her rengin her bedenine yazılır. Ondalık girilebilir (ör. 0,5).',
  warrantyMonths: '0 = garanti yok.',
  deliveryDays: 'Siparişin kargoya teslim süresi, 1-30 gün.',
  status: 'Toplu Satışa Aç / Kapat ile aynı: taslak kartlar değişmez.',
  safetyStock: 'Varyantların pazar yeri kayıtlarına yazılır; kaydı olmayan varyant listelenir.',
  description: 'Ürün düzenleme ekranındaki gibi temizlenmiş HTML olarak kaydedilir.'
}

export function BulkFieldDialog({ groups, cardCount, variantCount, onClose, onDone }: {
  groups: ReadonlyArray<ReadonlyArray<string>>
  cardCount: number
  variantCount: number
  onClose: () => void
  onDone: (outcome: { title: string; message: string; kind: 'success' | 'error' }, result: ChunkRunResult) => Promise<void> | void
}) {
  const [field, setField] = useState<BulkFieldKey>('vatRate')
  const [values, setValues] = useState<Record<BulkFieldKey, string>>(bulkFieldInitialValues)
  const [error, setError] = useState('')
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null)
  const running = useRef(false)
  const value = values[field]
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => loadAllPages<Brand>('/catalog/brands'), enabled: field === 'brandId' })
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => loadAllPages<Category>('/catalog/categories'), enabled: field === 'categoryId' })
  const activeBrands = useMemo(() => (brands.data?.items ?? []).filter(item => item.isActive), [brands.data])
  const leafCategories = useMemo(() => (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive), [categories.data])
  const summary = bulkFieldSummary(cardCount, variantCount, field, bulkFieldValueLabel(field, value, { brands: activeBrands, categories: leafCategories }))

  function update(next: string) {
    setValues(current => ({ ...current, [field]: next }))
    setError('')
  }

  async function apply() {
    const issue = bulkFieldIssue(field, value)
    if (issue) { setError(issue); return }
    if (running.current || !groups.length) return
    running.current = true
    const chunks = bulkFieldChunks(groups)
    const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0)
    setProgress({ done: 0, total })
    const counts: BulkFieldResponse = { updatedCount: 0, unchangedCount: 0, variantCount: 0, failures: [] }
    const payloadValue = bulkFieldPayloadValue(field, value)
    try {
      const result = await runBulkFieldChunks(chunks, async productIds => {
        const response = await hubApi<BulkFieldResponse>('/products/bulk-field', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productIds, field, value: payloadValue }) })
        counts.updatedCount += response?.updatedCount ?? 0
        counts.unchangedCount += response?.unchangedCount ?? 0
        counts.variantCount += response?.variantCount ?? 0
        counts.failures.push(...(response?.failures ?? []))
      }, (done, all) => setProgress({ done, total: all }))
      await onDone(bulkFieldResultMessage(result, counts, field), result)
    } finally {
      running.current = false
      setProgress(null)
    }
  }

  const busy = progress != null
  const label = bulkFieldLabel(field)
  const invalid = error ? true : undefined
  let control
  switch (field) {
    case 'vatRate':
      control = <PanelSelect ariaLabel={label} value={value} disabled={busy} width={200} placeholder="Seçin" className={error ? 'is-invalid' : undefined} options={TURKISH_VAT_RATES.map(rate => ({ value: rate, label: `%${rate}` }))} onChange={update} />
      break
    case 'vatInclusion':
      control = <PanelSelect ariaLabel={label} value={value} disabled={busy} width={200} options={bulkVatInclusionOptions} onChange={update} />
      break
    case 'status':
      control = <PanelSelect ariaLabel={label} value={value} disabled={busy} width={200} options={bulkStatusOptions} onChange={update} />
      break
    case 'brandId':
      control = <PanelSelect ariaLabel={label} value={value} disabled={busy || brands.isLoading} width={200} placeholder={brands.isLoading ? 'Markalar yükleniyor…' : 'Marka Seçin'} className={error ? 'is-invalid' : undefined} options={activeBrands.map(item => ({ value: item.id, label: item.name }))} onChange={update} />
      break
    case 'categoryId':
      control = <PanelSelect ariaLabel={label} value={value} disabled={busy || categories.isLoading} width={200} placeholder={categories.isLoading ? 'Kategoriler yükleniyor…' : 'Kategori Seçin'} className={error ? 'is-invalid' : undefined} options={leafCategories.map(item => ({ value: item.id, label: item.path }))} onChange={update} />
      break
    case 'desi':
      control = <input className="rr-in" aria-label={label} aria-invalid={invalid} value={value} disabled={busy} onChange={event => update(event.target.value)} type="text" inputMode="decimal" placeholder="ör. 0,5" autoComplete="off" />
      break
    case 'warrantyMonths':
      control = <input className="rr-in" aria-label={label} aria-invalid={invalid} value={value} disabled={busy} onChange={event => update(event.target.value)} type="number" min="0" max="240" step="1" inputMode="numeric" placeholder="0 = garanti yok" />
      break
    case 'deliveryDays':
      control = <input className="rr-in" aria-label={label} aria-invalid={invalid} value={value} disabled={busy} onChange={event => update(event.target.value)} type="number" min="1" max="30" step="1" inputMode="numeric" placeholder="1-30" />
      break
    case 'safetyStock':
      control = <input className="rr-in" aria-label={label} aria-invalid={invalid} value={value} disabled={busy} onChange={event => update(event.target.value)} type="number" min="0" step="1" inputMode="numeric" placeholder="0" />
      break
    case 'description':
      control = <div className="bf-desc">
        <textarea className="rr-in" aria-label="Açıklama HTML kodu" aria-invalid={invalid} value={value} disabled={busy} onChange={event => update(event.target.value)} rows={7} spellCheck={false} placeholder="<p>Ürünün öne çıkan özelliklerini anlatın…</p>" />
        <div className="bf-preview" aria-label="Açıklama önizlemesi"><small>Önizleme</small>{value.trim() ? <div className="bf-preview-body" dangerouslySetInnerHTML={{ __html: sanitizeRichText(value) }} /> : <span className="rr-muted">Yazdığınız açıklama burada görünür.</span>}</div>
      </div>
      break
  }

  return <RrModal crumb="Toplu İşlem" label="Toplu Değer Ata" modalClass="bf-modal" size="md" locked={busy} onClose={onClose}
    title={<>Toplu Değer Ata<TitleHint label="Toplu Değer Ata">Seçilen alan, seçili ürünlerin tüm renk kartlarına ve varyantlarına yazılır; ürünün başka hiçbir bilgisi değişmez. Pazar yerlerine bir şey gönderilmez; değişen kartlar “Platformlara Güncelle” için işaretlenir.</TitleHint></>}
    desc="Seçili ürünlere tek bir alanın değerini ata."
    foot={<><span className="rr-note bf-summary" role="status" title={progress ? undefined : summary}>{progress ? `${progress.done.toLocaleString('tr-TR')} / ${progress.total.toLocaleString('tr-TR')} kayıt gönderildi` : summary}</span><button type="button" className="rr-btn" onClick={onClose} disabled={busy}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => void apply()} disabled={busy}>{busy ? 'Uygulanıyor…' : 'Uygula'}</button></>}>
    <div className="bf-body">
      <label className="rr-fld"><small>Atanacak Alan</small><PanelSelect ariaLabel="Atanacak Alan" value={field} disabled={busy} width={200} options={bulkFieldOptions} onChange={next => { setField(next); setError('') }} /></label>
      <div className="rr-fld" role="group" aria-label={label}><small>{label}</small>{control}{error ? <em className="rr-err" role="alert">{error}</em> : FIELD_HINTS[field] ? <em>{FIELD_HINTS[field]}</em> : null}</div>
    </div>
  </RrModal>
}
