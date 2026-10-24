// How İşlem Takibi names a job. The title is the platform and the area ("N11 Sipariş İşlemi"); the line under it says
// which way the data goes and what is done ("Okuma · Yeni Sipariş Çekme"); the tooltip explains it.

export type JobTypeIconName = 'price' | 'order' | 'invoice' | 'return' | 'product' | 'connection' | 'generic'
// Okuma: read from the platform; Yazma: sent to it; Bildirim: the platform pushed it; Panel İçi: no outside call.
export type JobDirection = 'Okuma' | 'Yazma' | 'Bildirim' | 'Panel İçi'
export type JobPresentation = { area: string; icon: JobTypeIconName; direction: JobDirection; action: string; description: string }

const READ: JobDirection = 'Okuma'
const SEND: JobDirection = 'Yazma'
const NOTICE: JobDirection = 'Bildirim'
const PANEL: JobDirection = 'Panel İçi'

const job = (area: string, icon: JobTypeIconName, direction: JobDirection, action: string, description: string): JobPresentation => ({ area, icon, direction, action, description })

const connectionTest = job('Bağlantı', 'connection', READ, 'Bağlantı Testi', 'Kayıtlı kimlik bilgileriyle platforma bağlanılır; hesap ve erişim yetkileri doğrulanır. Platformdaki kayıtlarda değişiklik yapılmaz.')
const referenceSync = job('Katalog', 'product', READ, 'Kategori ve Özellik Listesi Çekme', 'Platformun kategori, özellik ve marka listeleri okunarak eşleştirme ekranlarında kullanılmak üzere güncellenir. Platforma veri gönderilmez.')
const orderSync = job('Sipariş', 'order', READ, 'Yeni Sipariş Çekme', 'Son okumadan bu yana oluşan ve değişen siparişler platformdan alınarak panele işlenir.')
const orderRecovery = job('Sipariş', 'order', READ, 'Sipariş Geçmişi Taraması', 'Geçmiş dönemlerin siparişleri yeniden okunur; kaçırılmış veya eksik kalmış kayıtlar tamamlanır.')
const orderStatus = job('Sipariş', 'order', READ, 'Açık Sipariş Durumu Kontrolü', 'Henüz teslim edilmemiş siparişlerin güncel paket ve kargo durumu platformdan okunarak panelde güncellenir.')
const orderReconciliation = job('Sipariş', 'order', READ, 'Sipariş Mutabakatı', 'Paneldeki siparişler platform kayıtlarıyla karşılaştırılır; durum ve paket farkları giderilir.')
const packageInvoice = job('Fatura', 'invoice', READ, 'Paket Fatura Durumu Kontrolü', 'Paketlerin platformdaki fatura bilgisi okunarak panele işlenir. Yeni fatura oluşturulmaz.')
const webhook = job('Sipariş', 'order', NOTICE, 'Anlık Bildirim İşleme', 'Platformun gönderdiği anlık sipariş bildirimi doğrulanır ve ilgili sipariş panelde güncellenir.')
const productSync = job('Ürün', 'product', READ, 'Ürün Çekme', 'Platformdaki ürünler ve varyantları okunarak panel kataloğuyla eşleştirilir.')
const returnSync = job('İade', 'return', READ, 'Yeni İade Çekme', 'Platformda oluşan ve değişen iade talepleri okunarak panele işlenir.')

