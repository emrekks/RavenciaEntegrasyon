# F4 Fatura Operasyon Runbook'u

## Güvenli başlangıç

1. E-Faturam bağlantısını `STAGE`, API `1.0.0` ile oluştur.
2. Credential'ı panelden şifreli kaydet; değerlerin yanıt/log içinde geri dönmediğini doğrula.
3. Bağlantı testini çalıştır. Yalnız sign-in kanıtlanır; diğer capability'ler test firma kanıtı olmadan `UNKNOWN` kalır.
4. Legal entity mali girdilerini yetkili kaynaktan gir; maskeli dönüşü doğrula.
5. Mali kararlar onaylanmadıysa policy alanlarını `UNAPPROVED`, auto-submit'i kapalı bırak.
6. E-Faturam `companyId`, `userId`, fatura serisi ve siparişteki adla eşleşen kargo firması VKN/TCKN + yasal unvanını kaydet. Eksik veya eşleşmeyen kimlikte E-Arşiv gönderimi fail-closed durur.

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
- Fatura notu yalnız `YALNIZ: <YAZIYLA ÜCRET TUTARI>` biçimindedir.

## Kill switch ve olay müdahalesi

Şüpheli mali etkide global external-write ve `AUTO_INVOICE_ENABLED` kapalı tutulur. Connection write anahtarı da ayrıca kapalı olmalıdır. Credential rotate edilince capability kanıtları `UNKNOWN`a döner. Duplicate, timeout, status eşleme, due veya delivery sorunu OperationalIssue/job attempt üzerinden incelenir; invoice/document satırı elle güncellenmez.

## Dış blocker

Test firma/credential, doğrulanmış write capability, hesap modeli, mali policy, KVKK/retention ve açık işlem bazlı kullanıcı onayı yokken gerçek submit/delivery çalıştırılmaz. Production dağıtımında global ve bağlantı bazlı dış yazma anahtarları bu kanıtlar tamamlanana kadar kapalı kalır.
