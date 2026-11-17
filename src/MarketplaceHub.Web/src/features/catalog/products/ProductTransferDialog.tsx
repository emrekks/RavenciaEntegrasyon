// Ürün içe / dışa aktarma: the products export download and the upload that is previewed before anything is saved.
// The seller picks which exported columns an upload writes, in groups (product information, price and VAT, stock and
// logistics, cost, store prices); a change of the pick previews the same file again. A brand is the model's: it is
// found or opened by name like the other imports do and goes to every colour card of the model. Rows are matched by stock code
// (then barcode); the stock code itself is never written. Stock is saved like the product editor saves it, so it
// reaches the stores with stock sends on; every other field is a panel write that sends nothing by itself.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useRef, useState, type DragEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../shared/idempotency'
import { formatMoney } from '../../../shared/format-money'
import { TitleHint } from '../../../shared/components'
import { RrIcon, RrModal } from '../../../shared/ui/rr-components'
import './product-transfer.css'
import { PanelSelect } from '../../../shared/PanelSelect'
import { queryKeys } from '../../../shared/api/query-keys'

export type ProductTransferSummary = { variantCount: number; missingCostCount: number; missingDesiCount: number }
export type ProductTransferField = 'TITLE' | 'DESCRIPTION' | 'BRAND' | 'BARCODE' | 'MODEL_CODE' | 'PANEL_PRICES' | 'VAT' | 'COST' | 'DESI' | 'STOCK' | 'PRICES'
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
  newTitle?: string | null; titleChange?: ProductTransferFieldChange
  oldBarcode?: string | null; newBarcode?: string | null; barcodeChange?: ProductTransferFieldChange
  oldModelCode?: string | null; newModelCode?: string | null; modelCodeChange?: ProductTransferFieldChange
  oldPanelSalePrice?: number | null; newPanelSalePrice?: number | null; oldPanelListPrice?: number | null; newPanelListPrice?: number | null; panelPriceChange?: ProductTransferFieldChange
  oldVatRate?: number | null; newVatRate?: number | null; vatChange?: ProductTransferFieldChange
  oldBrand?: string | null; newBrand?: string | null; brandChange?: ProductTransferFieldChange
  // The description is shown by its length only, never as HTML in the table.
  descriptionChange?: ProductTransferFieldChange; newDescriptionLength?: number | null
}
type TransferPreviewAnswer = ApiAnswer<'postProductsTransferImport'>
// The changes go back as the apply body as the answer gave them (the apply request reads numbers or strings).
export type ProductTransferChange = TransferPreviewAnswer['changes'][number]
export type ProductTransferProductChange = NonNullable<TransferPreviewAnswer['productChanges']>[number]
// POST /products/transfer/import, sent back as the apply body. CONTRACT: the API declares its numbers (versions, cost,
// desi, stock, VAT, prices) as "number or string" because the schema is the apply request's too, which reads either; an
// answer writes numbers. It declares the fields as strings; ProductTransferFields holds only ProductTransferField's codes.
export type ProductTransferPreview = Omit<TransferPreviewAnswer, 'availableFields' | 'fields' | 'rows'> & { availableFields: ProductTransferField[]; fields: ProductTransferField[]; rows: ProductTransferRow[] }

const transferFields: ReadonlySet<string> = new Set<ProductTransferField>(['TITLE', 'DESCRIPTION', 'BRAND', 'BARCODE', 'MODEL_CODE', 'PANEL_PRICES', 'VAT', 'COST', 'DESI', 'STOCK', 'PRICES'])
const transferOutcomes: ReadonlySet<string> = new Set<ProductTransferOutcome>(['UPDATE', 'UNCHANGED', 'SKIPPED', 'UNMATCHED', 'AMBIGUOUS', 'ERROR'])
const isTransferField = (code: string): code is ProductTransferField => transferFields.has(code)
const isTransferOutcome = (code: string): code is ProductTransferOutcome => transferOutcomes.has(code)
// A change kind the panel does not know still changes something: it is shown as a set value, never as no change.
const fieldChange = (kind: string | null): ProductTransferFieldChange => kind === null ? null : kind === 'CLEAR' ? 'CLEAR' : 'SET'

/**
 * The preview as the dialog reads it. A field code the panel has no name for is left out of the picks (it cannot be
 * offered); a row outcome it does not know is shown as an error with the code in its message, so it is never counted
 * as an update or as unchanged.
 */
