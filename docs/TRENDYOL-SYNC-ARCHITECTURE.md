# Trendyol veri eşitleme mimarisi

## Kaynak sınırları

Ravencia panelinin operasyonel okuma kaynağı yerel PostgreSQL veritabanıdır. Ürün, sipariş, iade ve fatura liste/detay ekranları bir sayfa açıldığında Trendyol'a istek göndermez.

| Panel alanı | Yerel kaynak |
| --- | --- |
| Ürünler | `catalog.products`, varyant, stok ve medya tabloları |
| Siparişler | `sales.orders`, `sales.order_lines`, `sales.shipment_packages` ve durum geçmişi |
| İadeler | `sales.return_claims`, `sales.return_lines` ve ilişkili sipariş snapshot'ı |
| Faturalar | Yerel sipariş, paket ve fatura tabloları |

Trendyol yalnız arka plan eşitleme katmanının dış kaynağıdır. Kullanıcının açıkça başlattığı paket/iade aksiyonları da dayanıklı iş kuyruğundan yürür; sonuç daha sonra tekrar okunup yerel veritabanına işlenir.

## Sipariş akışı

1. İlk kurulum veya tam tarama, Trendyol'un erişilebilir son üç aylık verisini en fazla 14 günlük pencerelere böler.
2. Her pencere `/orders/stream` üzerinden `nextCursor` ile, sayfa başına en fazla 200 paket olarak okunur.
3. Devam sayfaları arasında en az beş saniye beklenir ve opaque cursor değiştirilmeden saklanır.
4. Sipariş, satır, paket, paket-satır miktarları ve durum geçmişi idempotent anahtarlarla yerel veritabanına upsert edilir.
5. Başarılı tur sonunda `LastModifiedWatermark`, turun sabit bitiş zamanına ilerletilir.
6. Sonraki tur yalnız watermark ile şimdiki zaman arasını, ayarlanmış güvenlik örtüşmesiyle birlikte okur.
7. İşçi 14 günden uzun süre durmuşsa aralık otomatik olarak 14 günlük parçalara ayrılır; erişilebilir değişiklikler atlanmaz.

Bu akış yeni siparişleri, paket/sipariş durum değişikliklerini, tam veya kısmi iptal sonrası oluşan paketleri, sevk ve teslim durumlarını aynı yerel modele yazar. Eski veya geriye götüren olaylar mevcut durumu bozmaz; olay geçmişinde/audit kaydında izlenir.

## Webhook ve uzlaştırma

Webhook alım hattı düşük gecikmeli tam sipariş paketini dayanıklı inbox ve iş kuyruğuna yazar. Trendyol webhook tesliminin her zaman mümkün olmayabileceğini belirttiği için cursor tabanlı periyodik stream eşitlemesi kapatılmaz; webhook hız, polling ise kaçırılan olaylara karşı uzlaştırma sağlar.

## İade akışı

`getClaims` tarih filtresi `lastModifiedDate` yerine iade oluşturma tarihine göre çalıştığından sipariş watermark mantığı doğrudan iadelere uygulanmaz. İade işi son 14 günlük oluşturma penceresini idempotent biçimde yeniden tarar; durum değişiklikleri `lastModifiedDate` sırasından alınarak yerel claim ve satır kayıtlarına yazılır. İlk tarama erişilebilir son üç ayı alır.

## Ürün akışı

Aktif veya doğrulanmış STAGE/PRODUCTION Trendyol bağlantılarında onaylı ürünler arka planda yerel kataloğa alınır. Sağlayıcının en fazla 100 kayıtlık sayfaları `nextPageToken` ile sonuna kadar izlenir; ilk tarama tüm erişilebilir kataloğu, sonraki taramalar kısa bir güvenlik örtüşmesiyle değişen ürünleri işler. Panel toplamı tek bir sağlayıcı sayfasıyla sınırlandırılmaz.

## Yönetim

Trendyol bağlantı detayındaki **Otomatik veritabanı eşitleme** bölümü şunları yönetir:

- sipariş/iade/ürün eşitleme işinin açık veya kapalı olması,
- kontrol aralığı,
- siparişlerde geç gelen değişiklikler için güvenlik örtüşmesi,
- son başarılı adım ve sipariş değişiklik watermark zamanı.

Önerilen başlangıç değeri siparişlerde 5 dakikalık kontrol ve en az 2 dakikalık örtüşmedir. Webhook etkin ve sağlıklı olsa bile periyodik eşitleme açık tutulmalıdır.

## Resmî sözleşme

- Stream: <https://developers.trendyol.com/docs/sipari%C5%9F-paketlerini-ak%C4%B1%C5%9F-ile-%C3%A7ekme>
- Webhook modeli: <https://developers.trendyol.com/docs/webhook-model>
- İade talepleri: <https://developers.trendyol.com/docs/i%CC%87adesi-olu%C5%9Fturulan-sipari%C5%9Fleri-%C3%A7ekme-getclaims>
- Onaylı ürünler: <https://developers.trendyol.com/tr/v2.0/docs/product-filtering-approved-products-v2>
