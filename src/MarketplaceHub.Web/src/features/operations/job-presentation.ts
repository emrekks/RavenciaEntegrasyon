import { knownJobTypes } from './job-categories'
import { platformDisplayName, platformDisplayNames, platformDisplayOrder } from '../../shared/platform-names'
// How İşlem Takibi names a job. The title is the platform and what the job does ("N11 Yeni Siparişler"); the line under it says
// which way the data goes and what is done ("Okuma · Yeni Sipariş Çekme"); the tooltip explains it.

export type JobTypeIconName = 'price' | 'order' | 'invoice' | 'return' | 'product' | 'connection' | 'generic'
// Okuma: read from the platform; Yazma: sent to it; Bildirim: the platform pushed it; Panel İçi: no outside call.
export type JobDirection = 'Okuma' | 'Yazma' | 'Bildirim' | 'Panel İçi'
export type JobPresentation = { title: string; icon: JobTypeIconName; direction: JobDirection; action: string; description: string }

const READ: JobDirection = 'Okuma'
const SEND: JobDirection = 'Yazma'
const NOTICE: JobDirection = 'Bildirim'
const PANEL: JobDirection = 'Panel İçi'

const job = (title: string, icon: JobTypeIconName, direction: JobDirection, action: string, description: string): JobPresentation => ({ title, icon, direction, action, description })

const connectionTest = job('Bağlantı Testi', 'connection', READ, 'Bağlantı Testi', 'Kayıtlı kimlik bilgileriyle platforma bağlanılır; hesap ve erişim yetkileri doğrulanır. Platformdaki kayıtlarda değişiklik yapılmaz.')
const referenceSync = job('Kategori Listesi', 'product', READ, 'Kategori ve Özellik Listesi Çekme', 'Platformun kategori, özellik ve marka listeleri okunarak eşleştirme ekranlarında kullanılmak üzere güncellenir. Platforma veri gönderilmez.')
const orderSync = job('Yeni Siparişler', 'order', READ, 'Yeni Sipariş Çekme', 'Son okumadan bu yana oluşan ve değişen siparişler platformdan alınarak panele işlenir.')
const orderRecovery = job('Sipariş Geçmişi', 'order', READ, 'Sipariş Geçmişi Taraması', 'Geçmiş dönemlerin siparişleri yeniden okunur; kaçırılmış veya eksik kalmış kayıtlar tamamlanır.')
const orderStatus = job('Açık Sipariş Durumları', 'order', READ, 'Açık Sipariş Durumu Kontrolü', 'Henüz teslim edilmemiş siparişlerin güncel paket ve kargo durumu platformdan okunarak panelde güncellenir.')
const orderReconciliation = job('Sipariş Uzlaştırması', 'order', READ, 'Sipariş Uzlaştırması', 'Paneldeki siparişler platform kayıtlarıyla karşılaştırılır; durum ve paket farkları giderilir.')
const packageInvoice = job('Paket Fatura Durumları', 'invoice', READ, 'Paket Fatura Durumu Kontrolü', 'Paketlerin platformdaki fatura bilgisi okunarak panele işlenir. Yeni fatura oluşturulmaz.')
const webhook = job('Anlık Sipariş Bildirimi', 'order', NOTICE, 'Anlık Bildirim İşleme', 'Platformun gönderdiği anlık sipariş bildirimi doğrulanır ve ilgili sipariş panelde güncellenir.')
const productSync = job('Ürünler', 'product', READ, 'Ürün Çekme', 'Platformdaki ürünler ve varyantları okunarak panel kataloğuyla eşleştirilir.')
const returnSync = job('Yeni İadeler', 'return', READ, 'Yeni İade Çekme', 'Platformda oluşan ve değişen iade talepleri okunarak panele işlenir.')

