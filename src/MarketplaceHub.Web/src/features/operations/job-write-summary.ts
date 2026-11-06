// What a product write job sent to the marketplace (JobWritePayloadReader on the server, read from the job's stored
// payload): the product, one part per marketplace request with the fields and attributes its items carried, and the
// marketplace request ids the job kept.
export type JobWriteAttribute = { id: string; name: string | null }
export type JobWritePart = { key: string; itemCount: number; fields: string[]; barcodes: string[]; contentCount: number; attributes: JobWriteAttribute[] }
export type JobWriteRequest = { key: string; id: string }
export type JobWriteSummary = { productTitle: string | null; modelCodes: string[]; mode: string | null; phase: string | null; parts: JobWritePart[]; requests: JobWriteRequest[] }

// Jobs about one product card: their detail shows the product and its variants, not the read counters of a store import.
const productLevelJobs = new Set(['PRODUCT_CREATE', 'PRODUCT_UPDATE', 'PRODUCT_ARCHIVE', 'PRODUCT_APPROVAL_RECONCILE'])
export const isProductLevelJob = (jobType: string) => productLevelJobs.has(jobType.trim().toUpperCase())

const partLabels: Record<string, string> = {
  CREATE: 'Ürün Oluşturma',
  UNAPPROVED: 'Ürün Bilgileri',
  CONTENT: 'İçerik',
  VARIANTS: 'Varyant Fiyat ve Stok',
  DELIVERY: 'Teslimat'
}
export const writePartLabel = (key: string) => partLabels[key] ?? key

// Request ids are stored per request: the product request's, or the phase the job was waiting on.
const requestLabels: Record<string, string> = {
  PRODUCT: 'Ürün İsteği No',
  POLL_UNAPPROVED: 'Ürün İsteği No',
  POLL_CONTENT: 'İçerik İsteği No',
  POLL_VARIANTS: 'Varyant İsteği No',
  POLL_DELIVERY: 'Teslimat İsteği No'
}
export const writeRequestLabel = (key: string) => requestLabels[key] ?? 'Pazaryeri İstek No'

// Identifiers name the item; they are not changes.
const identifierFields = new Set(['barcode', 'contentId', 'merchantSku', 'hbSku'])
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
  currencyType: 'Para Birimi'
}

// The fields a part's items carried, in Turkish, identifiers left out and repeats merged ("Teslim Süresi" once).
export function writeFieldLabels(fields: readonly string[]) {
  const labels: string[] = []
  for (const field of fields) {
    if (identifierFields.has(field)) continue
    const label = fieldLabels[field] ?? field
    if (!labels.includes(label)) labels.push(label)
  }
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
  return phase
}
