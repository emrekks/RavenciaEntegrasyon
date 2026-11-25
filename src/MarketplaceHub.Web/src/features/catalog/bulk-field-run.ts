// Ürünler › Toplu İşlemler › Toplu Değer Ata (owner, 12.11.2026): one field set to one value on every colour card and
// variant of the selected models. The checks are the product editor's (the API checks the same, BulkProductFields);
// only the chosen field is written and nothing goes to a marketplace. The selection goes out in chunks of whole models
// (at most 500 records each), so one model is never split over two requests.
import type { ChunkRunResult } from './bulk-status-run'
import { parseTrCount, parseTrMeasure } from '../../shared/tr-number'
import { hasAtMostTwoDecimals } from './quick-price-edit'
import { requiredFieldsSkipNotice, type RequiredFieldsSkipped } from './required-fields-skip'
import { TURKISH_VAT_RATES } from './product-editor-state'
import { isProductDeliveryDays, maxProductDeliveryDays } from './product-editor/product-delivery-days'
import { maxProductDesi } from './product-desi'
import { sanitizeRichText } from '../../shared/security/sanitizeHtml'

export type BulkFieldKey = 'vatRate' | 'vatInclusion' | 'desi' | 'warrantyMonths' | 'deliveryDays' | 'status' | 'brandId' | 'categoryId' | 'safetyStock' | 'description'

// The owner's order; his "KDV" and "KDV Oranı" are the one field KDV Oranı.
export const bulkFieldOptions: ReadonlyArray<{ value: BulkFieldKey; label: string }> = [
  { value: 'vatRate', label: 'KDV Oranı' },
  { value: 'vatInclusion', label: 'KDV Dahil mi' },
  { value: 'desi', label: 'Desi' },
  { value: 'warrantyMonths', label: 'Garanti Süresi (Ay)' },
  { value: 'deliveryDays', label: 'Teslimat Süresi (Gün)' },
  { value: 'status', label: 'Satış Durumu' },
  { value: 'brandId', label: 'Marka' },
  { value: 'categoryId', label: 'Kategori' },
  { value: 'safetyStock', label: 'Güvenlik Stoğu' },
  { value: 'description', label: 'Açıklama' }
]

export const bulkFieldLabel = (field: BulkFieldKey) => bulkFieldOptions.find(option => option.value === field)?.label ?? field

export const bulkVatInclusionOptions: ReadonlyArray<{ value: string; label: string }> = [{ value: 'INCLUDED', label: 'Evet' }, { value: 'EXCLUDED', label: 'Hayır' }]
export const bulkStatusOptions: ReadonlyArray<{ value: string; label: string }> = [{ value: 'ACTIVE', label: 'Satışa Açık' }, { value: 'ARCHIVED', label: 'Satışa Kapalı' }]

export const bulkFieldInitialValues: Record<BulkFieldKey, string> = {
  vatRate: '', vatInclusion: 'INCLUDED', desi: '', warrantyMonths: '', deliveryDays: '', status: 'ACTIVE', brandId: '', categoryId: '', safetyStock: '', description: ''
}

export const bulkMaxWarrantyMonths = 240
export const bulkMaxDesi = maxProductDesi
export const bulkMaxSafetyStock = 100_000
export const bulkMaxDescriptionLength = 60_000

const wholeNumber = (text: string) => /^\d+$/.test(text) ? Number(text) : null
// Read in Turkish (shared/tr-number.ts): "1.250" is 1250 and "0,5" a half; a point used as a decimal point ("1.25") is
// refused, never read as 1.25. The same readers give the value the API takes.
const decimalNumber = (text: string) => parseTrMeasure(text)
const countNumber = (text: string) => { const count = parseTrCount(text); return count.ok ? count.value : null }
const plainText = (html: string) => html.replace(/<[^>]*>/g, '').replace(/&nbsp;/gi, ' ').trim()

// Why the value cannot be applied, or null when it can.
export function bulkFieldIssue(field: BulkFieldKey, value: string): string | null {
  const text = value.trim()
  switch (field) {
    case 'vatRate': return TURKISH_VAT_RATES.includes(text) ? null : 'KDV oranı seçin.'
    case 'vatInclusion': return text === 'INCLUDED' || text === 'EXCLUDED' ? null : 'KDV dahil bilgisini seçin.'
    case 'status': return text === 'ACTIVE' || text === 'ARCHIVED' ? null : 'Satış durumunu seçin.'
    case 'brandId': return text ? null : 'Marka seçin.'
    case 'categoryId': return text ? null : 'Kategori seçin.'
    case 'desi': {
      const desi = decimalNumber(text)
      return desi != null && desi > 0 && desi <= bulkMaxDesi && hasAtMostTwoDecimals(desi) ? null : 'Desi sıfırdan büyük ve en fazla 9.999 olmalıdır (en çok iki ondalık, ör. 0,5).'
    }
    case 'warrantyMonths': {
      const months = wholeNumber(text)
      return months != null && months <= bulkMaxWarrantyMonths ? null : 'Garanti süresi 0-240 ay arasında tam sayı olmalıdır.'
    }
    case 'deliveryDays': return isProductDeliveryDays(text) ? null : `Teslimat süresi 1-${maxProductDeliveryDays} gün arasında tam sayı olmalıdır.`
    case 'safetyStock': {
      const stock = countNumber(text)
      return stock != null && stock <= bulkMaxSafetyStock ? null : 'Güvenlik stoğu 0 veya daha büyük bir tam sayı olmalıdır.'
    }
    case 'description': {
      const html = sanitizeRichText(value)
      if (html.length > bulkMaxDescriptionLength) return `Açıklama en fazla ${bulkMaxDescriptionLength.toLocaleString('tr-TR')} karakter olabilir.`
      return plainText(html) ? null : 'Açıklama yazın.'
    }
  }
}

