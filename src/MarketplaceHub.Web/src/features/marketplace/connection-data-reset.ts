import type { DataResetGroup, DataResetOption } from '../../shared/components/DataResetLedger'

export type ConnectionDataResetScope = 'ORDERS' | 'RETURNS' | 'INVOICES' | 'PRODUCTS' | 'CATEGORIES' | 'CATEGORY_ATTRIBUTES' | 'BRANDS' | 'COMMISSION_TARIFFS' | 'PROMOTION_OFFERS'

export type ConnectionDataResetOption = DataResetOption<ConnectionDataResetScope>
export type ConnectionDataResetGroup = DataResetGroup<ConnectionDataResetScope>

const invoiceOption: ConnectionDataResetOption = {
  scope: 'INVOICES',
  label: 'Fatura Verileri',
  description: 'Bu bağlantıyla sağlayıcı veya sipariş üzerinden ilişkili faturaları ve belgelerini kaldırır.',
  unit: 'fatura'
}

const orderOptions: ConnectionDataResetOption[] = [
  {
    scope: 'ORDERS',
    label: 'Sipariş Verileri',
    description: 'Siparişleri ve gönderi kayıtlarını kaldırır; bağlı iadeler ve faturalar da silinir.',
    unit: 'sipariş',
    // The server removes an order's returns and invoices with it (ResetConnectionDataAsync).
    implies: ['RETURNS', 'INVOICES']
  },
  {
    scope: 'RETURNS',
    label: 'İade Verileri',
    description: 'Bu bağlantının iade taleplerini, kararlarını ve kanıtlarını kaldırır.',
    unit: 'iade'
  },
  invoiceOption
]

const productOption: ConnectionDataResetOption = {
  scope: 'PRODUCTS',
  label: 'Ürün Verileri',
  description: 'Bu bağlantının ürün aktarım ve kanal kayıtlarını kaldırır. Başka bağlantılarda kullanılan ortak ürünler korunur.',
  unit: 'ürün'
}

const categoryOptions: ConnectionDataResetOption[] = [
  {
    scope: 'CATEGORIES',
    label: 'Kategori Verileri',
    description: 'İndirilen kategori ağacını, kategori eşleştirmelerini ve özellik snapshotlarını kaldırır. Paneldeki yerel kategori ağacı korunur.',
    unit: 'kategori'
  },
  {
    scope: 'CATEGORY_ATTRIBUTES',
    label: 'Kategori Özellik Verileri',
    description: 'Bu bağlantının kategori özelliklerini, değerlerini ve özellik eşleştirmelerini kaldırır.',
    unit: 'kayıt'
  }
]

const brandOption: ConnectionDataResetOption = {
  scope: 'BRANDS',
  label: 'Marka Verileri',
  description: 'Bu bağlantının indirilen marka listesini ve marka eşleştirmelerini kaldırır.',
  unit: 'marka'
}

// The files uploaded on the Promotions page (Trendyol only, as the page is).
const pricingOptions: ConnectionDataResetOption[] = [
  {
    scope: 'COMMISSION_TARIFFS',
    label: 'Komisyon Tarifeleri',
    description: 'Yüklenen komisyon tarifesi dosyalarını ve satırlarını kaldırır.',
    unit: 'dosya'
  },
  {
    scope: 'PROMOTION_OFFERS',
    label: 'Promosyon Teklifleri',
    description: 'Plus, Avantajlı Ürün Etiketi ve Flash Ürünler dosyalarını kaldırır.',
    unit: 'dosya'
  }
]

export function connectionDataResetGroups(platformCode: string): ConnectionDataResetGroup[] {
  const normalizedPlatformCode = platformCode.trim().toUpperCase()
  if (normalizedPlatformCode === 'TRENDYOL_EFATURAM') return [{
    label: 'Fatura Verileri',
    description: 'Yalnızca bu e-fatura bağlantısına ait kayıtlar.',
    options: [invoiceOption]
  }]

  const groups: ConnectionDataResetGroup[] = [{
    label: 'Sipariş ve Finans',
    description: 'Sipariş, iade ve fatura kayıtları.',
    options: [...orderOptions]
  }]
  const catalogOptions = [productOption]
  if (normalizedPlatformCode === 'TRENDYOL' || normalizedPlatformCode === 'HEPSIBURADA' || normalizedPlatformCode === 'N11') catalogOptions.push(...categoryOptions)
  if (normalizedPlatformCode === 'TRENDYOL') catalogOptions.push(brandOption)
  groups.push({
    label: 'Ürün Kataloğu',
    description: 'Bu bağlantıdan alınan ürün ve katalog eşleştirmeleri.',
    options: catalogOptions
  })
  if (normalizedPlatformCode === 'TRENDYOL') groups.push({
    label: 'Fiyat ve Promosyon',
    description: 'Promosyonlar sayfasına yüklenen dosyalar.',
    options: [...pricingOptions]
  })
  return groups
}

