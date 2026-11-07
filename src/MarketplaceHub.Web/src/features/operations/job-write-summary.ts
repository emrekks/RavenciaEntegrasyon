// What a product write job sent to the marketplace (JobWritePayloadReader on the server, read from the job's stored
// payload): the product, one part per marketplace request with the fields and attributes its items carried, and the
// marketplace request ids the job kept.
export type JobWriteAttribute = { id: string; name: string | null }
// clearedFields: fields an item sent as null; the marketplace deletes them on the product (Trendyol), so they are shown.
// sharedCards, sharedItems: the part went in one request with other cards of the same send (a grouped Trendyol update).
export type JobWritePart = { key: string; itemCount: number; fields: string[]; barcodes: string[]; contentCount: number; attributes: JobWriteAttribute[]; clearedFields?: string[] | null; sharedCards?: number | null; sharedItems?: number | null }
export type JobWriteRequest = { key: string; id: string }
// Other write jobs (price/stock, package actions, labels, return decisions, invoices) also carry a note about what the job
// sends, why it was sent (a price/stock trigger) and each item's stored answer.
export type JobSentItem = { reference: string; detail: string | null; status: string; reason: string | null; at?: string | null }
export type JobWriteSummary = { productTitle: string | null; modelCodes: string[]; mode: string | null; phase: string | null; parts: JobWritePart[]; requests: JobWriteRequest[]; note?: string | null; trigger?: string | null; items?: JobSentItem[] | null }

// Jobs about one product card: their detail shows the product and its variants, not the read counters of a store import.
const productLevelJobs = new Set(['PRODUCT_CREATE', 'PRODUCT_UPDATE', 'PRODUCT_ARCHIVE', 'PRODUCT_APPROVAL_RECONCILE'])
export const isProductLevelJob = (jobType: string) => productLevelJobs.has(jobType.trim().toUpperCase())

const partLabels: Record<string, string> = {
  CREATE: 'Ürün Oluşturma',
  UNAPPROVED: 'Ürün Bilgileri',
  CONTENT: 'İçerik',
  VARIANTS: 'Varyant Fiyat ve Stok',
  DELIVERY: 'Teslimat',
  ARCHIVE: 'Arşivleme',
  PRICE_STOCK: 'Fiyat ve Stok',
  PRICE: 'Fiyat',
  STOCK: 'Stok',
  SALE_OPEN: 'Satışa Açma',
  SALE_CLOSE: 'Satışa Kapatma',
  RETURN_DECISION: 'İade Kararı',
  COMMON_LABEL: 'Ortak Etiket',
  STAGE_TEST_ORDER: 'Test Siparişi',
  INVOICE_SUBMIT: 'Fatura Kesimi (E-Faturam)',
  INVOICE_CANCEL: 'Fatura İptali (E-Faturam)',
  INVOICE_DELIVERY: 'Fatura Bağlantısı'
}

// Package actions: SHIPMENT_<action>.
const shipmentActionLabels: Record<string, string> = {
  PICKING: 'İşleme Alma',
  INVOICED: 'Faturalandı Bildirimi',
  TRACKING_NUMBER: 'Takip Numarası',
  CANCEL_ITEMS: 'Tedarik Edilemedi (Kalem İptali)',
  NOT_SUPPLIED: 'Tedarik Edilemedi',
  SPLIT: 'Paket Bölme',
  MULTI_SPLIT: 'Çoklu Paket Bölme',
  SPLIT_BY_QUANTITY: 'Adede Göre Paket Bölme',
  CHANGE_CARGO_PROVIDER: 'Kargo Firması Değişikliği',
  ALTERNATIVE_DELIVERY: 'Alternatif Teslimat',
  MANUAL_DELIVER: 'Manuel Teslim Bildirimi',
  MANUAL_RETURN: 'Manuel İade Bildirimi',
  UNPACK: 'Paketi Bozma',
  UPDATE_PACKAGE: 'Paket Güncelleme',
  LABOR_COST: 'İşçilik Bedeli',
  COLLECTION_REQUEST: 'Toplama İsteği',
  CANCEL_APPROVE: 'İptal Talebi Onayı',
  CANCEL_REJECT: 'İptal Talebi Reddi'
}
export function writePartLabel(key: string) {
  if (key.startsWith('SHIPMENT_')) {
    const action = key.slice('SHIPMENT_'.length)
    return `Paket İşlemi: ${shipmentActionLabels[action] ?? action}`
  }
  return partLabels[key] ?? key
}