const catalogue: Record<string, JobPresentation> = {
  TRENDYOL_CONNECTION_TEST: connectionTest, SHOPIFY_CONNECTION_TEST: connectionTest, HEPSIBURADA_CONNECTION_TEST: connectionTest,
  N11_CONNECTION_TEST: connectionTest, PAZARAMA_CONNECTION_TEST: connectionTest, PTTAVM_CONNECTION_TEST: connectionTest, EFATURAM_CONNECTION_TEST: connectionTest,
  TRENDYOL_CAPABILITY_PROBE: job('Bağlantı', 'connection', READ, 'Yetki Kontrolü', 'Bağlantının hangi işlemler için (sipariş, ürün, fiyat ve stok, iade) yetkili olduğu platformdan sorgulanır.'),
  EFATURAM_STAGE_CAPABILITY_PROBE: job('Bağlantı', 'connection', READ, 'Test Ortamı Yetki Kontrolü', 'Test ortamı hesabının fatura oluşturma ve sorgulama yetkileri doğrulanır. Gerçek fatura kesilmez.'),
  TRENDYOL_STAGE_TEST_ORDER: job('Sipariş', 'order', SEND, 'Test Siparişi Oluşturma', 'Entegrasyonu denemek için pazaryerinin test ortamında örnek sipariş oluşturulur. Canlı mağaza etkilenmez.'),
  TRENDYOL_REFERENCE_SYNC: referenceSync, HEPSIBURADA_REFERENCE_SYNC: referenceSync, N11_REFERENCE_SYNC: referenceSync, PAZARAMA_REFERENCE_SYNC: referenceSync, PTTAVM_REFERENCE_SYNC: referenceSync,
  CATALOG_STRUCTURE_IMPORT: job('Katalog', 'product', READ, 'Ürün Yapısı Çekme', 'Platformdaki ürünlerin kategori, seçenek ve özellik yapısı okunur. Önizleme panele kayıt yazmaz; uygulama yalnızca panel kategorilerini, özelliklerini ve eşleşmelerini oluşturur. Platforma veri gönderilmez.'),
  TRENDYOL_ORDER_SYNC: orderSync, SHOPIFY_ORDER_SYNC: orderSync, HEPSIBURADA_ORDER_SYNC: orderSync, N11_ORDER_SYNC: orderSync, PAZARAMA_ORDER_SYNC: orderSync, PTTAVM_ORDER_SYNC: orderSync,
  TRENDYOL_ORDER_RECOVERY_SYNC: orderRecovery, SHOPIFY_ORDER_RECOVERY_SYNC: orderRecovery, HEPSIBURADA_ORDER_RECOVERY_SYNC: orderRecovery,
  N11_ORDER_RECOVERY_SYNC: orderRecovery, PAZARAMA_ORDER_RECOVERY_SYNC: orderRecovery, PTTAVM_ORDER_RECOVERY_SYNC: orderRecovery,
  ORDER_STATUS_SYNC: orderStatus, SHOPIFY_ORDER_STATUS_SYNC: orderStatus, HEPSIBURADA_ORDER_STATUS_SYNC: orderStatus, PAZARAMA_ORDER_STATUS_SYNC: orderStatus, PTTAVM_ORDER_STATUS_SYNC: orderStatus,
  ORDER_RECONCILIATION: orderReconciliation, SHOPIFY_ORDER_RECONCILIATION: orderReconciliation,
  ORDER_INVOICE_RECONCILIATION: packageInvoice, SHOPIFY_ORDER_INVOICE_RECONCILIATION: packageInvoice, HEPSIBURADA_ORDER_INVOICE_RECONCILIATION: packageInvoice,
  TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION: job('Kargo', 'order', READ, 'Kargo Bilgisi Tamamlama', 'Kargo firması veya takip numarası eksik paketlerin bu bilgileri platformdan okunarak tamamlanır.'),
  SHIPMENT_ACTION: job('Kargo', 'order', SEND, 'Paket İşlemi Gönderme', 'Panelden başlatılan paket işlemi (işleme alma, kargo firması değişikliği, takip numarası, tedarik edememe) platforma iletilir.'),
  TRENDYOL_COMMON_LABEL: job('Kargo', 'order', SEND, 'Kargo Etiketi Oluşturma', 'Paket için ortak kargo etiketi platformda oluşturulur ve yazdırılmak üzere alınır.'),
  TRENDYOL_WEBHOOK_INGEST: webhook, SHOPIFY_WEBHOOK_INGEST: webhook, HEPSIBURADA_WEBHOOK_INGEST: webhook,
  PRODUCT_SYNC: productSync, SHOPIFY_PRODUCT_SYNC: productSync, HEPSIBURADA_PRODUCT_SYNC: productSync,
  PRODUCT_CREATE: job('Ürün', 'product', SEND, 'Ürün Oluşturma', 'Paneldeki ürün platformda yeni ürün olarak açılır.'),
  PRODUCT_UPDATE: job('Ürün', 'product', SEND, 'Ürün Güncelleme', 'Ürün bilgilerinde yapılan değişiklikler platformdaki ürüne iletilir.'),
  PRODUCT_ARCHIVE: job('Ürün', 'product', SEND, 'Ürünü Satışa Kapatma', 'Ürün platformda satışa kapatılır veya arşive alınır.'),
  PRODUCT_APPROVAL_RECONCILE: job('Ürün', 'product', READ, 'Ürün Onay Durumu Kontrolü', 'Platforma gönderilen ürünlerin onay ve yayın durumu okunarak panelde güncellenir.'),
  PRICE_INVENTORY_SYNC: job('Fiyat ve Stok', 'price', SEND, 'Fiyat ve Stok Gönderme', 'Güncel fiyat ve stok bilgileri platforma iletilir. Dış yazma kapalıysa gönderim yapılmaz.'),
  STOCK_PROJECTION_DISPATCH: job('Fiyat ve Stok', 'price', PANEL, 'Stok Gönderim Hazırlığı', 'Stok değişiklikleri platforma gönderilmek üzere sıraya alınır. Bu adım platforma veri göndermez; gönderim ayrı bir Fiyat ve Stok Gönderme işiyle yapılır.'),
  STOCK_RECONCILIATION: job('Fiyat ve Stok', 'price', PANEL, 'Stok Mutabakatı', 'Paneldeki stok, platforma en son gönderilen değerlerle karşılaştırılır; fark varsa yeni gönderim hazırlanır.'),
  RETURN_SYNC: returnSync, HEPSIBURADA_RETURN_SYNC: returnSync,
  RETURN_STATUS_SYNC: job('İade', 'return', READ, 'Açık İade Durumu Kontrolü', 'Sonuçlanmamış iade taleplerinin güncel durumu platformdan okunarak panelde güncellenir.'),
  RETURN_RECONCILIATION: job('İade', 'return', READ, 'İade Mutabakatı', 'Paneldeki iadeler platform kayıtlarıyla karşılaştırılır; eksik veya farklı kayıtlar düzeltilir.'),
  RETURN_ACTION: job('İade', 'return', SEND, 'İade Kararı Gönderme', 'İade talebi için verilen onay veya ret kararı platforma iletilir.'),
  EXTERNAL_EFFECT_RECONCILE: job('Doğrulama', 'connection', READ, 'Gönderim Sonucu Doğrulama', 'Sonucu kesinleşmeyen bir gönderimin platformda uygulanıp uygulanmadığı kontrol edilir; uygulanmadıysa işlem yeniden gönderilir.'),
  MARKETPLACE_QUESTION_SYNC: job('Soru', 'connection', READ, 'Müşteri Sorularını Çekme', 'Ürün ve sipariş soruları platformdan okunarak panele alınır. Platforma cevap gönderilmez.'),
  PAZARAMA_FINANCE_SYNC: job('Finans', 'invoice', READ, 'Sipariş Finans Hareketlerini Çekme', 'Son siparişlerin satış, iade ve komisyon hareketleri okunarak komisyon satış raporuna işlenir. Platforma veri gönderilmez.'),
  TRENDYOL_FINANCE_SYNC: job('Finans', 'invoice', READ, 'Cari Hesap Ekstresi Çekme', 'Platformun cari hesap ekstresi okunarak komisyon, kargo ve diğer kesintiler satış raporuna işlenir. Platforma veri gönderilmez.'),
  INVOICE_SUBMIT: job('Fatura', 'invoice', SEND, 'Fatura Oluşturma', 'Sipariş için hazırlanan e-Fatura veya e-Arşiv faturası E-Faturam’a gönderilerek kesilir.'),
  INVOICE_RECONCILE: job('Fatura', 'invoice', READ, 'Fatura Durumu Sorgulama', 'Gönderilen faturanın E-Faturam’daki onay ve iletim durumu sorgulanır.'),
  INVOICE_DOCUMENT_FETCH: job('Fatura', 'invoice', READ, 'Fatura Belgesi Alma', 'Kesilen faturanın PDF belgesi E-Faturam’dan alınarak saklanır.'),
  INVOICE_MARKETPLACE_DELIVERY: job('Fatura', 'invoice', SEND, 'Faturayı Pazaryerine İletme', 'Kesilen faturanın bağlantısı veya belgesi siparişin pazaryerine iletilir.'),
  MANUAL_ORDER_INVOICE_DELIVERY: job('Fatura', 'invoice', SEND, 'Faturayı Elle İletme', 'Kullanıcının seçtiği siparişin faturası pazaryerine elle iletilir.'),
  INVOICE_CANCELLATION: job('Fatura', 'invoice', SEND, 'Fatura İptali', 'Kesilmiş fatura E-Faturam üzerinden iptal edilir.'),
  INVOICE_DUE_SCAN: job('Fatura', 'invoice', PANEL, 'Fatura Süresi Taraması', 'Fatura kesme süresi yaklaşan veya dolan paketler belirlenir ve uyarı listesi güncellenir.'),
  IMPORT_PREVIEW: job('İçe Aktarım', 'product', PANEL, 'Dosya Önizleme', 'Yüklenen dosya okunur ve doğrulanır; panele kayıt yazılmaz.'),
  IMPORT_APPLY: job('İçe Aktarım', 'product', PANEL, 'Dosyayı Uygulama', 'Onaylanan dosyadaki kayıtlar panele işlenir.')
}

