namespace MarketplaceHub.Application;

// What the owner reads when a job stops. The code stays as it is (logs, filters, retries); a code whose stored summary
// is developer wording (listing state, payload, batch, canary, English) is shown with the plain sentence below: what is
// wrong and what to do. Applied when a job is read (job lists and detail, attempts, the price-stock monitor, the
// dashboard), so jobs that stopped before this map existed read the same. Owner, 7 Nov 2026: RYP003T showed
// "Ürün güncelleme listing state kayıtları eksik."; every failure must say its cause in the user's language.
// JobErrorMessagesTests fails when a job result in the source carries such wording under a code that has no entry here.
public static class JobErrorMessages
{
    private const string MarketplaceRecordMissing = "Bu ürünün pazaryeri kaydı panelde eksik";

    public static IReadOnlyDictionary<string, string> Messages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Product update
        ["PRODUCT_UPDATE_STATE_INCOMPLETE"] = $"{MarketplaceRecordMissing}; güncelleme pazaryerine gönderilmedi. Ürün senkronizasyonu çalışınca düzelecek, sonra Platformlarda Güncelle'ye yeniden basın.",
        ["PRODUCT_UPDATE_STATE_MISSING"] = "Bu ürünün bu pazaryerindeki yayın bilgisi panelde bulunamadı; güncelleme gönderilmedi. Ürünün pazaryeri kartında durumu yenileyip yeniden deneyin.",
        ["PRODUCT_UPDATE_PAYLOAD_INVALID"] = "Güncelleme isteği eksik ya da hatalı hazırlandı; pazaryerine bir şey gönderilmedi. Ürünü kaydedip güncellemeyi yeniden başlatın.",
        ["PRODUCT_UPDATE_SUPERSEDED"] = "Bu ürün için daha yeni bir güncelleme başlatıldığı için bu eski güncelleme gönderilmedi; geçerli olan yenisidir.",
        ["PRODUCT_UPDATE_BATCH_CONTRACT_INVALID"] = "Pazaryerinin güncelleme sonucu gönderilen varyantlarla eşleşmedi; güncellemenin yapılıp yapılmadığı bilinmiyor. Ürünün pazaryeri kartında durumu yenileyip kontrol edin.",
        ["PRODUCT_UPDATE_BATCH_EXPIRED"] = "Pazaryeri güncellemenin sonucunu dört saat içinde bildirmedi; sonuç bilinmiyor. Ürünü pazaryeri panelinde kontrol edin ya da güncellemeyi yeniden başlatın.",
        ["PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN"] = "Pazaryeri güncelleme için panelin tanımadığı bir durum bildirdi; sonuç bilinmiyor. Ürünü pazaryeri panelinde kontrol edin.",
        ["PRODUCT_UPDATE_NEXT_PHASE"] = "Güncellemenin ilk adımı tamamlandı; ikinci adımı hazırlanıyor.",
        ["HEPSIBURADA_DISPATCH_TIME_UNCONFIRMED"] = "Hepsiburada teslimat süresi yüklemesinde hata bildirdi; bazı ürünlerde yeni sürenin geçip geçmediği bilinmiyor. Hepsiburada panelinde kontrol edin.",