export function readTransferPreview(raw: TransferPreviewAnswer): ProductTransferPreview {
  return {
    ...raw,
    availableFields: raw.availableFields.filter(isTransferField),
    fields: raw.fields.filter(isTransferField),
    rows: raw.rows.map(row => {
      return {
        ...row,
        outcome: isTransferOutcome(row.outcome) ? row.outcome : 'ERROR',
        message: isTransferOutcome(row.outcome) ? row.message : [`Bilinmeyen sonuç: ${row.outcome}`, row.message].filter(Boolean).join(' · '),
        costChange: fieldChange(row.costChange), desiChange: fieldChange(row.desiChange), stockChange: fieldChange(row.stockChange),
        titleChange: fieldChange(row.titleChange), barcodeChange: fieldChange(row.barcodeChange), modelCodeChange: fieldChange(row.modelCodeChange),
        panelPriceChange: fieldChange(row.panelPriceChange), vatChange: fieldChange(row.vatChange), brandChange: fieldChange(row.brandChange),
        descriptionChange: fieldChange(row.descriptionChange ?? null)
      }
    })
  }
}
export type ProductTransferApplyResult = {
  variants: number; costsUpdated: number; costsCleared: number; desiUpdated: number; desiCleared: number; stocksUpdated: number; pricesUpdated: number; products: number
  titlesUpdated?: number; barcodesUpdated?: number; modelCodesUpdated?: number; modelCodesCleared?: number; panelPricesUpdated?: number; vatUpdated?: number; brandsUpdated?: number; brandsCreated?: number
  // KATALOG-14 phase D5b: the colour cards of the models a model field (name, VAT, brand, desi) reached.
  modelColours?: number
  descriptionsUpdated?: number
}
export type ProductTransferExportFilter = 'all' | 'cost' | 'desi'

export const productTransferSummaryKey = queryKeys.products('transfer-summary')
// The exported columns an upload can write, grouped as the seller thinks of them. A group of one field is that field.
export const productTransferFieldGroups: ReadonlyArray<{ label: string; fields: ProductTransferField[] }> = [
  { label: 'Ürün Bilgileri', fields: ['TITLE', 'DESCRIPTION', 'BRAND', 'BARCODE', 'MODEL_CODE'] },
  { label: 'Fiyat ve Vergi', fields: ['PANEL_PRICES', 'VAT'] },
  { label: 'Stok ve Lojistik', fields: ['STOCK', 'DESI'] },
  { label: 'Maliyet', fields: ['COST'] },
  { label: 'Mağaza Fiyatları', fields: ['PRICES'] }
]
// The order the server lists the fields in (ProductTransferFields.All); a pick is sent in it.
const allFields: ProductTransferField[] = ['TITLE', 'DESCRIPTION', 'BRAND', 'BARCODE', 'MODEL_CODE', 'PANEL_PRICES', 'VAT', 'COST', 'DESI', 'STOCK', 'PRICES']
const fieldLabels: Record<ProductTransferField, string> = { TITLE: 'Ürün Adı', DESCRIPTION: 'Açıklama', BRAND: 'Marka', BARCODE: 'Barkod', MODEL_CODE: 'Model Kodu', PANEL_PRICES: 'Panel Fiyatı', VAT: 'KDV Oranı', COST: 'Maliyet', DESI: 'Desi', STOCK: 'Stok', PRICES: 'Mağaza Fiyatları' }
const defaultFields: ProductTransferField[] = ['COST', 'DESI']

export function productTransferExportHref(filter: ProductTransferExportFilter) {
  return `/api/v1/products/transfer/export.xlsx${filter === 'all' ? '' : `?missing=${filter}`}`
}

// The fields picked after a group's select-all: every available field of the group on, or all of them off.
export function toggleProductTransferGroup(picked: readonly ProductTransferField[], group: readonly ProductTransferField[], available?: readonly ProductTransferField[]) {
  const usable = group.filter(field => available == null || available.includes(field))
  const allOn = usable.length > 0 && usable.every(field => picked.includes(field))
  return allFields.filter(field => group.includes(field) ? !allOn && usable.includes(field) : picked.includes(field))
}

const outcomeLabels: Record<ProductTransferOutcome, [string, 'ok' | 'warn' | 'bad' | 'info' | 'muted']> = {
  UPDATE: ['Güncellenecek', 'ok'],
  UNCHANGED: ['Değişmiyor', 'muted'],
  SKIPPED: ['Atlandı', 'muted'],
  UNMATCHED: ['Eşleşmedi', 'warn'],
  AMBIGUOUS: ['Belirsiz', 'bad'],
  ERROR: ['Hatalı', 'bad']
}

