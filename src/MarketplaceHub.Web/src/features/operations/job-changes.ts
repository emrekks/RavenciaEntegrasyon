import type { ApiAnswer } from '../../shared/api/api-answer'
import { activitySourceLabel, activityVariantText } from './product-activity'
import { priceChange, quantityChange, sendLogResultLabel, sendLogResultTone, sendLogTriggerLabel, type SendLogTone, type ValueChange } from './price-stock-send-log'

// İş detayı › Gidenler ve Değişenler: what a price/stock job sent line by line and what a product import changed, read
// from GET /jobs/{id}/changes/summary and GET /jobs/{id}/changes. Text only; the section reads no state of its own.

export type JobChangesSummary = ApiAnswer<'getJobsByIdChangesSummary'>
export type JobChangesPage = ApiAnswer<'getJobsByIdChanges'>
export type JobChangeLine = JobChangesPage['items'][number]

export const jobChangesPageSize = 50

export { isProductImportJob, jobHasChanges } from './job-changes-applies'

const outcomes: Record<string, string> = {
  NO_CHANGES: 'Bu taramada gönderilecek değişiklik bulunmadı.',
  AUTOMATIC_WRITES_OFF: 'Otomatik yazma kapalı olduğu için gönderilmedi.',
  ORDER_READ_PENDING: 'Beklemede: sipariş okuması bekleniyor. Değişiklikten sonra başlayan sipariş okuması bitince gönderilecek.',
  PUSH_IN_FLIGHT: 'Bağlantının önceki gönderimi sürüyordu; bu tarama gönderim yapmadı, sonraki tarama devam eder.',
  PRICE_POLICY_HELD: 'Gönderilmedi: fiyat yazma akışı kapalı. İstek fiyatı da taşıdığı için stok bekletildi.',
  SUPERSEDED: 'Gönderilmedi: fiyat veya stok bu arada yeniden değişti; yeni değer ayrıca gönderilir.',
  RUNNING: 'İş henüz bitmedi; satırlar iş ilerledikçe burada görünür.'
}

/** Why the list is empty (or what the job is waiting for), as the job recorded it; null when it said nothing. */
export function jobChangesOutcomeText(outcome: string | null | undefined, productImport = false) {
  if (!outcome) return null
  if (outcome === 'NO_CHANGES' && productImport) return 'Bu aktarımda panelde fiyatı veya stoğu değişen varyant bulunmadı.'
  return outcomes[outcome] ?? outcome
}

export type JobChangeCountItem = { key: string; label: string; value: number | null }

const count = (value: number | null | undefined) => (value === null || value === undefined ? null : value)

/**
 * The summary line: Taranan · Gönderilen · Değişmeyen · Bekleyen · Reddedilen · Belirsiz for a send, Fiyatı Değişen ·
 * Stoğu Değişen for an import. A count the job does not record stays null ("—"), never 0.
 */
export function jobChangeCountItems(summary: Pick<JobChangesSummary, 'mode' | 'counts'>): JobChangeCountItem[] {
  const counts = summary.counts
  if (summary.mode === 'PRODUCT_IMPORT') return [
    { key: 'price', label: 'Fiyatı Değişen', value: count(counts.priceChanged) },
    { key: 'stock', label: 'Stoğu Değişen', value: count(counts.stockChanged) }
  ]
  return [
    { key: 'scanned', label: 'Taranan', value: count(counts.scanned) },
    { key: 'sent', label: 'Gönderilen', value: count(counts.sent) },
    { key: 'applied', label: 'Kabul Edilen', value: count(counts.applied) },
    { key: 'unchanged', label: 'Değişmeyen', value: count(counts.unchanged) },
    { key: 'pending', label: 'Bekleyen', value: count(counts.pending) },
    { key: 'rejected', label: 'Reddedilen', value: count(counts.rejected) },
    { key: 'uncertain', label: 'Belirsiz', value: count(counts.uncertain) }
  ]
}

export function formatCount(value: number | null) {
  return value === null ? '—' : value.toLocaleString('tr-TR')
}

const kinds: Record<string, string> = { STOCK: 'Stok', PRICE: 'Fiyat', STOCK_PRICE: 'Stok ve Fiyat' }

export function jobChangeKindLabel(kind: string) {
  return kinds[kind] ?? kind
}

