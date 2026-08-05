# F4 Fatura Operasyon Runbook'u

## Güvenli başlangıç

1. E-Faturam bağlantısını `STAGE`, API `1.0.0` ile oluştur.
2. Credential'ı panelden şifreli kaydet; değerlerin yanıt/log içinde geri dönmediğini doğrula.
3. Bağlantı testini çalıştır. Yalnız sign-in kanıtlanır; diğer capability'ler test firma kanıtı olmadan `UNKNOWN` kalır.
4. Legal entity mali girdilerini yetkili kaynaktan gir; maskeli dönüşü doğrula.
5. Mali kararlar onaylanmadıysa policy alanlarını `UNAPPROVED`, auto-submit'i kapalı bırak.
6. E-Faturam `companyId`, `userId`, fatura serisi ve Temel/Ticari senaryoyu kaydet. Trendyol internet satışı E-Arşiv için kullanılan her kargo sağlayıcısını `Sağlayıcı | VKN/TCKN | Yasal unvan` biçiminde ayrı satırla eşle; eşleşmeyen paket submit edilmez.

## Invoice akışı

- Taslak order/package snapshot'ından oluşur; lojistik durum değişmez.
- Yerel validation başarısızsa dış job üretilmez.
- Submit, delivery ve cancellation; CSRF + parola re-auth + açık kullanıcı onayı + idempotency + capability + global/connection write kapılarının tümünü gerektirir.
- `UNKNOWN_RESULT` oluşursa ikinci submit çalıştırma; önce reconciliation uygula.
- Provider ham status doğrulanmış eşleme olmadan `ACCEPTED`, `REJECTED` veya `COMPLETED` sayılmaz.
- Document private FileAsset'ta checksum ile tutulur ve `Cache-Control: private, no-store` ile indirilir.
- Başarılı submit sonrasında doğrulanmış `InvoiceDocumentRead` capability varsa kalıcı HTTPS PDF bağlantısı alınır; PDF private FileAsset'a yazılır ve bağlantı ayrı alanda saklanır.
- Trendyol link teslimi yalnız kalıcı HTTPS bağlantısı, paket kimliği, fatura numarası ve fatura tarihi birlikte hazırsa çalışır. Başarısız teslim yeni fatura üretmez; ayrı attempt olarak kalır.
- Trendyol sipariş fiyatları KDV dahil kabul edilir. Her sipariş satırı ayrı fatura kalemidir; satırın `vatRate` oranıyla KDV hariç tutar hesaplanır ve toplam, paket/sipariş tutarıyla en fazla bir kuruş toleransla eşleşmeden taslak oluşturulmaz.
- Trendyol'un güncel sipariş sözleşmesinde `lineUnitPrice` indirim sonrası birim fiyat, `packageTotalPrice` ise faturalanacak net paket tutarıdır. Fatura denetimi paket kapsamındaki tahsisli kalemler üzerinden yapılır; siparişin birden fazla paketi varsa sipariş genel toplamı tek paket faturası için kullanılmaz.
- Fatura notu yalnız `YALNIZ: <YAZIYLA ÜCRET TUTARI>` biçimindedir.

## Kill switch ve olay müdahalesi

Şüpheli mali etkide global external-write ve `AUTO_INVOICE_ENABLED` kapalı tutulur. Connection write anahtarı da ayrıca kapalı olmalıdır. Credential rotate edilince capability kanıtları `UNKNOWN`a döner. Duplicate, timeout, status eşleme, due veya delivery sorunu OperationalIssue/job attempt üzerinden incelenir; invoice/document satırı elle güncellenmez.

## Dış blocker

Test firma/credential, doğrulanmış write capability, hesap modeli, mali policy, KVKK/retention ve açık işlem bazlı kullanıcı onayı yokken gerçek submit/delivery çalıştırılmaz. Production dağıtımında global ve bağlantı bazlı dış yazma anahtarları bu kanıtlar tamamlanana kadar kapalı kalır.


## Trendyol E-Faturam operasyon akışı (F4 kod kapanışı)

1. Bağlantıda `API_USER` veya `MARKETPLACE` modeli seçilir. MARKETPLACE için Partner ID `externalStoreId` alanında sayısal olmalıdır.
2. API_USER credential: e-posta/parola. MARKETPLACE credential: partner e-posta/parola + müşteri e-posta/parola + VKN/TCKN. Credential hiçbir yanıtta gösterilmez.
3. Company ID, User ID, 3 karakter seri, Temel/Ticari E-Fatura senaryosu ve kullanılan tüm Trendyol kargo sağlayıcılarının VKN/TCKN-yasal unvan eşlemeleri kaydedilir. Şifre içermeyen ayarlar panelde güvenli read-back ile önceden doldurulur; gönderilmeyen PATCH alanları korunur.
4. Connection test ve gerekli capability evidence kaydı tamamlanır. Write capability için Stage/SIT fixture SHA-256 zorunludur.
5. Sipariş paketinden taslak oluşturulur ve `VALIDATE` çalıştırılır. E-Arşiv için package, purchase URL ve carrier mapping eksikse submit açılmaz.
6. `SUBMIT` yalnız parola + açık onay, ETag, idempotency ve tüm write kapılarıyla kuyruğa alınır.
7. E-Arşiv `status/{uuid}` ile uzlaştırılır. 205 kabul, 305 iptal, 105/29/405 ret/hata; bilinmeyen kod manuel incelemedir.
8. Kabulden sonra permanent PDF URL alınır, güvenli indirilir ve private storage'a yazılır. PDF oluşması 205 yerine geçmez.
9. Kalıcı HTTPS link Trendyol paketine gönderilir; kesin teyit yoksa `SUBMITTED` kaydı korunur ve manuel inceleme açılır.
10. E-Arşiv iptal isteği terminal başarı sayılmaz; 305 görülene kadar `CANCELLATION_PENDING` kalır. E-Fatura iptali bu otomasyondan yapılmaz.

### Giden E-Fatura status endpoint kapısı

`MARKETPLACEHUB_EFATURAM_EINVOICE_STATUS_PATH` varsayılan olarak boştur. Yalnız resmî/Stage doğrulanmış göreli API yolu, source URL, environment/store scope ve fixture checksum kaydedildikten sonra deployment'a eklenir. Tam URL, `..` veya doğrulanmamış tahmini yol kabul edilmez.
