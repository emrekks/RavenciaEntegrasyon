# Trendyol E-Faturam adapter sınırı

Bu adapter F4 kapsamındadır ve API sürümünü `1.0.0` olarak sabitler. Stage ve production kök adresleri ile sign-in sözleşmesi resmî entegrasyon dokümanına dayanır. Credential yalnız şifreli persistence üzerinden okunur; access/refresh token saklanmaz veya loglanmaz.

Resmî kaynaklar:

- Entegrasyon dokümanı: <https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani>
- Sign-in: <https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in>
- Mükellef sorgusu: <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-application-status-by-tax-id>
- Giden E-Fatura: <https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice>
- E-Arşiv durum: <https://developers.trendyolefaturam.com/OpenApi/eArşiv/get-e-archive-status>
- Kalıcı belge bağlantısı: <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-permanent-document-download-url>
- E-Arşiv iptal: <https://developers.trendyolefaturam.com/OpenApi/eArşiv/cancel-e-archive>

Güvenlik kapısı: provider çağrıları capability bazında fail-closed çalışır. Salt-okunur bağlantı/mükellefiyet sorguları ilgili capability doğrulanmadan açılmaz; fatura gönderimi, E-Arşiv iptali ve Trendyol fatura bağlantısı teslimi için ayrıca tarihli Stage/SIT fixture SHA-256 kanıtı, global dış-yazma anahtarı, bağlantı dış-yazma anahtarı ve açık operatör onayı birlikte gerekir. Giden E-Fatura için exact durum sorgusu yolu Stage/SIT kanıtıyla yapılandırılmadıkça uzlaştırma `EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED` ile güvenli biçimde durur. Ham provider durumları yalnız doğrulanmış sayısal katalog üzerinden domain sonucuna çevrilir.
