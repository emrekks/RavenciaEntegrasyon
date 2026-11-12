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
  { scope: 'MAPPINGS', label: 'Eşleştirmeler', description: 'Kategori, marka, özellik ve değer eşleştirmeleri; eşleme şablonları ve varsayılan özellik değerleri dahil.', unit: 'eşleştirme' },
  { scope: 'COMMISSION_TARIFFS', label: 'Komisyon Tarifeleri', description: 'Yüklenen tarife dosyaları ve satırları.', unit: 'dosya' },
  { scope: 'PROMOTION_OFFERS', label: 'Promosyon Teklifleri', description: 'Promosyonlar sayfasına yüklenen dosyalar.', unit: 'dosya' },
  { scope: 'JOBS', label: 'İş Takibi Kayıtları', description: 'Bu bağlantının İş Takibi’ndeki iş geçmişi, içe aktarma oturumları ve okuma konumları silinir; bekleyen işler iptal edilir, çalışan iş kalır.', unit: 'iş' }
]

// An e-invoice connection's records are its invoices, which the selective reset and the delete cover.
export function connectionFullResetSupported(platformCode: string) {
  return platformCode.trim().toUpperCase() !== 'TRENDYOL_EFATURAM'
}

export function connectionFullResetRunning(status: string | null | undefined) {
  return status === 'PENDING' || status === 'LEASED' || status === 'RETRY_SCHEDULED'
}