export function connectionDataResetOptions(platformCode: string): ConnectionDataResetOption[] {
  return connectionDataResetGroups(platformCode).flatMap(group => group.options)
}

// Tam Sıfırlama (30 Oct 2026): everything the connection holds goes, issued invoices included; the server's counts are
// shown under these labels (OperationalDataMaintenanceService.FullResetViewAsync).
export type ConnectionFullResetScope = 'ORDERS' | 'RETURNS' | 'INVOICES' | 'QUESTIONS' | 'PRODUCTS' | 'PRODUCT_LINKS' | 'FINANCE' | 'REFERENCES' | 'MAPPINGS' | 'COMMISSION_TARIFFS' | 'PROMOTION_OFFERS' | 'JOBS'
export type ConnectionFullResetItem = { scope: ConnectionFullResetScope; label: string; description: string; unit: string }
export type ConnectionFullResetJob = { jobId: string; status: string; progressLabel: string | null; progressPercent: number | null; createdAt: string }
export type ConnectionFullResetView = { items: Array<{ scope: string; count: number }>; keptProducts: number; job: ConnectionFullResetJob | null }

export const connectionFullResetItems: ConnectionFullResetItem[] = [
  { scope: 'ORDERS', label: 'Siparişler', description: 'Paketler, gönderi belgeleri ve sipariş geçmişiyle.', unit: 'sipariş' },
  { scope: 'RETURNS', label: 'İadeler', description: 'Kararları, kanıtları ve stok işlemleriyle.', unit: 'iade' },
  { scope: 'INVOICES', label: 'Faturalar', description: 'Kesilmiş faturalar dahil; yüklenen fatura belgeleri ve pazaryerine gönderim kayıtlarıyla.', unit: 'fatura' },
  { scope: 'QUESTIONS', label: 'Müşteri Soruları', description: 'Soru geçmişi ve soru aktarım durumu.', unit: 'soru' },
  { scope: 'PRODUCTS', label: 'Ürünler', description: 'Yalnız bu bağlantıdan içe aktarılan ve başka bağlantıda kullanılmayan ürünler.', unit: 'ürün' },
  { scope: 'PRODUCT_LINKS', label: 'Ürün Bağları', description: 'Bu bağlantıyla ürün, varyant ve ilan bağları; kanal fiyatları ve fiyat geçmişi.', unit: 'ürün' },
  { scope: 'FINANCE', label: 'Finans Kayıtları', description: 'Cari hesap hareketleri ve kargo faturası kalemleri.', unit: 'kayıt' },
  { scope: 'REFERENCES', label: 'Kategori ve Marka Listeleri', description: 'İndirilen kategori, özellik, değer ve marka listeleri.', unit: 'kayıt' },
  { scope: 'MAPPINGS', label: 'Eşleştirmeler', description: 'Kategori, marka, özellik ve değer eşleştirmeleri. Eşleme şablonları korunur.', unit: 'eşleştirme' },
  { scope: 'COMMISSION_TARIFFS', label: 'Komisyon Tarifeleri', description: 'Yüklenen tarife dosyaları ve satırları.', unit: 'dosya' },
  { scope: 'PROMOTION_OFFERS', label: 'Promosyon Teklifleri', description: 'Promosyonlar sayfasına yüklenen dosyalar.', unit: 'dosya' },
  { scope: 'JOBS', label: 'İşler', description: 'Bekleyen işler iptal edilir; iş geçmişi, içe aktarma oturumları ve okuma konumları silinir.', unit: 'iş' }
]

// An e-invoice connection's records are its invoices, which the selective reset and the delete cover.
export function connectionFullResetSupported(platformCode: string) {
  return platformCode.trim().toUpperCase() !== 'TRENDYOL_EFATURAM'
}

export function connectionFullResetRunning(status: string | null | undefined) {
  return status === 'PENDING' || status === 'LEASED' || status === 'RETRY_SCHEDULED'
}