// Request ids are stored per request: the product request's, or the phase the job was waiting on.
const requestLabels: Record<string, string> = {
  PRODUCT: 'Ürün İsteği No',
  POLL_UNAPPROVED: 'Ürün İsteği No',
  POLL_CONTENT: 'İçerik İsteği No',
  POLL_VARIANTS: 'Varyant İsteği No',
  POLL_DELIVERY: 'Teslimat İsteği No',
  PRICE_STOCK: 'Fiyat/Stok İsteği No',
  RETURN_DECISION: 'İade İsteği No',
  COMMON_LABEL: 'Etiket İsteği No',
  INVOICE_SUBMIT: 'E-Faturam İstek No',
  INVOICE_DELIVERY: 'Pazaryeri Fatura Referansı'
}
export const writeRequestLabel = (key: string) => requestLabels[key] ?? 'Pazaryeri İstek No'

// Identifiers name the item; they are not changes.
const identifierFields = new Set(['barcode', 'contentId', 'merchantSku', 'hbSku', 'hepsiburadaSku', 'code', 'variantId', 'lineId', 'orderLineId'])
const fieldLabels: Record<string, string> = {
  title: 'Başlık',
  productName: 'Başlık',
  description: 'Açıklama',
  images: 'Görseller',
  attributes: 'Özellikler',
  productMainId: 'Model Kodu',
  brandId: 'Marka',
  brand: 'Marka',
  categoryId: 'Kategori',
  quantity: 'Stok Adedi',
  stock: 'Stok Adedi',
  stockCode: 'Stok Kodu',
  dimensionalWeight: 'Desi',
  origin: 'Menşei',
  listPrice: 'Liste Fiyatı',
  salePrice: 'Satış Fiyatı',
  price: 'Fiyat',
  vatRate: 'KDV Oranı',
  deliveryOption: 'Teslim Süresi',
  deliveryDuration: 'Teslim Süresi',
  dispatchTime: 'Teslim Süresi',
  cargoCompanyId: 'Kargo Firması',
  shipmentAddressId: 'Sevkiyat Adresi',
  returningAddressId: 'İade Adresi',
  currencyType: 'Para Birimi',
  priceWithVat: 'KDV Dahil Fiyat',
  variants: 'Varyant Stokları',
  availableStock: 'Stok Adedi',
  stockCount: 'Stok Adedi',
  lines: 'Kalemler',
  items: 'Kalemler',
  status: 'Durum',
  params: 'Ek Bilgiler',
  reasonId: 'İptal Nedeni',
  cargoSenderNumber: 'Kargo Gönderi No',
  providerCode: 'Kargo Firması',
  returnTrackingNumber: 'İade Takip No',
  orderLineIds: 'Sipariş Kalemleri',
  shouldKeepPreviousStatus: 'Önceki Durumu Koru',
  splitGroups: 'Bölme Grupları',
  cargoProvider: 'Kargo Firması',
  CargoCompanyShortName: 'Kargo Firması',
  isPhoneNumber: 'Takip Türü',
  trackingInfo: 'Takip Bilgisi',
  boxQuantity: 'Koli Adedi',
  deci: 'Desi',
  packages: 'Paketler',
  laborCost: 'İşçilik Bedeli',
  volumetricHeight: 'Hacimsel Yükseklik',
  format: 'Etiket Biçimi',
  decision: 'Karar',
  returnLines: 'İade Kalemleri',
  invoiceLines: 'Fatura Satırları',
  recipient: 'Alıcı Bilgileri',
  totals: 'Tutarlar',
  ettn: 'ETTN',
  invoiceLink: 'Fatura Bağlantısı',
  invoiceNumber: 'Fatura No',
  invoiceDateTime: 'Fatura Tarihi',
  package: 'Paket No',
  archived: 'Satış Durumu'
}

// The fields a part's items carried, in Turkish, identifiers left out and repeats merged ("Teslim Süresi" once). A field
// sent as null is listed as "(silinir)": the marketplace deletes its value, so the drawer never hides it.
export function writeFieldLabels(fields: readonly string[], clearedFields: readonly string[] = []) {
  const labels: string[] = []
  const add = (field: string, suffix: string) => {
    if (identifierFields.has(field)) return
    const label = `${fieldLabels[field] ?? field}${suffix}`
    if (!labels.includes(label)) labels.push(label)
  }
  for (const field of fields) add(field, '')
  for (const field of clearedFields) add(field, ' (silinir)')
  return labels
}

export const writeAttributeNames = (attributes: readonly JobWriteAttribute[]) =>
  attributes.map(attribute => attribute.name ?? `Özellik #${attribute.id}`)

