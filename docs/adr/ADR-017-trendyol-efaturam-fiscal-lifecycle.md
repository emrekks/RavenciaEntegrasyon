# ADR-017: Trendyol E-Faturam Sağlayıcı Tarafından Yönetilen Mali Hesap ve Fail-Closed Yaşam Döngüsü

**Durum:** Kabul edildi
**Tarih:** 2026-08-05

## Bağlam

Ravencia kendi Trendyol E-Faturam hesabını kullanır. Firma, kullanıcı, fatura serisi ve diğer gönderen mali bilgiler zaten E-Faturam hesabında yönetilir; panelde ikinci bir mali hesap kopyası oluşturmak veri tutarsızlığı ve yanlış firma kapsamına fatura kesme riski üretir.

Trendyol sipariş snapshot'ı `commercial` ve `invoiceAddress.eInvoiceAvailable` alanlarını sağlar. İnternet satışı E-Arşiv isteklerinde E-Faturam sözleşmesi ödeme ve teslim bilgilerini ister; bu bilgiler kullanıcı ayarı değil, Trendyol sipariş/paket verisinden ve resmî Trendyol kargo sağlayıcı kataloğundan türetilen teknik payload alanlarıdır. Provider mali durumları sayısal kodlarla döner ve HTTP 2xx veya PDF üretimi tek başına nihai kabul değildir.

## Karar

1. E-Faturam bağlantısı yalnız doğrudan `API_USER` modeliyle `signIn` kullanır. Panel yalnız E-Faturam e-posta/parolasını şifreli saklar; partner/müşteri credential akışı aktif ürün kapsamında yoktur.
2. `companyId` ve `userId`, başarılı giriş tokenındaki doğrulanmış kapsamdan alınır. Panelde girilmez, API ile geri okunmaz ve bağlantı ayarlarında saklanmaz.
3. Fatura serisi/prefix gönderilmez; E-Faturam hesabındaki varsayılan seri kullanılır.
4. Belge türü otomatik seçilir:
   - `commercial=true` ve `invoiceAddress.eInvoiceAvailable=true` ise `TEMELFATURA`;
   - diğer tüm siparişler `EARSIVFATURA`.
   Ayrı mükellef sorgusu veya kullanıcı tarafından Temel/Ticari seçimi yapılmaz.
5. Ödeme ve taşıyıcı alanları kullanıcı ayarı değildir. E-Arşiv internet satışı payloadında ödeme bilgisi Trendyol sipariş bağlamından, taşıyıcı VKN/unvanı resmî Trendyol kargo kataloğundan otomatik üretilir. Bilinmeyen kargo sağlayıcısında sistem tahmin yapmaz ve gönderimi bloklar.
6. Fatura submit yalnız capability, global write switch, connection write switch, `AUTO_INVOICE_ENABLED`, parola ile yeniden doğrulama ve açık onay birlikte geçerse çalışır.
7. Uzak durumlar `10/20/29/30/40/50/100/105/200/205/305/405` kod kataloğuyla canonical duruma çevrilir. Bilinmeyen kod `MANUAL_REVIEW` olur.
8. E-Arşiv durum ve iptal servisleri resmî endpointlerle çalışır. Giden E-Fatura UUID durum endpoint'i public sözleşmede kesinleştirilmediği için tahmin edilmez; exact Stage/SIT kanıtlı göreli yol yalnız deployment configuration üzerinden verilebilir.
9. Kalıcı PDF URL güvenli indirici sınırından geçer ve private immutable storage'a alınır. PDF varlığı mali kabul değildir.
10. E-Fatura otomatik iptali açılmaz; mevzuata uygun itiraz/iptal iş akışı operatör incelemesine bırakılır. E-Arşiv iptal isteği `CANCELLATION_PENDING` olarak uzlaştırılır.
11. Önceki sürümlerde connection settings içinde tutulmuş company/user/prefix/senaryo/kargo/ödeme alanları veri migrasyonuyla temizlenir. Temizlik geri döndürülemez; yalnız `ExternalWritesEnabled` korunur.

## Sonuç

Panelde mali hesap, seri, senaryo, ödeme veya kargo eşleme formu bulunmaz. Kod yalnız müşteri/sipariş verisini ve provider tarafından yönetilen hesap kapsamını kullanır. Exact runtime ve Stage/SIT kabulü olmadan capability satırları `SUPPORTED` yapılamaz ve production dış yazmaları kapalı kalır.