        // Publication and its result
        ["PRODUCT_PUBLICATION_PAYLOAD_INVALID"] = "Yayın isteği eksik ya da hatalı hazırlandı; pazaryerine gönderilmedi. Ürünü kaydedip yeniden yayınlayın.",
        ["PRODUCT_PUBLICATION_STATE_MISSING"] = "Bu ürünün yayın bilgisi panelde bulunamadı; gönderim yapılmadı. Ürünü yeniden yayınlayın.",
        ["PRODUCT_BATCH_CONTRACT_INVALID"] = "Pazaryerinin gönderim sonucu gönderilen barkodlarla bire bir eşleşmedi; varyantların gerçek durumu bilinmiyor. İşlem Takibi'nde bu işin Manuel Yeniden Dene düğmesi sonucu pazaryerinden yeniden sorgular, ürünü yeniden göndermez.",
        ["PRODUCT_BATCH_RESULT_EXPIRED"] = "Pazaryeri gönderimin sonucunu dört saat içinde bildirmedi; ürünün yayına girip girmediği bilinmiyor. Ürünün pazaryeri kartında durumu yenileyin.",
        ["PRODUCT_BATCH_STATUS_UNKNOWN"] = "Pazaryeri gönderim için panelin tanımadığı bir durum bildirdi; sonuç bilinmiyor. Ürünün pazaryeri kartında durumu yenileyin.",
        ["PRODUCT_APPROVAL_IDENTIFIERS_MISSING"] = "Pazaryeri ürünü onayladı ama ürün numaralarını bildirmedi; eşleşme tamamlanamadı. Ürünün pazaryeri kartında durumu yenileyin.",
        ["PRODUCT_APPROVAL_PAYLOAD_INVALID"] = "Onay kontrolü eksik bilgiyle başlatıldı; pazaryerine sorulmadı. Ürünün pazaryeri kartında durumu yenileyin.",
        ["PRODUCT_APPROVAL_STATE_INCOMPLETE"] = $"{MarketplaceRecordMissing}; onay kontrolü yapılamadı. Ürün senkronizasyonu çalışınca düzelecek.",
        ["PRODUCT_APPROVAL_STATE_MISSING"] = "Onay kontrolü için ürünün yayın bilgisi panelde bulunamadı. Ürünü yeniden yayınlayın ya da kartında durumu yenileyin.",
        ["PRODUCT_APPROVAL_SUPERSEDED"] = "Ürün bu kontrol başladıktan sonra yeniden gönderildi; eski onay kontrolü bir şey değiştirmedi.",
        ["PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE"] = "Pazaryeri onay yanıtında iki farklı varyant için aynı ürün numarasını verdi; yanlış eşleşme olmasın diye işlem durduruldu. Pazaryeri panelinde varyantları kontrol edin.",
        ["LISTING_LOOKUP_PAYLOAD_INVALID"] = "Ürün sorgusu eksik bilgiyle başlatıldı; pazaryerine sorulmadı. Kartta durumu yeniden yenileyin.",
        ["HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID"] = "Hepsiburada'nın ürün sonucu gönderilen ürünlerle bire bir eşleşmedi; durum bilinmiyor. Hepsiburada panelinde kontrol edin.",
        ["HEPSIBURADA_TRACKING_ID_REQUIRED"] = "Hepsiburada gönderiminin takip numarası kayıp olduğu için sonucu sorgulanamadı. Ürünün pazaryeri kartında durumu yenileyin.",

        // Archive
        ["PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID"] = "Trendyol arşiv sonucunu eksik bildirdi; ürünün arşivlenip arşivlenmediği bilinmiyor. Trendyol panelinde kontrol edin.",
        ["PRODUCT_ARCHIVE_BATCH_PENDING"] = "Trendyol arşiv sonucunu henüz bildirmedi; birazdan yeniden kontrol edilecek.",
        ["PRODUCT_ARCHIVE_BATCH_STATUS_UNKNOWN"] = "Trendyol arşiv için panelin tanımadığı bir durum bildirdi; sonuç bilinmiyor. Trendyol panelinde kontrol edin.",
        ["PRODUCT_ARCHIVE_PAYLOAD_INVALID"] = "Arşiv isteği eksik bilgiyle hazırlandı; Trendyol'a gönderilmedi. Arşivlemeyi yeniden başlatın.",
        ["PRODUCT_ARCHIVE_PHASE_INVALID"] = "Arşiv sonucunu sorgulamak için gereken gönderim numarası kayıp; sonuç bilinmiyor. Trendyol panelinde kontrol edin.",
        ["PRODUCT_ARCHIVE_STATE_INCOMPLETE"] = $"{MarketplaceRecordMissing}; arşivleme yapılmadı. Ürün senkronizasyonu çalışınca yeniden deneyin.",
        ["PRODUCT_ARCHIVE_STATE_MISSING"] = "Arşivlenecek ürünün yayın bilgisi panelde bulunamadı; arşivleme yapılmadı.",
        ["PRODUCT_ARCHIVE_SUPERSEDED"] = "Bu ürün için daha yeni bir işlem başlatıldığı için eski arşiv isteği gönderilmedi.",