const count = (value: number) => value.toLocaleString('tr-TR')
const formatNumber = (value: number | null) => value == null ? '—' : value.toLocaleString('tr-TR', { maximumFractionDigits: 2 })
const formatVat = (value: number | null) => value == null ? '—' : `%${value.toLocaleString('tr-TR', { maximumFractionDigits: 2 })}`
const formatText = (value: string | null) => value ?? '—'

export function ProductTransferDialog({ onClose }: { onClose: () => void }) {
  const client = useQueryClient()
  const summary = useQuery({ queryKey: productTransferSummaryKey, queryFn: ({ signal }) => hubApi<ApiAnswer<'getProductsTransferSummary'>>('/products/transfer/summary', { signal }) })
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
      return hubApi<TransferPreviewAnswer>('/products/transfer/import', { method: 'POST', body }).then(readTransferPreview)
    },
    onMutate: () => { previewId.current = null },
    onSuccess: () => { previewId.current = newIdempotencyKey() }
  })
  const apply = useMutation({
    mutationFn: ({ changes, productChanges }: { changes: ProductTransferChange[]; productChanges: ProductTransferProductChange[] }) => {
      const body = JSON.stringify(productChanges.length ? { changes, productChanges } : { changes })
      const key = versionedIdempotencyKey('product-transfer', previewId.current ?? newIdempotencyKey(), 1, body)
      return sendWithVersionedIdempotencyKey(key, idempotencyKey => hubApi<ApiAnswer<'postProductsTransferApply'>>('/products/transfer/apply', { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey }, body }))
    },
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: queryKeys.products() }),
        client.invalidateQueries({ queryKey: queryKeys.product() }),
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
  const pick = (next: ProductTransferField[]) => {
    setFields(next)
    if (file && next.length > 0) { apply.reset(); preview.mutate({ upload: file, picked: next }) }
  }
  const toggleField = (field: ProductTransferField) => pick(fields.includes(field) ? fields.filter(item => item !== field) : allFields.filter(item => item === field || fields.includes(item)))
  const toggleGroup = (group: readonly ProductTransferField[]) => pick(toggleProductTransferGroup(fields, group, preview.data?.availableFields))
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  const restart = () => { preview.reset(); apply.reset(); setFile(null); setFileError(null) }

  const result = preview.data
  const done = apply.data
  const productChanges = result?.productChanges ?? []
  const writeCount = result && fields.length > 0 && (result.changes.length > 0 || productChanges.length > 0) ? result.updates : 0
  const problemCount = result ? result.unmatched + result.ambiguous + result.errors : 0
  const busy = preview.isPending || apply.isPending
  const missingCost = summary.data?.missingCostCount ?? 0
  const missingDesi = summary.data?.missingDesiCount ?? 0
  const picker = <FieldPicker fields={fields} available={result?.availableFields} disabled={busy} onToggle={toggleField} onToggleGroup={toggleGroup} />

  // As in the other windows: a spacer note on the left, the actions on the right.
  const foot = <>
    <span className="rr-note" />
    {done
      ? <button type="button" className="rr-btn rr-primary" onClick={onClose}>Kapat</button>
      : result
        ? <>
          <button type="button" className="rr-btn" disabled={busy} onClick={restart}>Başka Dosya Yükle</button>
          <button type="button" className="rr-btn rr-primary" disabled={busy || writeCount === 0} onClick={() => apply.mutate({ changes: result.changes, productChanges })}><RrIcon name="check" />{apply.isPending ? 'Kaydediliyor…' : `Değişiklikleri Kaydet (${count(writeCount)})`}</button>
        </>
        : <button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button>}
  </>

  return <RrModal crumb="Ürünler" label="Ürünleri İçe / Dışa Aktar" title={<>Ürünleri İçe / Dışa Aktar<TitleHint label="Ürünleri İçe / Dışa Aktar">Ürünleri Excel’e aktarın, düzenleyip geri yükleyin. Değişiklikler önce önizlenir; siz kaydetmeden hiçbir şey değişmez.</TitleHint></>} size="lg" onClose={onClose} locked={apply.isPending} foot={foot} modalClass="pci-modal">
    {done ? <div className="pci-done" role="status">
      <span className="pci-done-mark"><RrIcon name="check" size={20} /></span>
      <strong>{appliedTotal(done) === 0 ? 'Değerler zaten güncel' : 'Değişiklikler kaydedildi'}</strong>
      <p>{appliedTotal(done) === 0 ? 'Dosyadaki değerler panelde zaten aynıydı; hiçbir şey değişmedi.' : appliedText(done)}</p>
    </div> : result ? <PreviewView preview={result} picker={picker} problemCount={problemCount} pending={preview.isPending} error={apply.error ?? preview.error} /> : <>
      <ol className="pci-steps">
        <li className="pci-step">
          <span className="pci-step-n" aria-hidden="true">1</span>
          <div className="pci-step-b">
            <strong>Ürünleri Dışa Aktarın<TitleHint label="Ürünleri Dışa Aktarın">Her varyant bir satırdır; stok, maliyet, desi, fiyat, KDV ve her mağazanın <b>satış ve liste fiyatı</b> da yer alır. Boş hücre değişmez; maliyeti, desiyi ya da model kodunu silmek için <b>-</b> yazın.</TitleHint></strong>
            <div className="pci-step-acts">
              <div className="pci-field"><span>Varyantlar</span>
                <PanelSelect className="pci-select" ariaLabel="Varyantlar" width={220} value={filter} onChange={setFilter} options={[
                  { value: 'all', label: `Tüm Varyantlar${summary.data ? ` (${count(summary.data.variantCount)})` : ''}` },
                  { value: 'cost', label: `Maliyeti Eksik Olanlar${summary.data ? ` (${count(missingCost)})` : ''}` },
                  { value: 'desi', label: `Desisi Eksik Olanlar${summary.data ? ` (${count(missingDesi)})` : ''}` }
                ]} />
              </div>
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

function FieldCheck({ field, label, fields, available, disabled, onToggle }: { field: ProductTransferField; label: string; fields: ProductTransferField[]; available?: ProductTransferField[]; disabled: boolean; onToggle: (field: ProductTransferField) => void }) {
  const missing = available != null && !available.includes(field)
  return <label className={`pci-check${missing ? ' is-missing' : ''}`} title={missing ? 'Dosyada bu sütun yok' : undefined}>
    <input type="checkbox" checked={fields.includes(field)} disabled={disabled || missing} onChange={() => onToggle(field)} />{label}
  </label>
}

function FieldPicker({ fields, available, disabled, onToggle, onToggleGroup }: { fields: ProductTransferField[]; available?: ProductTransferField[]; disabled: boolean; onToggle: (field: ProductTransferField) => void; onToggleGroup: (group: readonly ProductTransferField[]) => void }) {
  return <fieldset className="pci-fields">
    <legend>Panele Aktarılacak Alanlar<TitleHint label="Panele Aktarılacak Alanlar">Satırlar <b>Stok Kodu</b> ile, yoksa <b>Barkod</b> ile eşleştirilir; barkod aktarılırken yalnız stok kodu ile eşleştirilir. Stok kodu değiştirilemez, barkod bir varyanta aittir ve silinemez. Varyant, kategori ve durum sütunları yalnız bilgi içindir; <b>ürün adı</b> ürüne aittir, aynı ürünün tüm satırlarına aynı adı yazın. <b>Açıklama</b> modele aittir: değişen açıklama modelin tüm renklerine yazılır; boş hücre açıklamayı değiştirmez, silmez. <b>Marka</b> modele aittir: modelin tüm renklerine yazılır; panelde aynı adlı marka yoksa açılır, kapatılmış marka yeniden açılmaz. Satışa açık ya da kapalı üründe desi ve model kodu silinemez. <b>Panel fiyatı ve KDV</b> yalnız panele kaydedilir; pazaryerine fiyat gönderilmez.</TitleHint></legend>
    <div className="pci-groups">{productTransferFieldGroups.map(group => {
      if (group.fields.length === 1) return <div key={group.label} className="pci-group" role="group" aria-label={group.label}>
        <FieldCheck field={group.fields[0]} label={group.label} fields={fields} available={available} disabled={disabled} onToggle={onToggle} />
      </div>
      const usable = group.fields.filter(field => available == null || available.includes(field))
      const on = usable.filter(field => fields.includes(field)).length
      return <div key={group.label} className="pci-group" role="group" aria-label={group.label}>
        <label className={`pci-check pci-group-all${usable.length === 0 ? ' is-missing' : ''}`}>
          <input type="checkbox" checked={usable.length > 0 && on === usable.length} ref={element => { if (element) element.indeterminate = on > 0 && on < usable.length }} disabled={disabled || usable.length === 0} onChange={() => onToggleGroup(group.fields)} aria-label={`${group.label}: tümünü seç`} />{group.label}
        </label>
        <div className="pci-field-list">{group.fields.map(field => <FieldCheck key={field} field={field} label={fieldLabels[field]} fields={fields} available={available} disabled={disabled} onToggle={onToggle} />)}</div>
      </div>
    })}</div>
    {fields.length === 0 && <p className="pci-error" role="alert">En az bir alan seçin.</p>}
    {fields.includes('STOCK') && <p className="pci-note" role="note"><b>Stok</b>, ürün sayfasındaki stok değişikliği gibi kaydedilir: stok gönderimi açık mağazalara yeni stok gönderilir.</p>}
    {fields.includes('PRICES') && <p className="pci-note" role="note"><b>Mağaza fiyatları</b> panele kaydedilir. Mağazanın otomatik fiyat ve stok gönderimi açıksa değişen fiyat o mağazaya hemen gönderilir; kapalıysa gönderilmez, Hızlı Güncelle ya da Platformlara Güncelle ile elle gönderilir. Yalnız fiyat kaydı olan mağazalar güncellenir.</p>}
  </fieldset>
}

function appliedTotal(done: ProductTransferApplyResult) {
  return done.variants + (done.titlesUpdated ?? 0) + (done.descriptionsUpdated ?? 0) + (done.vatUpdated ?? 0) + (done.brandsUpdated ?? 0)
}

function appliedText(done: ProductTransferApplyResult) {
  const parts = [
    done.titlesUpdated ? `${count(done.titlesUpdated)} ürün adı güncellendi` : '',
    done.descriptionsUpdated ? `${count(done.descriptionsUpdated)} açıklama güncellendi` : '',
    done.brandsUpdated ? `${count(done.brandsUpdated)} ürün kartının markası güncellendi${done.brandsCreated ? ` (${count(done.brandsCreated)} yeni marka açıldı)` : ''}` : '',
    done.barcodesUpdated ? `${count(done.barcodesUpdated)} barkod güncellendi` : '',
    done.modelCodesUpdated ? `${count(done.modelCodesUpdated)} model kodu güncellendi` : '',
    done.modelCodesCleared ? `${count(done.modelCodesCleared)} model kodu silindi` : '',
    done.panelPricesUpdated ? `${count(done.panelPricesUpdated)} panel fiyatı güncellendi` : '',
    done.vatUpdated ? `${count(done.vatUpdated)} ürünün KDV oranı güncellendi` : '',
    done.costsUpdated ? `${count(done.costsUpdated)} maliyet güncellendi` : '',
    done.costsCleared ? `${count(done.costsCleared)} maliyet silindi` : '',
    done.desiUpdated ? `${count(done.desiUpdated)} desi güncellendi` : '',
    done.desiCleared ? `${count(done.desiCleared)} desi silindi` : '',
    done.stocksUpdated ? `${count(done.stocksUpdated)} stok güncellendi` : '',
    done.pricesUpdated ? `${count(done.pricesUpdated)} mağaza fiyatı güncellendi` : ''
  ].filter(Boolean)
  const model = done.modelColours ? ` Model İçindeki ${count(done.modelColours)} Renge Uygulandı.` : ''
  return `${count(done.variants)} varyant kaydedildi (${count(done.products)} ürün): ${parts.join(', ')}.${model}`
}

function FieldCell<T>({ change, oldValue, newValue, format }: { change: ProductTransferFieldChange | undefined; oldValue: T | null | undefined; newValue: T | null | undefined; format: (value: T | null) => string }) {
  if (change === 'SET') return <><span className="pci-old">{format(oldValue ?? null)}</span><span className="pci-arrow" aria-label="yeni değer">→</span><b>{format(newValue ?? null)}</b></>
  if (change === 'CLEAR') return <><span className="pci-old">{format(oldValue ?? null)}</span><span className="pci-arrow" aria-label="yeni değer">→</span><b className="pci-clear">Silinecek</b></>
  return <span className="pci-same">{oldValue == null ? '—' : format(oldValue)}</span>
}

// A changed description by its length ("Açıklama Değişti (N Karakter)"): the HTML itself never goes into the table.
export function descriptionChangeText(row: Pick<ProductTransferRow, 'descriptionChange' | 'newDescriptionLength'>) {
  return row.descriptionChange === 'SET' ? `Açıklama Değişti (${count(row.newDescriptionLength ?? 0)} Karakter)` : null
}

function DescriptionCell({ row }: { row: ProductTransferRow }) {
  const text = descriptionChangeText(row)
  return text ? <b>{text}</b> : <span className="pci-same">Değişmiyor</span>
}

function PanelPriceCell({ row }: { row: ProductTransferRow }) {
  if (row.panelPriceChange !== 'SET') return <span className="pci-same">{row.oldPanelSalePrice == null ? '—' : formatMoney(row.oldPanelSalePrice)}</span>
  return <span className="pci-price">
    {row.oldPanelSalePrice !== row.newPanelSalePrice && <span><span className="pci-old">{row.oldPanelSalePrice == null ? '—' : formatMoney(row.oldPanelSalePrice)}</span><span className="pci-arrow" aria-label="yeni satış fiyatı">→</span><b>{formatMoney(row.newPanelSalePrice ?? null)}</b></span>}
    {row.oldPanelListPrice !== row.newPanelListPrice && <span>Liste: <span className="pci-old">{row.oldPanelListPrice == null ? '—' : formatMoney(row.oldPanelListPrice)}</span><span className="pci-arrow" aria-label="yeni liste fiyatı">→</span><b>{formatMoney(row.newPanelListPrice ?? null)}</b></span>}
  </span>
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
  const stat = (field: ProductTransferField, label: string, value: number): Array<[string, number, string]> => picked.has(field) ? [[label, value, changed(value)]] : []
  const modelCodeChanges = (preview.modelCodeUpdates ?? 0) + (preview.modelCodeClears ?? 0)
  const stats: Array<[string, number, string]> = [
    ['Güncellenecek Varyant', preview.updates, 'ok'],
    ...stat('TITLE', 'Ürün Adı Değişen', preview.titleUpdates ?? 0),
    ...stat('DESCRIPTION', 'Açıklama Değişen', preview.descriptionUpdates ?? 0),
    ...stat('BRAND', 'Marka Değişen', preview.brandUpdates ?? 0),
    ...stat('BARCODE', 'Barkod Değişen', preview.barcodeUpdates ?? 0),
    ...stat('MODEL_CODE', 'Model Kodu Değişen', modelCodeChanges),
    ...stat('PANEL_PRICES', 'Panel Fiyatı Değişen', preview.panelPriceUpdates ?? 0),
    ...stat('VAT', 'KDV Değişen', preview.vatUpdates ?? 0),
    ...stat('COST', 'Maliyet Değişen', preview.costUpdates + preview.costClears),
    ...stat('DESI', 'Desi Değişen', preview.desiUpdates + preview.desiClears),
    ...stat('STOCK', 'Stok Değişen', preview.stockUpdates),
    ...stat('PRICES', 'Fiyat Değişen', preview.priceUpdates),
    ['Değişmiyor', preview.unchanged, 'muted'],
    ['Boş / Atlanan', preview.skipped, 'muted'],
    ['Eşleşmedi', preview.unmatched, preview.unmatched ? 'warn' : 'muted'],
    ['Belirsiz', preview.ambiguous, preview.ambiguous ? 'bad' : 'muted'],
    ['Hatalı', preview.errors, preview.errors ? 'bad' : 'muted']
  ]
  const clearParts = [
    picked.has('COST') && preview.costClears ? `${count(preview.costClears)} varyantın maliyeti` : '',
    picked.has('DESI') && preview.desiClears ? `${count(preview.desiClears)} varyantın desisi` : '',
    picked.has('MODEL_CODE') && preview.modelCodeClears ? `${count(preview.modelCodeClears)} varyantın model kodu` : ''
  ].filter(Boolean)
  return <div className="pci-preview" aria-busy={pending}>
    <p className="pci-file"><RrIcon name="doc" /><span><b>{preview.fileName}</b> · {count(preview.totalRows)} satır okundu{pending ? ' · yeniden önizleniyor…' : ''}</span></p>
    <div className="pci-stats" role="list" aria-label="Önizleme özeti">{stats.map(([label, value, tone]) => <div key={label} role="listitem" className={`pci-stat is-${tone}`}><small>{label}</small><strong>{count(value)}</strong></div>)}</div>
    {picker}
    {clearParts.length > 0 && <p className="pci-note" role="note">{clearParts.join(', ')} silinecek.</p>}
    {problemCount > 0 && <p className="pci-note" role="note">Eşleşmeyen, belirsiz ve hatalı satırlar kaydedilmez. Düzeltmek için dosyayı değiştirip yeniden yükleyin; diğer satırları şimdi kaydedebilirsiniz.</p>}
    {error ? <p className="pci-error" role="alert">{error instanceof Error ? error.message : 'Değişiklikler kaydedilemedi.'}</p> : null}
    {preview.rows.length === 0 ? <p className="pci-empty">Seçtiğiniz alanlarda dolu satır yok; kaydedilecek bir şey bulunmadı.</p> : <div className="pci-tblwrap"><table className="pci-tbl">
      <thead><tr>
        <th scope="col">Satır</th><th scope="col">Ürün</th>
        {picked.has('TITLE') && <th scope="col">Ürün Adı</th>}
        {picked.has('DESCRIPTION') && <th scope="col">Açıklama</th>}
        {picked.has('BRAND') && <th scope="col">Marka</th>}
        {picked.has('BARCODE') && <th scope="col">Barkod</th>}
        {picked.has('MODEL_CODE') && <th scope="col">Model Kodu</th>}
        {picked.has('PANEL_PRICES') && <th scope="col">Panel Fiyatı</th>}
        {picked.has('VAT') && <th scope="col">KDV</th>}
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
          {picked.has('TITLE') && <td className="pci-text">{matched ? <FieldCell change={row.titleChange} oldValue={row.productTitle} newValue={row.newTitle} format={formatText} /> : formatText(row.newTitle ?? null)}</td>}
          {picked.has('DESCRIPTION') && <td className="pci-desc">{matched ? <DescriptionCell row={row} /> : '—'}</td>}
          {picked.has('BRAND') && <td className="pci-text">{matched ? <FieldCell change={row.brandChange} oldValue={row.oldBrand} newValue={row.newBrand} format={formatText} /> : formatText(row.newBrand ?? null)}</td>}
          {picked.has('BARCODE') && <td className="pci-money">{matched ? <FieldCell change={row.barcodeChange} oldValue={row.oldBarcode} newValue={row.newBarcode} format={formatText} /> : formatText(row.newBarcode ?? null)}</td>}
          {picked.has('MODEL_CODE') && <td className="pci-money">{matched ? <FieldCell change={row.modelCodeChange} oldValue={row.oldModelCode} newValue={row.newModelCode} format={formatText} /> : formatText(row.newModelCode ?? null)}</td>}
          {picked.has('PANEL_PRICES') && <td className="pci-prices">{matched ? <PanelPriceCell row={row} /> : formatMoney(row.newPanelSalePrice ?? null)}</td>}
          {picked.has('VAT') && <td className="pci-money">{matched ? <FieldCell change={row.vatChange} oldValue={row.oldVatRate} newValue={row.newVatRate} format={formatVat} /> : formatVat(row.newVatRate ?? null)}</td>}
          {picked.has('COST') && <td className="pci-money">{matched ? <FieldCell change={row.costChange} oldValue={row.oldCost} newValue={row.newCost} format={formatMoney} /> : formatMoney(row.newCost)}</td>}
          {picked.has('DESI') && <td className="pci-money">{matched ? <FieldCell change={row.desiChange} oldValue={row.oldDesi} newValue={row.newDesi} format={formatNumber} /> : formatNumber(row.newDesi)}</td>}
          {picked.has('STOCK') && <td className="pci-money">{matched ? <FieldCell change={row.stockChange} oldValue={row.oldStock} newValue={row.newStock} format={formatNumber} /> : formatNumber(row.newStock)}</td>}
          {picked.has('PRICES') && <td className="pci-prices"><PricesCell prices={row.prices ?? []} /></td>}
          <td><span className={`pci-badge is-${tone}`}>{label}</span>{row.message && <small>{row.message}</small>}</td>
        </tr>
      })}</tbody>
    </table></div>}
    {preview.rowsTruncated && <p className="pci-more">İlk {count(preview.rows.length)} satır gösteriliyor; sorunlu satırlar listenin başında. Kaydet düğmesi tüm {count(preview.updates)} satırın değişikliğini kaydeder.</p>}
  </div>
}
