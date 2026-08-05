# Trendyol E-Faturam adapter sınırı

Bu adapter F4 kapsamındadır ve API sürümünü `1.0.0` olarak sabitler. Stage ve production kök adresleri ile doğrudan API kullanıcısı `signIn` sözleşmesi resmî entegrasyon dokümanına dayanır. Credential yalnız şifreli persistence üzerinden okunur; access token saklanmaz veya loglanmaz.

## Sağlayıcı tarafından yönetilen hesap

- Panel yalnız E-Faturam e-posta/parolasını alır.
- `companyId` ve `userId` giriş tokenından okunur.
- Prefix/seri gönderilmez; E-Faturam hesabındaki varsayılan seri kullanılır.
- Partner/customerSignIn, manuel mali hesap, Temel/Ticari senaryo, ödeme ve kargo eşleme ayarları aktif kapsamda değildir.
- Belge türü Trendyol siparişindeki `commercial` ve `eInvoiceAvailable` alanlarından otomatik seçilir.
- E-Arşiv internet satışı için gereken `paymentInfo` ve `deliveryInfo`, kullanıcı ayarı olmadan Trendyol sipariş/paket verisi ve resmî kargo kataloğundan üretilir. Bilinmeyen taşıyıcı fail-closed hata üretir.

Resmî kaynaklar:

- Entegrasyon dokümanı: <https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani>
- Sign-in: <https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in>
- Giden E-Fatura: <https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice>
- E-Arşiv durum: <https://developers.trendyolefaturam.com/OpenApi/eArşiv/get-e-archive-status>
- Kalıcı belge bağlantısı: <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-permanent-document-download-url>
- E-Arşiv iptal: <https://developers.trendyolefaturam.com/OpenApi/eArşiv/cancel-e-archive>

Güvenlik kapısı: provider çağrıları capability bazında fail-closed çalışır. Fatura gönderimi, E-Arşiv iptali ve Trendyol fatura bağlantısı teslimi için tarihli Stage/SIT fixture SHA-256 kanıtı, global dış-yazma anahtarı, bağlantı dış-yazma anahtarı ve açık operatör onayı birlikte gerekir. Giden E-Fatura için exact durum sorgusu yolu Stage/SIT kanıtıyla yapılandırılmadıkça uzlaştırma `EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED` ile güvenli biçimde durur. Ham provider durumları yalnız doğrulanmış sayısal katalog üzerinden domain sonucuna çevrilir.
