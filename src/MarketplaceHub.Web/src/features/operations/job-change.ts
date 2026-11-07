import { notificationLinks } from '../../shared/notifications'
import { catalogStructureImportChange } from './catalog-structure-import'
import { jobPresentation } from './job-presentation'
import { marketplaceQuestionSyncChange } from './marketplace-question-sync'

export type JobChange = { label: string; value: string; detail: string | null }

// The job summary shown when the server sends none, each type by its own text (JobOperationsService.Change): product
// publications, updates and archives write to the marketplace, product reads do not; only the invoice deliveries send an
// invoice to the marketplace, the due scan is the panel's own and E-Faturam reads come from E-Faturam.
const changes: Record<string, JobChange> = {
  MARKETPLACE_QUESTION_SYNC: marketplaceQuestionSyncChange,
  CATALOG_STRUCTURE_IMPORT: catalogStructureImportChange,
  STOCK_PROJECTION_DISPATCH: { label: 'İşlem Türü', value: 'Stok gönderim hazırlığı', detail: 'Bu adım dış API’ye yazmaz. Dış yazma açıksa ayrı bir fiyat-stok gönderim işi oluşturur.' },
  STOCK_RECONCILIATION: { label: 'İşlem Türü', value: 'Stok uzlaştırma taraması', detail: 'Bu kayıt tek başına pazaryerine stok gönderildiği anlamına gelmez.' },
  PRICE_INVENTORY_SYNC: { label: 'İşlem Türü', value: 'Fiyat ve stok gönderimi', detail: 'Dış yazma kapalıysa pazaryerine yeni gönderim yapılmaz.' },
  SHIPMENT_ACTION: { label: 'Yapılan Değişiklik', value: 'Paket işlemi', detail: 'Paket işlemi pazaryerine gönderildi.' },
  ORDER_STATUS_SYNC: { label: 'Tarama Türü', value: 'Sipariş durum taraması', detail: 'Açık siparişlerin paket ve taşıma durumları kontrol edilerek yerel durum güncellenir.' },
  TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION: { label: 'Tarama Türü', value: 'Trendyol kargo bilgisi taraması', detail: 'Teslim edilmiş paketlerin kargo bilgileri ayrı, salt okunur bir taramada yenilenir.' },
  ORDER_RECONCILIATION: { label: 'Tarama Türü', value: 'Kapsamlı sipariş taraması', detail: 'Yerel siparişler ile pazaryeri kayıtları karşılaştırılır; durum ve paket farklılıkları düzeltilir.' },
  ORDER_INVOICE_RECONCILIATION: { label: 'Tarama Türü', value: 'Paket fatura taraması', detail: 'Teslim edilmiş ve açık paketlerin pazaryeri fatura durumu kontrol edilir.' },
  TRENDYOL_ORDER_RECOVERY_SYNC: { label: 'Tarama Türü', value: 'Tam sipariş taraması', detail: 'Erişilebilen sipariş pencereleri taranarak eksik yerel kayıtlar tamamlanır.' },
  TRENDYOL_ORDER_SYNC: { label: 'Yapılan Değişiklik', value: 'Sipariş senkronizasyonu', detail: 'Sipariş bilgileri pazaryerinden eşitlendi.' },
  PRODUCT_CREATE: { label: 'Yapılan Değişiklik', value: 'Ürün yayını', detail: 'Ürün pazaryerine gönderildi.' },
  PRODUCT_UPDATE: { label: 'Yapılan Değişiklik', value: 'Ürün güncellemesi', detail: 'Ürün değişiklikleri pazaryerine gönderildi.' },
  PRODUCT_ARCHIVE: { label: 'Yapılan Değişiklik', value: 'Ürün arşivleme', detail: 'Arşivleme isteği pazaryerine gönderildi.' },
  PRODUCT_APPROVAL_RECONCILE: { label: 'Yapılan İşlem', value: 'Ürün onay kontrolü', detail: 'Gönderilen ürünün pazaryerindeki onay durumu okundu; pazaryerine veri gönderilmedi.' },
  PRODUCT_SYNC: { label: 'Yapılan İşlem', value: 'Ürün senkronizasyonu', detail: 'Ürün bilgileri pazaryerinden okundu; pazaryerine veri gönderilmedi.' },
  SHOPIFY_PRODUCT_SYNC: { label: 'Yapılan İşlem', value: 'Ürün senkronizasyonu', detail: 'Ürün bilgileri pazaryerinden okundu; pazaryerine veri gönderilmedi.' },
  HEPSIBURADA_PRODUCT_SYNC: { label: 'Yapılan İşlem', value: 'Ürün senkronizasyonu', detail: 'Ürün bilgileri pazaryerinden okundu; pazaryerine veri gönderilmedi.' },
  INVOICE_MARKETPLACE_DELIVERY: { label: 'Yapılan Değişiklik', value: 'Fatura iletimi', detail: 'Fatura isteği pazaryerine gönderildi.' },
  MANUAL_ORDER_INVOICE_DELIVERY: { label: 'Yapılan Değişiklik', value: 'Fatura iletimi', detail: 'Fatura isteği pazaryerine gönderildi.' },
  INVOICE_SUBMIT: { label: 'Yapılan Değişiklik', value: 'Fatura kesimi', detail: 'Fatura E-Faturam’a gönderildi.' },
  INVOICE_CANCELLATION: { label: 'Yapılan Değişiklik', value: 'Fatura iptali', detail: 'İptal isteği E-Faturam’a gönderildi.' },
  INVOICE_RECONCILE: { label: 'Yapılan İşlem', value: 'Fatura durumu', detail: 'Fatura durumu E-Faturam’dan okundu.' },
  INVOICE_DOCUMENT_FETCH: { label: 'Yapılan İşlem', value: 'Fatura belgesi', detail: 'Fatura belgesi E-Faturam’dan okundu.' },
  INVOICE_DUE_SCAN: { label: 'Tarama Türü', value: 'Fatura vade taraması', detail: 'Panel içi tarama: kesilmesi gereken faturalar panelde kontrol edildi; dış servise istek gönderilmedi.' },
  RETURN_SYNC: { label: 'Yapılan İşlem', value: 'Yeni iade çekme', detail: 'Pazaryerinden son değişen iade talepleri okundu ve panele işlendi; pazaryerine veri gönderilmedi.' },
  HEPSIBURADA_RETURN_SYNC: { label: 'Yapılan İşlem', value: 'Yeni iade çekme', detail: 'Hepsiburada’dan son değişen iade talepleri okundu ve panele işlendi; pazaryerine veri gönderilmedi.' },
  RETURN_STATUS_SYNC: { label: 'Tarama Türü', value: 'Açık iade durum taraması', detail: 'Paneldeki açık iade taleplerinin güncel durumu pazaryerinden okundu.' },
  RETURN_RECONCILIATION: { label: 'Tarama Türü', value: 'Kapsamlı iade taraması', detail: 'Geçmiş iade pencereleri taranarak paneldeki iade kayıtları pazaryeriyle karşılaştırıldı.' },
  TRENDYOL_FINANCE_SYNC: { label: 'Yapılan İşlem', value: 'Hakediş ve cari hareket okuma', detail: 'Trendyol’un cari hesap ve hakediş hareketleri okundu; pazaryerine veri gönderilmedi.' },
  PAZARAMA_FINANCE_SYNC: { label: 'Yapılan İşlem', value: 'Hakediş okuma', detail: 'Pazarama’nın ödeme ve hakediş kayıtları okundu; pazaryerine veri gönderilmedi.' },
  HEPSIBURADA_FINANCE_SYNC: { label: 'Yapılan İşlem', value: 'Hakediş okuma', detail: 'Hepsiburada’nın muhasebe ve hakediş kayıtları okundu; pazaryerine veri gönderilmedi.' },
  N11_SETTLEMENT_SYNC: { label: 'Yapılan İşlem', value: 'Hakediş okuma', detail: 'N11’in hakediş kayıtları okundu; pazaryerine veri gönderilmedi.' },
  LISTING_VERIFY: { label: 'Tarama Türü', value: 'Ürün bağlantı doğrulaması', detail: 'Paneldeki pazaryeri ürün bağlantılarının pazaryerinde hâlâ var olup olmadığı okundu; pazaryerine veri gönderilmedi.' },
  LISTING_LOOKUP: { label: 'Tarama Türü', value: 'Barkod arama', detail: 'Seçilen barkodlar pazaryerinde tek tek arandı; pazaryerine veri gönderilmedi.' },
  TRENDYOL_WEBHOOK_INGEST: { label: 'Yapılan İşlem', value: 'Anlık bildirim işleme', detail: 'Pazaryerinin gönderdiği anlık bildirim (webhook) panele işlendi.' },
  SHOPIFY_WEBHOOK_INGEST: { label: 'Yapılan İşlem', value: 'Anlık bildirim işleme', detail: 'Shopify’ın gönderdiği anlık bildirim (webhook) panele işlendi.' },
  HEPSIBURADA_WEBHOOK_INGEST: { label: 'Yapılan İşlem', value: 'Anlık bildirim işleme', detail: 'Hepsiburada’nın gönderdiği anlık bildirim (webhook) panele işlendi.' },
  EXTERNAL_EFFECT_RECONCILE: { label: 'Tarama Türü', value: 'Gönderim sonucu doğrulaması', detail: 'Sonucu kesinleşmemiş bir pazaryeri işleminin uygulanıp uygulanmadığı pazaryerinden okundu.' },
  RETURN_ACTION: { label: 'Yapılan Değişiklik', value: 'İade kararı', detail: 'İade kararı pazaryerine gönderildi.' },
  TRENDYOL_COMMON_LABEL: { label: 'Yapılan Değişiklik', value: 'Ortak etiket', detail: 'Ortak kargo etiketi pazaryerinden istendi.' },
  CONNECTION_FULL_RESET: { label: 'Yapılan İşlem', value: 'Bağlantı verisi sıfırlama', detail: 'Bağlantının panelde tutulan verileri silindi; pazaryerine veri gönderilmedi.' }
}