// "6 içerik kartı", "18 barkod", "Gönderilecek kalem yoktu; istek yapılmadı".
export function writePartCount(part: JobWritePart) {
  if (part.itemCount === 0) return 'Gönderilecek kalem yoktu; istek yapılmadı'
  if (part.contentCount > 0 && part.barcodes.length === 0) return `${part.contentCount} içerik kartı`
  if (part.barcodes.length > 0) return `${part.barcodes.length} barkod`
  return `${part.itemCount} kalem`
}

export function writeModeLabel(mode: string | null) {
  if (!mode) return null
  const value = mode.toUpperCase()
  if (value === 'APPROVED') return 'Onaylı ürün: içerik, varyant ve teslimat ayrı isteklerle'
  if (value === 'UNAPPROVED') return 'Onaysız ürün: tüm bilgiler tek istekte'
  return mode
}

// The step a running job is on ("SUBMIT_CONTENT" → "İçerik gönderiliyor", "POLL_CONTENT" → "İçerik sonucu bekleniyor").
export function writePhaseLabel(phase: string | null) {
  if (!phase) return null
  const value = phase.toUpperCase()
  const part = value.replace(/^(SUBMIT|POLL)_/, '')
  const label = partLabels[part] ?? part
  if (value.startsWith('SUBMIT_')) return `${label} gönderilecek`
  if (value.startsWith('POLL_')) return `${label} sonucu bekleniyor`
  // A card of a grouped Trendyol send that the card polling the shared request answered.
  if (value === 'GROUP_DONE') return 'Ortak istek sonucu işleniyor'
  if (value === 'GROUP_STOPPED') return 'Ortak istek sonucu kaydediliyor'
  return phase
}

export type SentItemTone = 'success' | 'warning' | 'danger' | 'neutral' | 'info'

// The stored answer codes of the other write jobs: the price/stock send log, package-action and label records, return
// decisions, invoice submission attempts and delivery states. An unknown code is shown as it came, never guessed.
const sentItemStatuses: Record<string, { label: string; tone: SentItemTone }> = {
  SUBMITTED: { label: 'Gönderildi', tone: 'info' },
  APPLIED: { label: 'Uygulandı', tone: 'success' },
  UNCHANGED: { label: 'Zaten Güncel', tone: 'success' },
  REJECTED: { label: 'Reddedildi', tone: 'danger' },
  FAILED: { label: 'Başarısız', tone: 'danger' },
  SUPERSEDED: { label: 'Yeni Sürüm Geldi', tone: 'warning' },
  UNKNOWN: { label: 'Sonuç Belirsiz', tone: 'warning' },
  ACCEPTED: { label: 'Pazaryeri Kabul Etti', tone: 'success' },
  VERIFIED: { label: 'Doğrulandı', tone: 'success' },
  CONFIRMED: { label: 'Doğrulandı', tone: 'success' },
  UNRESOLVED: { label: 'Doğrulanamadı', tone: 'warning' },
  OPEN: { label: 'Sonuç Bekleniyor', tone: 'neutral' },
  PENDING: { label: 'Bekliyor', tone: 'neutral' },
  STARTED: { label: 'Başladı', tone: 'neutral' },
  POLLING: { label: 'Sonuç Bekleniyor', tone: 'neutral' },
  SUCCEEDED: { label: 'Tamamlandı', tone: 'success' },
  MANUAL_REVIEW: { label: 'İnceleme Bekliyor', tone: 'warning' },
  RETRYABLE_FAILURE: { label: 'Yeniden Denenecek', tone: 'warning' },
  REVOKED: { label: 'Geri Alındı', tone: 'neutral' },
  CANCELLED: { label: 'İptal Edildi', tone: 'neutral' },
  CANCELLATIONPENDING: { label: 'İptal Bekleniyor', tone: 'warning' }
}

export function sentItemStatus(status: string): { label: string; tone: SentItemTone } {
  return sentItemStatuses[status.toUpperCase()] ?? { label: status, tone: 'info' }
}

export const isSentProblem = (item: JobSentItem) => ['danger', 'warning'].includes(sentItemStatus(item.status).tone)

// "12 kalem gönderildi · 2 sorunlu".
export function sentItemSummary(items: readonly JobSentItem[]) {
  const problems = items.filter(isSentProblem).length
  return `${items.length} kayıt${problems > 0 ? ` · ${problems} sorunlu` : ''}`
}

// "Aynı gönderimdeki 6 kart tek istekte gitti (toplam 18 kalem); …": the part went in one marketplace request with the other cards of the same send.
export function writePartShared(part: JobWritePart) {
  if (!part.sharedCards || part.sharedCards < 2) return null
  return `Aynı gönderimdeki ${part.sharedCards} kart tek istekte gitti${part.sharedItems ? ` (toplam ${part.sharedItems} kalem)` : ''}; sonuç bir kez okunup her karta ayrı yazılır.`
}