const catalogue: Record<string, JobPresentation> = {
  TRENDYOL_CONNECTION_TEST: connectionTest, SHOPIFY_CONNECTION_TEST: connectionTest, HEPSIBURADA_CONNECTION_TEST: connectionTest,
  N11_CONNECTION_TEST: connectionTest, PAZARAMA_CONNECTION_TEST: connectionTest, PTTAVM_CONNECTION_TEST: connectionTest, EFATURAM_CONNECTION_TEST: connectionTest,
  TRENDYOL_CAPABILITY_PROBE: job('Yetki Kontrolü', 'connection', READ, 'Yetki Kontrolü', 'Bağlantının hangi işlemler için (sipariş, ürün, fiyat ve stok, iade) yetkili olduğu platformdan sorgulanır.'),
  EFATURAM_STAGE_CAPABILITY_PROBE: job('Test Ortamı Yetki Kontrolü', 'connection', READ, 'Test Ortamı Yetki Kontrolü', 'Test ortamı hesabının fatura oluşturma ve sorgulama yetkileri doğrulanır. Gerçek fatura kesilmez.'),
  TRENDYOL_STAGE_TEST_ORDER: job('Test Siparişi', 'order', SEND, 'Test Siparişi Oluşturma', 'Entegrasyonu denemek için pazaryerinin test ortamında örnek sipariş oluşturulur. Canlı mağaza etkilenmez.'),
  TRENDYOL_REFERENCE_SYNC: referenceSync, HEPSIBURADA_REFERENCE_SYNC: referenceSync, N11_REFERENCE_SYNC: referenceSync, PAZARAMA_REFERENCE_SYNC: referenceSync, PTTAVM_REFERENCE_SYNC: referenceSync,
  CATALOG_STRUCTURE_IMPORT: job('Ürün Yapısı', 'product', READ, 'Ürün Yapısı Çekme', 'Platformdaki ürünlerin kategori, seçenek ve özellik yapısı okunur. Önizleme kayıt yazmaz; uygulama panel kategorilerini, özelliklerini ve eşleşmelerini oluşturur. Platforma veri gönderilmez.'),
  TRENDYOL_ORDER_SYNC: orderSync, SHOPIFY_ORDER_SYNC: orderSync, HEPSIBURADA_ORDER_SYNC: orderSync, N11_ORDER_SYNC: orderSync, PAZARAMA_ORDER_SYNC: orderSync, PTTAVM_ORDER_SYNC: orderSync,
  TRENDYOL_ORDER_RECOVERY_SYNC: orderRecovery, SHOPIFY_ORDER_RECOVERY_SYNC: orderRecovery, HEPSIBURADA_ORDER_RECOVERY_SYNC: orderRecovery,
  N11_ORDER_RECOVERY_SYNC: orderRecovery, PAZARAMA_ORDER_RECOVERY_SYNC: orderRecovery, PTTAVM_ORDER_RECOVERY_SYNC: orderRecovery,
  ORDER_STATUS_SYNC: orderStatus, SHOPIFY_ORDER_STATUS_SYNC: orderStatus, HEPSIBURADA_ORDER_STATUS_SYNC: orderStatus, PAZARAMA_ORDER_STATUS_SYNC: orderStatus, PTTAVM_ORDER_STATUS_SYNC: orderStatus,
  ORDER_RECONCILIATION: orderReconciliation, SHOPIFY_ORDER_RECONCILIATION: orderReconciliation,
  ORDER_INVOICE_RECONCILIATION: packageInvoice, SHOPIFY_ORDER_INVOICE_RECONCILIATION: packageInvoice, HEPSIBURADA_ORDER_INVOICE_RECONCILIATION: packageInvoice,
  TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION: job('Kargo Bilgileri', 'order', READ, 'Kargo Bilgisi Tamamlama', 'Kargo firması veya takip numarası eksik paketlerin bu bilgileri platformdan okunarak tamamlanır.'),
  SHIPMENT_ACTION: job('Paket Durumu Gönderimi', 'order', SEND, 'Paket İşlemi Gönderme', 'Panelden başlattığınız paket işlemi (işleme alma, kargo firması, takip numarası, tedarik edememe) platforma gönderilir.'),
  TRENDYOL_COMMON_LABEL: job('Kargo Etiketi', 'order', SEND, 'Kargo Etiketi Oluşturma', 'Paket için ortak kargo etiketi platformda oluşturulur ve yazdırılmak üzere alınır.'),
  TRENDYOL_WEBHOOK_INGEST: webhook, SHOPIFY_WEBHOOK_INGEST: webhook, HEPSIBURADA_WEBHOOK_INGEST: webhook,
  PRODUCT_SYNC: productSync, SHOPIFY_PRODUCT_SYNC: productSync, HEPSIBURADA_PRODUCT_SYNC: productSync,
  PRODUCT_CREATE: job('Ürün Oluşturma', 'product', SEND, 'Ürün Oluşturma', 'Paneldeki ürün platformda yeni ürün olarak açılır.'),
  PRODUCT_UPDATE: job('Ürün Güncelleme', 'product', SEND, 'Ürün Güncelleme', 'Ürün bilgilerinde yapılan değişiklikler platformdaki ürüne iletilir.'),
  PRODUCT_ARCHIVE: job('Ürünü Satıştan Kaldırma', 'product', SEND, 'Ürünü Satışa Kapatma', 'Ürün platformda satışa kapatılır veya arşive alınır.'),
  PRODUCT_APPROVAL_RECONCILE: job('Ürün Onay Durumu', 'product', READ, 'Ürün Onay Durumu Kontrolü', 'Platforma gönderilen ürünlerin onay ve yayın durumu okunarak panelde güncellenir.'),
  LISTING_VERIFY: job('Yayın Doğrulaması', 'product', READ, 'Günlük Yayın Doğrulaması', 'Mağazadaki bütün ürünler okunur; paneldeki varyantların pazaryerinde hâlâ durup durmadığı kontrol edilir. Ürün bilgisi, fiyat ve stok değiştirilmez.'),
  LISTING_LOOKUP: job('Varyant Arama', 'product', READ, 'Varyant Pazaryerinde Aranıyor', 'Görülmeyen ya da stok gönderiminde tanınmayan varyantlar barkodla aranır; pazaryeri yok derse Bulunamadı olarak işaretlenir.'),
  PRICE_INVENTORY_SYNC: job('Fiyat ve Stok Gönderimi', 'price', SEND, 'Fiyat ve Stok Gönderme', 'Güncel fiyat ve stok bilgileri platforma iletilir. Dış yazma kapalıysa gönderim yapılmaz.'),
  STOCK_PROJECTION_DISPATCH: job('Stok Gönderim Hazırlığı', 'price', PANEL, 'Stok Gönderim Hazırlığı', 'Stok değişiklikleri gönderilmek üzere sıraya alınır. Bu adım platforma veri göndermez; gönderimi ayrı bir Fiyat ve Stok Gönderme işi yapar.'),
  STOCK_RECONCILIATION: job('Stok Uzlaştırması', 'price', PANEL, 'Stok Uzlaştırması', 'Paneldeki stok, platforma en son gönderilen değerlerle karşılaştırılır; fark varsa yeni gönderim hazırlanır.'),
  RETURN_SYNC: returnSync, HEPSIBURADA_RETURN_SYNC: returnSync,
  RETURN_STATUS_SYNC: job('Açık İade Durumları', 'return', READ, 'Açık İade Durumu Kontrolü', 'Sonuçlanmamış iade taleplerinin güncel durumu platformdan okunarak panelde güncellenir.'),
  RETURN_RECONCILIATION: job('İade Uzlaştırması', 'return', READ, 'İade Uzlaştırması', 'Paneldeki iadeler platform kayıtlarıyla karşılaştırılır; eksik veya farklı kayıtlar düzeltilir.'),
  RETURN_ACTION: job('İade Kararı', 'return', SEND, 'İade Kararı Gönderme', 'İade talebi için verilen onay veya ret kararı platforma iletilir.'),
  EXTERNAL_EFFECT_RECONCILE: job('Gönderim Doğrulaması', 'connection', READ, 'Gönderim Sonucu Doğrulama', 'Sonucu belli olmayan bir gönderimin platformda uygulanıp uygulanmadığı kontrol edilir; uygulanmadıysa yeniden gönderilir.'),
  MARKETPLACE_QUESTION_SYNC: job('Müşteri Soruları', 'connection', READ, 'Müşteri Sorularını Çekme', 'Ürün ve sipariş soruları platformdan okunarak panele alınır. Platforma cevap gönderilmez.'),
  PAZARAMA_FINANCE_SYNC: job('Finans Hareketleri', 'invoice', READ, 'Sipariş Finans Hareketlerini Çekme', 'Son siparişlerin satış, iade ve komisyon hareketleri okunarak komisyon satış raporuna işlenir. Platforma veri gönderilmez.'),
  TRENDYOL_FINANCE_SYNC: job('Cari Hesap Ekstresi', 'invoice', READ, 'Cari Hesap Ekstresi Çekme', 'Platformun hesap ekstresi okunur; komisyon, kargo ve kesintiler satış raporuna, hakediş ve ödemeler Hakediş ekranına işlenir. Platforma veri gönderilmez.'),
  N11_SETTLEMENT_SYNC: job('Hakediş Kayıtları', 'invoice', READ, 'Hakediş Kayıtlarını Çekme', 'N11 hakediş günleri ve sipariş kalemi dökümleri okunarak Hakediş ekranına işlenir. Platforma veri gönderilmez.'),
  HEPSIBURADA_FINANCE_SYNC: job('Hakediş Kayıtları', 'invoice', READ, 'Muhasebe Kayıtlarını Çekme', 'Ödenecek ve ödenmiş muhasebe kayıtları okunarak Hakediş ekranına işlenir. Platforma veri gönderilmez.'),
  INVOICE_SUBMIT: job('Fatura Kesimi', 'invoice', SEND, 'Fatura Oluşturma', 'Sipariş için hazırlanan e-Fatura veya e-Arşiv faturası E-Faturam’a gönderilerek kesilir.'),
  INVOICE_RECONCILE: job('Fatura Durumu', 'invoice', READ, 'Fatura Durumu Sorgulama', 'Gönderilen faturanın E-Faturam’daki onay ve iletim durumu sorgulanır.'),
  INVOICE_DOCUMENT_FETCH: job('Fatura Belgesi', 'invoice', READ, 'Fatura Belgesi Alma', 'Kesilen faturanın PDF belgesi E-Faturam’dan alınarak saklanır.'),
  INVOICE_MARKETPLACE_DELIVERY: job('Fatura Gönderimi', 'invoice', SEND, 'Faturayı Pazaryerine İletme', 'Kesilen faturanın bağlantısı veya belgesi siparişin pazaryerine iletilir.'),
  MANUAL_ORDER_INVOICE_DELIVERY: job('Fatura Gönderimi (Elle)', 'invoice', SEND, 'Faturayı Elle İletme', 'Kullanıcının seçtiği siparişin faturası pazaryerine elle iletilir.'),
  INVOICE_CANCELLATION: job('Fatura İptali', 'invoice', SEND, 'Fatura İptali', 'Kesilmiş fatura E-Faturam üzerinden iptal edilir.'),
  INVOICE_DUE_SCAN: job('Fatura Süre Takibi', 'invoice', PANEL, 'Fatura Süresi Taraması', 'Fatura kesme süresi yaklaşan veya dolan paketler belirlenir ve uyarı listesi güncellenir.'),
  IMPORT_PREVIEW: job('Dosya Önizleme', 'product', PANEL, 'Dosya Önizleme', 'Yüklenen dosya okunur ve doğrulanır; panele kayıt yazılmaz.'),
  IMPORT_APPLY: job('Dosya İçe Aktarma', 'product', PANEL, 'Dosyayı Uygulama', 'Onaylanan dosyadaki kayıtlar panele işlenir.'),
  PANEL_FACTORY_RESET: job('Panel Verisi Sıfırlama', 'generic', PANEL, 'Panel Verisi Sıfırlama', 'Ayarlar › Veri’de seçilen veri türleri panelden silinir; bütün türler seçildiyse panel tamamen sıfırlanır ve bu oturum dışındaki oturumlar kapanır. Pazaryerine ve E-Faturam’a hiçbir şey gönderilmez.'),
  CONNECTION_FULL_RESET: job('Bağlantı Tam Sıfırlama', 'connection', PANEL, 'Tam Sıfırlama', 'Bağlantının sipariş, iade, fatura, ürün bağı, finans ve liste kayıtları panelden silinir; bağlantı ayarları korunur. Pazaryerine ve E-Faturam’a hiçbir şey gönderilmez.')
}