const referenceSources: Record<string, string> = {
  TRENDYOL_REFERENCE_SYNC: 'Trendyol kategori ve eşleştirme verisi',
  HEPSIBURADA_REFERENCE_SYNC: 'Hepsiburada kategori ve eşleştirme verisi',
  N11_REFERENCE_SYNC: 'N11 kategori ve eşleştirme verisi',
  PTTAVM_REFERENCE_SYNC: 'PTT AVM kategori verisi',
  PAZARAMA_REFERENCE_SYNC: 'Pazarama kategori, özellik ve marka verisi'
}

// Links from a job's detail to the records it is about: its product, its invoice and its order (or the orders of a batch).
export function jobRelatedLinks(detail: {
  productId?: string | null
  invoice?: { invoiceId: string; orderNumber: string } | null
  order?: { orderNumber: string } | null
  relatedOrders?: ReadonlyArray<{ orderNumber: string }> | null
}): Array<{ href: string; label: string }> {
  const links: Array<{ href: string; label: string }> = []
  if (detail.productId) links.push({ href: notificationLinks.product(detail.productId), label: 'Ürüne Git' })
  if (detail.invoice?.invoiceId) links.push({ href: notificationLinks.invoice(detail.invoice.invoiceId), label: 'Faturaya Git' })
  const orderNumbers = [detail.order?.orderNumber, detail.invoice?.orderNumber, ...(detail.relatedOrders ?? []).map(order => order.orderNumber)]
    .filter((value): value is string => Boolean(value))
  for (const orderNumber of [...new Set(orderNumbers)].slice(0, 5))
    links.push({ href: notificationLinks.order(orderNumber), label: `Sipariş #${orderNumber}` })
  return links
}

export function fallbackJobChange(jobType: string): JobChange {
  const type = jobType.toUpperCase()
  const known = changes[type]
  if (known) return known
  const reference = referenceSources[type]
  if (reference) return { label: 'Yapılan İşlem', value: reference, detail: 'Kategori, özellik ve eşleştirme referansları salt okunur yenilendi; dış platforma veri gönderilmedi.' }
  if (/CONNECTION_TEST$/.test(type)) return { label: 'Yapılan İşlem', value: 'Bağlantı testi', detail: 'Mağaza bilgileriyle pazaryerine bir okuma isteği yapılıp bağlantının çalıştığı kontrol edildi.' }
  // A type without its own text says what it is and that its detail is not kept, instead of an empty phrase.
  return { label: 'Yapılan İşlem', value: jobPresentation(jobType).action, detail: 'Bu iş türü için ayrıntılı kayıt tutulmuyor; sonucu aşağıdaki sayılarda ve deneme geçmişinde.' }
}
