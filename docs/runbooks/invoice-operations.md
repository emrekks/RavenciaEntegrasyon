# F4 Fatura Operasyon Runbook'u

## Güvenli başlangıç

1. E-Faturam bağlantısını `STAGE`, API `1.0.0` ile oluştur.
2. Yalnız doğrudan E-Faturam hesap e-postası ve parolasını panelden şifreli kaydet; değerlerin yanıt/log içinde geri dönmediğini doğrula.
3. Bağlantı testini çalıştır. Başarılı `signIn` tokenından `companyId/userId` kapsamı okunamıyorsa bağlantı geçmez. Firma kimliği, kullanıcı kimliği veya seri panelden girilmez.
4. Fatura politikası `MANUAL_CONFIRMED`, `SHIPMENT_PACKAGE` ve auto-submit kapalı olarak tutulur.
5. Stage/SIT fixture ve capability kanıtı olmadan submit, iptal veya Trendyol link teslimi açılmaz.
6. Önceki MARKETPLACE/manuel mali ayar sürümünden yükseltiliyorsa migration eski `SettingsJson` alanlarını geri döndürülemez biçimde temizler; doğrudan E-Faturam e-posta/parolası yeniden kaydedilir ve capability kanıtları tekrar doğrulanır.

## Otomatik belge türü kararı

- `commercial=true` ve `invoiceAddress.eInvoiceAvailable=true` ise `TEMELFATURA`.
- Bireysel sipariş veya kurumsal olup E-Fatura uygunluğu bulunmayan sipariş `EARSIVFATURA`.
- Kullanıcı Temel/Ticari senaryo seçmez ve ayrı mükellef sorgusu çalıştırmaz.
- Karar Trendyol sipariş snapshot'ından deterministik üretilir; eksik/bozuk snapshot güvenli tarafta E-Arşiv olarak ele alınır ve gerekli müşteri bilgileri yoksa validation bloklar.

## İnternet satışı alanları

Ödeme ve taşıma bilgileri panel ayarı değildir. E-Arşiv internet satışı payloadı hazırlanırken:

- satın alma adresi ve ödeme aracısı Trendyol bağlamından otomatik yazılır;
- ödeme tarihi sipariş zamanından alınır;
- taşıyıcı VKN/unvanı paket üzerindeki provider kodunun resmî Trendyol kargo kataloğundaki karşılığından alınır;
- bilinmeyen sağlayıcı için tahmin yapılmaz; `EFATURAM_CARRIER_CATALOG_MISS` ile submit durur.

## Invoice akışı

- Taslak order/package snapshot'ından oluşur; lojistik durum değişmez.
- Tamamen iptal edilmiş veya pozitif faturalanabilir miktarı olmayan satırlar faturaya girmez; geriye satır kalmazsa taslak oluşturulmaz.
- Yerel validation başarısızsa dış job üretilmez.
- Submit, delivery ve cancellation; CSRF + parola re-auth + açık kullanıcı onayı + idempotency + capability + global/connection write kapılarının tümünü gerektirir.
- `UNKNOWN_RESULT` oluşursa ikinci submit çalıştırma; önce reconciliation uygula.
- Provider ham status doğrulanmış sayısal eşleme olmadan `ACCEPTED`, `REJECTED` veya `COMPLETED` sayılmaz.
- E-Arşiv `status/{uuid}` ile uzlaştırılır. 205 kabul, 305 iptal, 105/29/405 ret/hata; bilinmeyen kod manuel incelemedir.
- Giden E-Fatura status yolu yalnız Stage/SIT kanıtlı `MARKETPLACEHUB_EFATURAM_EINVOICE_STATUS_PATH` göreli ayarıyla açılır. Tam URL, `..` veya doğrulanmamış tahmini yol kabul edilmez.
- Belge private FileAsset'ta checksum ile tutulur ve `Cache-Control: private, no-store` ile indirilir.
- Kalıcı PDF varlığı provider kabul kodunun yerine geçmez.
- Trendyol link teslimi yalnız kalıcı HTTPS bağlantısı, paket kimliği, fatura numarası ve fatura tarihi birlikte hazırsa çalışır. Başarısız teslim yeni fatura üretmez; ayrı attempt olarak kalır.
- Trendyol sipariş fiyatları KDV dahil kabul edilir. Her sipariş satırı ayrı fatura kalemidir; toplam paket tutarıyla en fazla bir kuruş toleransla eşleşmeden taslak gönderilmez.
- Fatura notu yalnız `YALNIZ: <YAZIYLA ÜCRET TUTARI>` biçimindedir.

## Kill switch ve olay müdahalesi

Şüpheli mali etkide global external-write ve `AUTO_INVOICE_ENABLED` kapalı tutulur. Connection write anahtarı da ayrıca kapalı olmalıdır. Credential rotate edilince capability kanıtları `UNKNOWN`a döner. Duplicate, timeout, status eşleme, due veya delivery sorunu OperationalIssue/job attempt üzerinden incelenir; invoice/document satırı elle güncellenmez.

## Dış blocker

Exact runtime testleri, E-Faturam Stage credential, kontrollü sipariş/paket verisi, doğrulanmış write capability ve açık işlem bazlı kullanıcı onayı yokken gerçek submit/delivery çalıştırılmaz. Production dağıtımında global ve bağlantı bazlı dış yazma anahtarları bu kanıtlar tamamlanana kadar kapalı kalır.
