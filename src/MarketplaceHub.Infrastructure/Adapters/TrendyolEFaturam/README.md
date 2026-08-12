# Trendyol E-Faturam adapter sınırı

Bu adapter F4 kapsamındadır ve API sürümünü `1.0.0` olarak sabitler. Stage ve production kök adresleri ile doğrudan API kullanıcısı `signIn` sözleşmesi resmî entegrasyon dokümanına dayanır. Credential yalnız şifreli persistence üzerinden okunur; access token saklanmaz veya loglanmaz.

## Sağlayıcı tarafından yönetilen hesap

- Panel partner e-posta/parolası ile Stage test müşteri e-posta/parolası ve VKN/TCKN'sini şifreli olarak alır.
- `companyId`, `userId` ve müşteri access token'ı partner `signIn` sonrasındaki `customerSignIn` yanıtından okunur.
- Prefix/seri gönderilmez; E-Faturam hesabındaki varsayılan seri kullanılır.
- Manuel mali hesap, Temel/Ticari senaryo, ödeme ve kargo eşleme ayarları aktif kapsamda değildir.
- Belge türü Trendyol siparişindeki `commercial` ve `eInvoiceAvailable` alanlarından otomatik seçilir.
- E-Arşiv internet satışı için gereken `paymentInfo` ve `deliveryInfo`, kullanıcı ayarı olmadan Trendyol sipariş/paket verisi ve resmî kargo kataloğundan üretilir. Bilinmeyen taşıyıcı fail-closed hata üretir.

Resmî kaynaklar:

- Entegrasyon dokümanı: <https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani>
- Sign-in: <https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in>
- Giden E-Fatura: <https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice>
- E-Arşiv durum: <https://developers.trendyolefaturam.com/OpenApi/eArşiv/get-e-archive-status>
- Kalıcı belge bağlantısı: <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-permanent-document-download-url>
- E-Arşiv iptal: <https://developers.trendyolefaturam.com/OpenApi/eArşiv/cancel-e-archive>

Güvenlik kapısı: Stage manuel çağrıları aktif/doğrulanmış bağlantı, şifreli credential, input doğrulama, idempotency ve provider yanıt doğrulamasıyla çalışır; capability/evidence, switch ve ek onay runtime blocker değildir. Production yazmaları aktif bağlantı, global + connection switch ve mevcut mali güvenlik zincirini ister. Giden E-Fatura için exact durum sorgusu yolu yapılandırılmadıkça uzlaştırma `EFATURAM_EINVOICE_STATUS_PATH_NOT_CONFIGURED` ile güvenli biçimde durur. Ham provider durumları yalnız doğrulanmış sayısal katalog üzerinden domain sonucuna çevrilir.
