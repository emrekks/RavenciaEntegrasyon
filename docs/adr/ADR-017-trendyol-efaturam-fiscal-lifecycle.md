# ADR-017: Trendyol E-Faturam Mali Yaşam Döngüsü ve Fail-Closed Durum Sorgusu

**Durum:** Kabul edildi
**Tarih:** 2026-08-05

## Bağlam

Trendyol E-Faturam iki kimlik doğrulama modeli sunar. `API_USER` doğrudan `signIn` kullanır. `MARKETPLACE` modeli önce partner `signIn`, ardından müşteri `customerSignIn` çağrısı ile company/user scope ve müşteri tokenı üretir. İnternet satışı E-Arşiv belgelerinde ödeme ve teslim bilgileri zorunludur. Provider mali durumları sayısal kodlarla döner ve HTTP 2xx veya PDF üretimi tek başına nihai kabul değildir.

## Karar

1. Credential modelleri ayrı doğrulanır ve şifreli saklanır.
2. Fatura submit yalnız capability, global write switch, connection write switch, AUTO_INVOICE_ENABLED, parola ile yeniden doğrulama ve açık onay birlikte geçerse çalışır.
3. E-Arşiv internet satışı payloadında `paymentInfo` ve `deliveryInfo` zorunludur; kargo sağlayıcısı VKN/TCKN ve yasal unvanla bağlantı ayarında eşlenir.
4. Uzak durumlar `10/20/29/30/40/50/100/105/200/205/305/405` kod kataloğuyla canonical duruma çevrilir. Bilinmeyen kod `MANUAL_REVIEW` olur.
5. E-Arşiv durum ve iptal servisleri resmî endpointlerle çalışır. Giden E-Fatura UUID durum endpoint'i public sözleşmede kesinleştirilmediği için tahmin edilmez; exact Stage/SIT kanıtlı göreli yol yalnız deployment configuration üzerinden verilebilir. Varsayılan değer boştur.
6. Kalıcı PDF URL güvenli indirici sınırından geçer ve private immutable storage'a alınır. PDF varlığı mali kabul değildir.
7. E-Fatura otomatik iptali açılmaz; mevzuata uygun itiraz/iptal iş akışı operatör incelemesine bırakılır. E-Arşiv iptal isteği `CANCELLATION_PENDING` olarak uzlaştırılır.

## Sonuç

Kod kapsamı tamamlanır; ancak exact runtime ve Stage/SIT kabulü olmadan capability satırları `SUPPORTED` yapılamaz ve production dış yazmaları kapalı kalır.