/** Where the line came from: a send's trigger (Sipariş #…, Elle Stok, Excel, Uzlaştırma…) or an import's source. */
export function jobChangeSourceLabel(line: Pick<JobChangeLine, 'source' | 'result'>, orderNumber?: string | null) {
  if (line.result === 'CHANGED') return activitySourceLabel(line.source)
  if (line.source === 'ORDER') return orderNumber ? `Sipariş #${orderNumber}` : 'Sipariş'
  if (line.source === 'RECONCILE') return 'Uzlaştırma'
  if (line.source === 'MANUAL_STOCK') return 'Elle Stok'
  if (line.source === 'STOCK_IMPORT') return 'Aktarım'
  return sendLogTriggerLabel(line.source)
}

export type JobChangeResult = { label: string; tone: SendLogTone; detail: string | null }

// A send's result in the owner's words; a refusal carries the marketplace's reason, an unknown answer stays Belirsiz.
const resultLabels: Record<string, string> = {
  SUBMITTED: 'Gönderildi · Yanıt Bekleniyor',
  APPLIED: 'Gönderildi · Kabul Edildi',
  UNCHANGED: 'Zaten Güncel',
  REJECTED: 'Reddedildi',
  FAILED: 'Gönderilemedi',
  SUPERSEDED: 'Yeni Sürüm Geldi',
  UNKNOWN: 'Belirsiz · Yeniden Denenecek'
}

export function jobChangeResult(line: Pick<JobChangeLine, 'result' | 'errorCode' | 'errorMessage'>): JobChangeResult {
  if (line.result === 'CHANGED') return { label: 'Panelde Güncellendi', tone: 'success', detail: null }
  const label = resultLabels[line.result] ?? sendLogResultLabel(line.result)
  const refused = line.result === 'REJECTED' || line.result === 'FAILED' || line.result === 'UNKNOWN'
  return { label, tone: sendLogResultTone(line.result), detail: refused ? line.errorMessage ?? line.errorCode ?? null : null }
}

export type JobChangeValues = { stock: ValueChange | null; price: ValueChange | null; listPrice: ValueChange | null }

/** The line's old → new values: the stock in pieces, the price in ₺; a part the line did not carry is left out. */
export function jobChangeValues(line: JobChangeLine): JobChangeValues {
  const currency = line.currency || 'TRY'
  const carriesStock = line.kind !== 'PRICE'
  const carriesPrice = line.kind !== 'STOCK'
  const listChanged = line.newListPrice !== null && line.newListPrice !== undefined && line.previousListPrice !== line.newListPrice
  return {
    stock: carriesStock ? quantityChange(line.previousQuantity ?? null, line.newQuantity ?? null) : null,
    price: carriesPrice ? priceChange(line.previousSalePrice ?? null, line.newSalePrice ?? null, currency) : null,
    listPrice: carriesPrice && listChanged ? priceChange(line.previousListPrice ?? null, line.newListPrice ?? null, currency) : null
  }
}

/** The difference badge: "+3", "−2", "+₺10"; null when either side is unknown or nothing changed. */
export function jobChangeDifference(previous: number | null | undefined, next: number | null | undefined, money = false) {
  if (previous === null || previous === undefined || next === null || next === undefined || previous === next) return null
  const delta = next - previous
  const amount = Math.abs(delta).toLocaleString('tr-TR', { maximumFractionDigits: 2 })
  return `${delta > 0 ? '+' : '−'}${money ? '₺' : ''}${amount}`
}

export function jobChangeVariant(line: Pick<JobChangeLine, 'optionText'>) {
  return activityVariantText(line.optionText)
}

/** ?limit=50&search=…&after=… */
export function jobChangesQueryString(search: string, after: string | null = null, limit = jobChangesPageSize) {
  const params = new URLSearchParams()
  params.set('limit', String(Math.min(Math.max(1, limit), 50)))
  const term = search.trim().slice(0, 100)
  if (term) params.set('search', term)
  if (after) params.set('after', after)
  return `?${params.toString()}`
}

const spawnedStatus: Record<string, string> = {
  PENDING: 'Sırada', LEASED: 'Çalışıyor', RETRY_SCHEDULED: 'Yeniden Denenecek', BLOCKED: 'Engellendi', MANUAL_REVIEW: 'İnceleme Bekliyor',
  SUCCEEDED: 'Tamamlandı', DEAD: 'Tamamlanamadı', CANCELLED: 'İptal Edildi'
}

export function spawnedJobStatusLabel(status: string) {
  return spawnedStatus[status] ?? status
}