// A type the catalogue does not know yet is named from its words, so it still reads as a sentence.
function unknownJob(jobType: string): JobPresentation {
  const type = jobType.toUpperCase()
  const words = jobType.replaceAll('_', ' ').toLocaleLowerCase('en-US').replace(/(^|\s)\S/g, value => value.toLocaleUpperCase('en-US'))
  if (/INVOICE|EFATURAM|BILLING/.test(type)) return job('Fatura', 'invoice', PANEL, words, 'Faturayla ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/RETURN|CLAIM/.test(type)) return job('İade', 'return', PANEL, words, 'İadeyle ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/PRICE|INVENTORY|STOCK/.test(type)) return job('Fiyat ve Stok', 'price', PANEL, words, 'Fiyat veya stokla ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/ORDER|SHIPMENT|PACKAGE|LABEL/.test(type)) return job('Sipariş', 'order', PANEL, words, 'Siparişle ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/PRODUCT|CATALOG|CATEGORY|ATTRIBUTE|BRAND/.test(type)) return job('Ürün', 'product', PANEL, words, 'Ürün veya katalogla ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  return job('Sistem', 'generic', PANEL, words, 'İşlem arka planda yürütülür; ayrıntılar için kaydı açabilirsiniz.')
}

export const jobDirections: readonly JobDirection[] = [READ, SEND, NOTICE, PANEL]

/** The known job types that go the given way; the list asks the server for these. */
export function jobTypesForDirection(direction: JobDirection): string[] {
  return [...new Set([...knownJobTypes, ...Object.keys(catalogue)])].filter(type => jobPresentation(type).direction === direction)
}

// One reference sync job reads one list (the payload's resourceType): categories, brands or category attributes.
const referenceResources: Record<string, Pick<JobPresentation, 'title' | 'action' | 'description'>> = {
  CATEGORIES: { title: 'Kategori Listesi', action: 'Kategori ve Özellik Listesi Çekme', description: 'Platformun kategori listesi ve kategorilerin özellikleri okunarak eşleştirme ekranlarında kullanılmak üzere güncellenir. Platforma veri gönderilmez.' },
  BRANDS: { title: 'Marka Listesi', action: 'Marka Listesi Çekme', description: 'Platformun marka listesi okunarak eşleştirme ekranlarında kullanılmak üzere güncellenir. Platforma veri gönderilmez.' },
  CATEGORY_ATTRIBUTES: { title: 'Kategori Özellikleri', action: 'Kategori Özellikleri Çekme', description: 'Eşleştirilen kategorilerin özellikleri ve seçenek değerleri platformdan okunarak güncellenir. Platforma veri gönderilmez.' }
}
referenceResources.ATTRIBUTES = referenceResources.CATEGORY_ATTRIBUTES
referenceResources.ATTRIBUTE_VALUES = referenceResources.CATEGORY_ATTRIBUTES

/** The job's presentation; resource is the job's payload resourceType, which tells apart the lists a reference sync reads. */
export function jobPresentation(jobType: string, resource?: string | null): JobPresentation {
  const base = catalogue[jobType.toUpperCase()] ?? unknownJob(jobType)
  const reference = jobType.toUpperCase().endsWith('_REFERENCE_SYNC') && resource ? referenceResources[resource.trim().toUpperCase()] : undefined
  return reference ? { ...base, ...reference } : base
}

// The platform the job talks to, as the server names it; "Ravencia" is the panel's own work.
const platformFromType = (jobType: string) => {
  const type = jobType.toUpperCase()
  if (type.includes('EFATURAM') || /^INVOICE_(SUBMIT|RECONCILE|DOCUMENT_FETCH|CANCELLATION)$/.test(type)) return platformDisplayNames.TRENDYOL_EFATURAM
  const prefix = type.split('_')[0]
  return (platformDisplayOrder as readonly string[]).includes(prefix) ? platformDisplayName(prefix) : null
}

/** "N11 Yeni Siparişler", "Trendyol Marka Listesi"; the panel's own jobs carry no platform: "Dosya İçe Aktarma". */
export function jobTitle(jobType: string, platform?: string | null, resource?: string | null) {
  const name = platform && platform !== 'Ravencia' ? platform : platformFromType(jobType)
  const { title } = jobPresentation(jobType, resource)
  return name ? `${name} ${title}` : title
}

// Job types that kept a TRENDYOL_ prefix from when only Trendyol had them, though other marketplaces run them too
// (the Hepsiburada test order is a TRENDYOL_STAGE_TEST_ORDER job). For another marketplace the code is shown without
// the prefix, so a Hepsiburada job never reads as Trendyol's.
const sharedTrendyolPrefixedTypes = new Set(['TRENDYOL_STAGE_TEST_ORDER'])

/** The job type as shown for the job's marketplace: "STAGE_TEST_ORDER" for a Hepsiburada test order. */
export function jobTypeCode(jobType: string, platform?: string | null) {
  return sharedTrendyolPrefixedTypes.has(jobType) && platform && platform !== 'Trendyol' ? jobType.slice('TRENDYOL_'.length) : jobType
}

/** "Okuma · Yeni Sipariş Çekme" */
export function jobSubtitle(jobType: string, resource?: string | null) {
  const { direction, action } = jobPresentation(jobType, resource)
  return `${direction} · ${action}`
}