// The value the API takes: a decimal comma as a point, the description cleaned as the editor cleans it.
export function bulkFieldPayloadValue(field: BulkFieldKey, value: string): string {
  if (field === 'description') return sanitizeRichText(value).trim()
  if (field === 'desi') return String(decimalNumber(value.trim()) ?? Number.NaN)
  if (field === 'safetyStock') return String(countNumber(value.trim()) ?? Number.NaN)
  return value.trim()
}

// The value as the summary line names it.
export function bulkFieldValueLabel(field: BulkFieldKey, value: string, names: { brands?: ReadonlyArray<{ id: string; name: string }>; categories?: ReadonlyArray<{ id: string; path: string }> } = {}): string {
  const text = value.trim()
  switch (field) {
    case 'vatRate': return text ? `%${text}` : '—'
    case 'vatInclusion': return bulkVatInclusionOptions.find(option => option.value === text)?.label ?? '—'
    case 'status': return bulkStatusOptions.find(option => option.value === text)?.label ?? '—'
    case 'brandId': return names.brands?.find(brand => brand.id === text)?.name ?? '—'
    case 'categoryId': return names.categories?.find(category => category.id === text)?.path ?? '—'
    case 'desi': return text ? text.replace('.', ',') : '—'
    case 'warrantyMonths': return text ? (text === '0' ? '0 (garanti yok)' : `${text} ay`) : '—'
    case 'deliveryDays': return text ? `${text} gün` : '—'
    case 'description': {
      const length = plainText(sanitizeRichText(value)).length
      return length ? `${length.toLocaleString('tr-TR')} karakterlik metin` : '—'
    }
    default: return text || '—'
  }
}

export function bulkFieldSummary(cardCount: number, variantCount: number, field: BulkFieldKey, valueLabel: string): string {
  return `${cardCount.toLocaleString('tr-TR')} ürün (${variantCount.toLocaleString('tr-TR')} varyant) için ${bulkFieldLabel(field)} = ${valueLabel} atanacak`
}

export type BulkFieldFailure = { model: string; message: string }
// skipped: closed cards Satış Durumu = Satışa Açık left for a missing required field (required-fields-skip.ts).
// modelColours (KATALOG-14 phase D5b): the colour cards of the models the value reached.
export type BulkFieldResponse = { updatedCount: number; unchangedCount: number; variantCount: number; failures: BulkFieldFailure[]; skipped?: RequiredFieldsSkipped[]; modelColours?: number }

// The selected records in chunks of whole models: a model larger than the limit goes alone.
export function bulkFieldChunks(groups: ReadonlyArray<ReadonlyArray<string>>, size = 500): string[][] {
  const chunks: string[][] = []
  let current: string[] = []
  for (const group of groups) {
    if (current.length && current.length + group.length > size) { chunks.push(current); current = [] }
    current.push(...group)
  }
  if (current.length) chunks.push(current)
  return chunks
}

export async function runBulkFieldChunks(chunks: ReadonlyArray<ReadonlyArray<string>>, send: (chunk: string[]) => Promise<unknown>, onProgress?: (done: number, total: number) => void): Promise<ChunkRunResult> {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0)
  const doneIds: string[] = []
  for (let index = 0; index < chunks.length; index++) {
    try {
      await send([...chunks[index]])
    } catch (error) {
      return { doneIds, remainingIds: chunks.slice(index).flat(), error }
    }
    doneIds.push(...chunks[index])
    onProgress?.(doneIds.length, total)
  }
  return { doneIds, remainingIds: [], error: null }
}

// The skipped cards (Satış Durumu only) are named in the message and carried as the notification's rows and link
// (required-fields-skip.ts).
export function bulkFieldResultMessage(result: ChunkRunResult, counts: BulkFieldResponse, field: BulkFieldKey): { title: string; message: string; kind: 'success' | 'error'; skipped?: ReturnType<typeof requiredFieldsSkipNotice> } {
  const label = bulkFieldLabel(field)
  const reason = result.error instanceof Error ? result.error.message : 'Toplu değer ataması başarısız.'
  const done = `${counts.updatedCount.toLocaleString('tr-TR')} ürün kartı güncellendi (${counts.variantCount.toLocaleString('tr-TR')} varyant)${counts.unchangedCount ? `, ${counts.unchangedCount.toLocaleString('tr-TR')} kart zaten bu değerdeydi` : ''}${counts.modelColours ? `; Model İçindeki ${counts.modelColours.toLocaleString('tr-TR')} Renge Uygulandı` : ''}`
  const failed = counts.failures.length ? ` Yazılamayanlar: ${counts.failures.map(failure => `${failure.model}: ${failure.message}`).join('; ')}` : ''
  const notice = requiredFieldsSkipNotice(counts.skipped ?? [])
  const skipped = notice ? ` ${notice.message}` : ''
  const withSkipped = notice ? { skipped: notice } : {}
  const note = ' Pazar yerlerine bir şey gönderilmedi.'
  if (result.remainingIds.length && !result.doneIds.length) return { title: `${label} Atanamadı`, message: reason, kind: 'error' }
  if (result.remainingIds.length) return { title: `${label} Kısmen Atandı`, message: `${done}; kalan ${result.remainingIds.length.toLocaleString('tr-TR')} kayıt güncellenemedi: ${reason}.${failed}${skipped}`, kind: 'error', ...withSkipped }
  const partial = counts.failures.length > 0 || notice != null
  return { title: partial ? `${label} Kısmen Atandı` : `${label} Atandı`, message: `${done}.${failed}${skipped}${note}`, kind: partial ? 'error' : 'success', ...withSkipped }
}
