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

Güvenlik kapısı: sign-in dışındaki provider çağrıları, anonim test firma kanıtı ve mali politika onayı tamamlanana kadar adapter içinde `NOT_SUPPORTED` ile kapalıdır. Bir capability yalnız resmî kaynak ile tekrarlanabilir test kanıtı birlikte mevcutsa `SUPPORTED` olabilir. Ham provider status değerleri doğrulanmış eşleme olmadan domain sonucuna çevrilmez.