        // Price and stock
        ["PRICE_INVENTORY_BATCH_CONTRACT_INVALID"] = "Hepsiburada fiyat-stok sonucunda gönderilmeyen bir ürün bildirdi; sonucun hangi ürüne ait olduğu bilinmiyor. Hepsiburada panelinde fiyat ve stoku kontrol edin.",
        ["PRICE_INVENTORY_BATCH_EXPIRED"] = "Pazaryeri fiyat-stok sonucunu beklenen süre içinde bildirmedi; değerlerin geçip geçmediği bilinmiyor. Pazaryeri panelinde kontrol edin.",
        ["PRICE_INVENTORY_BATCH_PENDING"] = "Pazaryeri fiyat-stok sonucunu henüz bildirmedi; birazdan yeniden kontrol edilecek.",
        ["PRICE_INVENTORY_BATCH_STATUS_UNKNOWN"] = "Pazaryeri fiyat-stok için panelin tanımadığı bir durum bildirdi; sonuç bilinmiyor. Pazaryeri panelinde kontrol edin.",
        ["PRICE_INVENTORY_PARTIAL_FAILURE"] = "Pazaryeri fiyat-stok güncellemesindeki bazı ürünleri reddetti; reddedilenler ve sebepleri işin ayrıntısında. Sebebi düzeltip yeniden gönderin.",
        ["PRICE_INVENTORY_PAYLOAD_INVALID"] = "Fiyat-stok isteği eksik ya da hatalı hazırlandı; pazaryerine gönderilmedi. Fiyat ve stoku kaydedip yeniden gönderin.",
        ["PRICE_INVENTORY_REJECTED"] = "Pazaryeri fiyat-stok güncellemesindeki tüm ürünleri reddetti; sebepler işin ayrıntısında. Sebebi düzeltip yeniden gönderin.",
        ["PRICE_INVENTORY_SUPERSEDED"] = "Fiyat veya stok bu arada yeniden değişti; eski değerler gönderilmedi, yenileri gönderilecek.",
        ["PRICE_INVENTORY_STATE_INCOMPLETE"] = "Fiyat-stok kontrolü için panelin stok hesabı eksik; pazaryerine stok gönderilmedi. Stok yeniden hesaplanınca otomatik denenecek.",
        ["STOCK_PROJECTION_PAYLOAD_INVALID"] = "Stok hesaplama isteği hatalı hazırlandı; pazaryerine stok gönderilmedi.",

        // Shipping labels and the test-store checks
        ["COMMON_LABEL_PAYLOAD_INVALID"] = "Kargo etiketi isteği eksik bilgiyle hazırlandı; etiket istenmedi. Etiketi yeniden isteyin.",
        ["CAPABILITY_PROBE_PAYLOAD_INVALID"] = "Test işlemi eksik bilgiyle başlatıldı; çalıştırılmadı.",
        ["CAPABILITY_PROBE_TARGET_INVALID"] = "Test işlemi için kargo takip numaralı test paketi bulunamadı.",
        ["COMMON_LABEL_CARRIER_UNSUPPORTED"] = "Etiket testi yalnız Trendyol'un ödediği Aras Kargo veya TEX test paketlerinde çalışır.",
        ["STAGE_CONNECTION_REQUIRED"] = "Bu test işlemi yalnız Trendyol test mağazası bağlantısında çalışır.",
        ["STAGE_INVOICE_FIXTURE_REQUIRED"] = "Bu test işlemi yalnız E-Faturam test hesabında çalışır.",
        ["STAGE_LABEL_FRESH_FIXTURE_REQUIRED"] = "Etiket testi yalnız en son oluşturulan test siparişi paketinde çalışır.",
        ["STAGE_LABEL_PICKING_PAYLOAD_INVALID"] = "Test paketi için tek ve geçerli bir ürün satırı ile adet gerekir.",
        ["STAGE_TEST_ORDER_PAYLOAD_INVALID"] = "Test siparişi isteği eksik ya da hatalı; oluşturulmadı.",
        ["STAGE_TEST_ORDER_SCOPE_REQUIRED"] = "Test siparişi yalnız test mağazası bağlantılarında oluşturulabilir.",
        ["HEPSIBURADA_STAGE_LISTING_AMBIGUOUS"] = "Test siparişi barkodu Hepsiburada test mağazasında tek bir ürünle eşleşmedi; sipariş oluşturulmadı.",
        ["HEPSIBURADA_STAGE_SKU_NOT_LINKED"] = "Test siparişi için seçilen ürün bu test mağazasında artık satışta değil.",