// A type the catalogue does not know yet is named from its words, so it still reads as a sentence.
function unknownJob(jobType: string): JobPresentation {
  const type = jobType.toUpperCase()
  const words = jobType.replaceAll('_', ' ').toLocaleLowerCase('tr-TR').replace(/(^|\s)\S/g, value => value.toLocaleUpperCase('tr-TR'))
  if (/INVOICE|EFATURAM|BILLING/.test(type)) return job('Fatura', 'invoice', PANEL, words, 'Faturayla ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/RETURN|CLAIM/.test(type)) return job('İade', 'return', PANEL, words, 'İadeyle ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/PRICE|INVENTORY|STOCK/.test(type)) return job('Fiyat ve Stok', 'price', PANEL, words, 'Fiyat veya stokla ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/ORDER|SHIPMENT|PACKAGE|LABEL/.test(type)) return job('Sipariş', 'order', PANEL, words, 'Siparişle ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  if (/PRODUCT|CATALOG|CATEGORY|ATTRIBUTE|BRAND/.test(type)) return job('Ürün', 'product', PANEL, words, 'Ürün veya katalogla ilgili arka plan işlemi yürütülür; ayrıntılar için kaydı açabilirsiniz.')
  return job('Sistem', 'generic', PANEL, words, 'İşlem arka planda yürütülür; ayrıntılar için kaydı açabilirsiniz.')
}

export function jobPresentation(jobType: string): JobPresentation {
  return catalogue[jobType.toUpperCase()] ?? unknownJob(jobType)
}

// The platform the job talks to, as the server names it; "Ravencia" is the panel's own work.
const platformFromType = (jobType: string) => {
  const type = jobType.toUpperCase()
  if (type.includes('EFATURAM') || /^INVOICE_(SUBMIT|RECONCILE|DOCUMENT_FETCH|CANCELLATION)$/.test(type)) return 'E-Faturam'
  const prefix = type.split('_')[0]
  return ({ TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify', N11: 'N11', PAZARAMA: 'Pazarama', PTTAVM: 'PTT AVM' } as Record<string, string>)[prefix] ?? null
}

/** "N11 Sipariş İşlemi"; the panel's own jobs carry no platform: "İçe Aktarım İşlemi". */
export function jobTitle(jobType: string, platform?: string | null) {
  const name = platform && platform !== 'Ravencia' ? platform : platformFromType(jobType)
  const { area } = jobPresentation(jobType)
  return name ? `${name} ${area} İşlemi` : `${area} İşlemi`
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
export function jobSubtitle(jobType: string) {
  const { direction, action } = jobPresentation(jobType)
  return `${direction} · ${action}`
}
