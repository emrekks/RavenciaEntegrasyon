# F4 Fatura Operasyon Runbook'u

## Güvenli başlangıç

1. E-Faturam bağlantısını `STAGE`, API `1.0.0` ile oluştur.
2. Credential'ı panelden şifreli kaydet; değerlerin yanıt/log içinde geri dönmediğini doğrula.
3. Bağlantı testini çalıştır. Yalnız sign-in kanıtlanır; diğer capability'ler test firma kanıtı olmadan `UNKNOWN` kalır.
4. Legal entity mali girdilerini yetkili kaynaktan gir; maskeli dönüşü doğrula.
5. Mali kararlar onaylanmadıysa policy alanlarını `UNAPPROVED`, auto-submit'i kapalı bırak.

## Invoice akışı

- Taslak order/package snapshot'ından oluşur; lojistik durum değişmez.
- Yerel validation başarısızsa dış job üretilmez.
- Submit, delivery ve cancellation; CSRF + parola re-auth + açık kullanıcı onayı + idempotency + capability + global/connection write kapılarının tümünü gerektirir.
- `UNKNOWN_RESULT` oluşursa ikinci submit çalıştırma; önce reconciliation uygula.
- Provider ham status doğrulanmış eşleme olmadan `ACCEPTED`, `REJECTED` veya `COMPLETED` sayılmaz.
- Document private FileAsset'ta checksum ile tutulur ve `Cache-Control: private, no-store` ile indirilir.

## Kill switch ve olay müdahalesi

Şüpheli mali etkide global external-write ve `AUTO_INVOICE_ENABLED` kapalı tutulur. Connection write anahtarı da ayrıca kapalı olmalıdır. Credential rotate edilince capability kanıtları `UNKNOWN`a döner. Duplicate, timeout, status eşleme, due veya delivery sorunu OperationalIssue/job attempt üzerinden incelenir; invoice/document satırı elle güncellenmez.

## Dış blocker

Test firma/credential, hesap modeli, mali policy, KVKK/retention, Trendyol Stage package ve Ubuntu sunucu/domain/public HTTPS yokken gerçek submit/delivery/production smoke çalıştırılmaz.
