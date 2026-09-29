# Hepsiburada Entegrasyonu Yol Haritası

## Amaç ve kapsam

Hepsiburada’yı mevcut panelin ürün, fiyat/stok, sipariş, kargo, iade ve fatura akışlarına aşamalı olarak eklemek. İlk canlı teslimat yalnızca veri okuyacak; dış yazma işlemleri Hepsiburada SIT ortamında doğrulanıp yetenek bazında açılacak.

Kapsam mevcut panel modülleridir. HepsiJet, muhasebe ve Satıcıya Sor servisleri bu çalışmaya dahil değildir. Projede Hepsiburada logosu ve bazı arayüz metinleri bulunuyor; çalışan bir Hepsiburada API adaptörü henüz yok. Bilinmeyen platform kodlarının Trendyol adaptörüne düşmesi de entegrasyondan önce kapatılmalıdır.

## Aşamalar

| Aşama | Yapılacak iş | Çıkış koşulu |
|---|---|---|
| **0. Erişim ve API sözleşmesi** | SIT ve canlı `merchantId`, kullanıcı adı ve servis anahtarı erişimini doğrula. Her API ailesinin kimlik doğrulamasını, ortam URL’lerini, limitlerini ve test senaryolarını kaydet. Anahtarları şifreli bağlantı kaydında tut; kaynak koduna veya loglara yazma. Başlangıç kaynakları: [servis anahtarı rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=entegratore-servis-anahtari-eklemegoruntuleme&product=api-authentication&view=guide) ve [sipariş rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=siparis-entegrasyonu-onemli-bilgiler&product=siparis-olusturma-entegrasyonu&view=guide). | SIT bağlantı testi başarılı; erişilebilir API listesi, auth biçimi, limitler ve örnek yanıt sözleşmeleri kayıtlı. |
| **1. Bağlantı ve güvenli okuma** | `HEPSIBURADA` platformunu bağlantı API’sine ve Entegrasyonlar ekranına ekle. `externalStoreId=merchantId` kullan. Bilinmeyen platform yönlendirmesini hata verecek şekilde değiştir. Ayrı Hepsiburada adaptörü ve iş türleriyle ürün listelemelerini, siparişleri ve paketleri kuyruk işleri üzerinden yerel veritabanına al. Sayfalama, mükerrer kayıt güvenliği, son okuma zamanı ve hata görünürlüğü ekle. | İlk canlı teslimatta siparişler ve listelemeler panelde görünür; dış yazma kapalıdır. |
| **2. Ürün, fiyat ve stok** | Panel ürünlerini Hepsiburada kategorileri ve zorunlu özellikleriyle eşleştir. Barkod, `merchantSku`, `hbSku` ve varyant grubu kimliklerini bağlantı kapsamında sakla. Ürün gönderiminden dönen `trackingId` sonucunu izle ve uzaktaki durumu yeniden okuyarak doğrula. Listeleme fiyatı ve stoğunu ayrı işler olarak gönder; kısmi ret ve başarısız satırları İş Takibi’nde göster. Başlangıç kaynağı: [katalog rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=katalog-onemli-bilgiler&product=katalog-urun-entegrasyonu&view=guide). | SIT’te ürün oluşturma/güncelleme ile fiyat ve stok gönderimleri uzaktan yeniden okunarak doğrulanmış. |
| **3. Sipariş, kargo ve fatura** | Desteklenen paket, kargo barkodu ve takip işlemlerini ekle. Faturayı mevcut E-Faturam akışında üret; Hepsiburada’ya paket bazında erişilebilir HTTPS belge bağlantısı gönder. Trendyol’a sabit bağlı fatura gönderim portunu platforma göre yönlendir ve Hepsiburada `hasInvoice` sonucuyla uzlaştır. Başlangıç kaynağı: [paket fatura bağlantısı API’si](https://developers.hepsiburada.com/tr/companies/hepsiburada?category=siparis-yonetimi&op=Put__packages_merchantid_merchantId_packagenumber_packageNumber_invoice&product=siparis-olusturma-entegrasyonu&version=v1.0&view=endpoint). | SIT’te paket ve fatura işlemleri uzaktan doğrulanmış; erişilemeyen belge bağlantısı gönderilmiyor. |
| **4. İade ve canlıya genişletme** | Hepsiburada talep durumlarını, son işlem zamanlarını, kabul/red gerekçelerini ve desteklenmeyen işlemler için panel yönlendirmesini ekle. Sağlayıcı testi ve onayı sonrası webhook’u aç; periyodik okuma uzlaştırma için sürsün. Başlangıç kaynakları: [talep rehberi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide) ve [sipariş webhook koşulları](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=siparis-webhook-modeli&product=siparis-olusturma-entegrasyonu&view=guide). | Tek mağazalık pilotta sipariş, ürün, fatura ve talep sayıları ile paneldeki ve uzaktaki durumlar tutarlı. |

## Teknik kararlar ve güvenlik kapıları

- Her Hepsiburada işi bağlantı ve tenant kapsamındaki uzak kimliğe göre tekrar çalıştırılabilir olmalı. Eşitleme aynı kaydı yeniden aldığında çoğaltmamalı.
- HTTP başarı yanıtı tek başına yazma başarısı sayılmamalı. Ürün `trackingId`, sipariş/paket ve fatura sonuçları uzaktan yeniden okunarak doğrulanmalı.
- `429` yanıtları ile limit başlıkları kontrollü yeniden denemeye alınmalı; bağlantı başına son başarılı okuma ve hata bilgisi saklanmalı.
- Dış yazma için genel dış yazma anahtarı, bağlantı ayarı ve ilgili yeteneğin SIT kanıtı birlikte zorunlu olmalı. Yetenek kanıtlanana kadar `Unknown` kalmalı.
- Ürün, fiyat, stok, kargo, fatura ve iade yazmaları birbirinden bağımsız yetenek kapılarıyla açılmalı. Canlı pilot önce okuma modunda çalışmalı.
- Sipariş webhook’ları sağlayıcı onayı gerektirir ve API üzerinden yapılan işlemler webhook’ta tekrar bildirilmez; periyodik okuma uzlaştırma akışı korunmalı.
- SIT ve canlı kimlik bilgileri ayrı tutulmalı. Üretim anahtarları loglanmamalı, iş payload’larına eklenmemeli ve uygulamanın şifreli credential saklama mekanizması dışında tutulmamalı.

## Kabul doğrulamaları

SIT kabul seti aşağıdakileri kapsamalı:

1. Kimlik doğrulama ve yanlış credential hatalarının açık raporlanması.
2. Listeleme ve sipariş sayfalaması, tekrar gelen kayıtlar ve bağlantı kapsamı.
3. Kısmi ürün reddi ve `trackingId` sonucunun takibi.
4. Fiyat/stok gönderimi ve uzaktaki değerlerle uzlaştırma.
5. Paket ve kargo akışı.
6. Fatura bağlantısının erişilebilirliği ve `hasInvoice` uzlaştırması.
7. Talep okuma, kabul/red gerekçeleri ve desteklenmeyen durumların kullanıcıya gösterilmesi.
8. `429` yanıtı ve geçici ağ hatalarında kontrollü tekrar deneme.

Her yeteneğin SIT kanıtı kaydedilmeden ilgili canlı yazma açılmaz. Canlıya çıkışta commit, build/test, sunucunun çektiği sürüm ve `/health/ready` kontrolü kayda alınır.

## Varsayımlar ve süre

- SIT ve canlı erişimi henüz doğrulanmış kabul edilmiyor. Aşama 0, canlı dış yazma için zorunlu kapıdır.
- Teknik çalışma tahmini tek geliştirici için yaklaşık **6–10 hafta**; Hepsiburada erişim, hesap yetkisi ve sağlayıcı onay süreleri bu tahmine dahil değildir.
- API endpoint’leri, auth biçimleri, limitler ve yanıt şemaları kodlanmadan önce güncel resmî doküman ve SIT yanıtlarıyla tekrar doğrulanmalıdır.