        // Reference lists (categories, brands, values)
        ["REFERENCE_CONTRACT_INVALID"] = "Pazaryeri kategori ya da marka listesini eksik bilgiyle döndürdü; paneldeki mevcut liste korunuyor. Daha sonra yeniden eşitleyin.",
        ["REFERENCE_CURSOR_INVALID"] = "Pazaryerinin listesi sayfalar arasında tutarsız geldi; paneldeki mevcut liste korunuyor. Daha sonra yeniden eşitleyin.",
        ["REFERENCE_EMPTY_RESPONSE"] = "Pazaryeri boş bir liste döndürdü; paneldeki mevcut liste korunuyor.",
        ["REFERENCE_IDENTIFIERS_DUPLICATE"] = "Hepsiburada aynı değeri farklı bilgilerle iki kez döndürdü; paneldeki mevcut liste korunuyor.",

        // Invoices
        ["INVOICE_PARTY_SNAPSHOT_INVALID"] = "Faturadaki alıcı bilgileri okunamadı; fatura gönderilmedi. Siparişi yenileyip faturayı yeniden hazırlayın.",
        ["INVOICE_SUBMIT_CONNECTION_MISMATCH"] = "Fatura başka bir e-Fatura bağlantısına ait; gönderilmedi. Faturayı doğru e-Fatura bağlantısından yeniden gönderin.",
        ["INVOICE_SUBMIT_INVOICE_NOT_FOUND"] = "Gönderilecek fatura bulunamadı; gönderim yapılmadı. Fatura ekranından yeniden deneyin.",
        ["INVOICE_SUBMIT_STATE_MISMATCH"] = "Fatura gönderime hazır durumda değil; gönderilmedi. Faturanın durumunu kontrol edip Tekrar Gönder'i kullanın.",
        ["REMOTE_STATUS_MAPPING_REQUIRED"] = "Fatura sağlayıcısı panelin tanımadığı bir durum bildirdi; faturanın sonucu bilinmiyor. Sağlayıcı panelinde kontrol edin.",
        ["DELIVERY_REFERENCE_MISSING"] = "Gönderilmiş fatura bağlantısının pazaryerindeki numarası bulunamadı; yeniden gönderilmedi.",
        ["DELIVERY_TIMEOUT"] = "Fatura bağlantısı gönderilirken pazaryeri zamanında yanıt vermedi; sonuç bilinmiyor. Durum doğrulanmadan yeniden gönderilmedi.",
        ["F4_JOB_REJECTED"] = "Fatura işlemi faturanın şu anki durumunda yapılamaz; gönderilmedi. Faturanın durumunu kontrol edin.",

        // Any job
        ["F3_JOB_REJECTED"] = "İşlem bu kaydın şu anki durumunda yapılamaz ya da mağaza bu işlemi desteklemiyor; pazaryerine gönderilmedi.",
        ["CONNECTION_OUT_OF_SCOPE"] = "Bu mağaza bağlantısı şu an etkin değil; işlem yapılmadı. Bağlantıyı Entegrasyonlar ekranından etkinleştirin.",
        ["CONNECTION_REQUIRED"] = "İşlem bir mağaza bağlantısına bağlı değil; yapılmadı. İşlemi ilgili mağazanın ekranından yeniden başlatın.",
        ["IMPORT_JOB_REJECTED"] = "İçe aktarma, kaydın şu anki durumu ya da kurallara uymayan bir değer yüzünden yapılmadı.",
        ["INVALID_IMPORT_PAYLOAD"] = "İçe aktarma isteği okunamadı; hiçbir kayıt değişmedi. İçe aktarmayı yeniden başlatın.",
        ["UNSUPPORTED_JOB_TYPE"] = "Bu işlem türü panelde tanımlı değil; yapılmadı. Kodu destekle paylaşın.",
        ["DATABASE_CONSTRAINT_VIOLATION"] = "Kayıt panelin veri kurallarından birine takıldı (örneğin aynı kayıt iki kez); işlem durdu. Kodu destekle paylaşın.",
        ["TRANSIENT_EXECUTION_FAILURE"] = "Geçici bir sunucu hatası oldu; işlem otomatik olarak yeniden denenecek.",
        ["UNHANDLED_EXECUTION_FAILURE"] = "Beklenmeyen bir hata oldu; işlem otomatik deneme hakkı içinde yeniden denenecek."
    };

    /// <summary>The sentence a job screen shows for a stored code and summary: the plain one when the code has it.</summary>
    public static string? Display(string? code, string? summary) =>
        code is not null && Messages.TryGetValue(code.Trim().ToUpperInvariant(), out var message) ? message : summary;
}
