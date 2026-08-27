# Trendyol senkronizasyon mimarisi

Bu belge, Trendyol odaklı entegrasyon gereksinimlerinin Ravencia’daki uygulanmış karşılığıdır. Yerel PostgreSQL veritabanı panelin operasyonel kaynağıdır; Trendyol yalnızca tanımlı okuma ve yazma adapter’ları üzerinden erişilir.

## Kapsam ve temel sınırlar

- Aktif gerçek sağlayıcı Trendyol’dur. Trendyol E-Faturam fatura akışında ayrı bir sağlayıcı olarak korunur; kapsam dışı pazar yerleri için sahte adapter veya boş worker eklenmez.
- Worker sipariş, iade, ürün, referans ve stok işlerini kalıcı `IntegrationJob` kuyruğundan alır. Aynı fiziksel worker içinde öncelik ve lease ile ayrılmış hot, recovery, lifecycle ve reconciliation hatları vardır.
- Her yazma isteği idempotency anahtarı, external effect kaydı ve read-back/reconciliation ile korunur. Ağ kopması sonrası dış etkinin kesinliği yoksa tekrar yazmak yerine `MANUAL_REVIEW` durumuna geçilir.
- Kullanıcı arayüzündeki yerel kaydetme Trendyol’a otomatik yazma yapmaz. Ürün importu salt-okunurdur; yayınlama, güncelleme, arşivleme ve fiyat/stok gönderimi açık bir iş olarak kuyruğa alınır.

## Kalıcı kuyruk ve worker yürütümü

`IntegrationJob` PostgreSQL’de tutulur. Job sahiplenme `FOR UPDATE SKIP LOCKED`, lease süresi, heartbeat, attempt sayısı, deduplication ve dead-letter durumlarıyla yapılır. Worker yeniden başlasa veya iki worker aynı anda çalışsa iş kaybolmaz ve aynı job iki kez sahiplenilmez.

Öncelik sırası hot operasyonları korur:

| Akış | Job/resource | Varsayılan aralık | Öncelik |
| --- | --- | ---: | ---: |
| Sipariş hot akışı | `TRENDYOL_ORDER_SYNC` / `ORDERS_HOT` | 60 sn | 0 |
| İade hot akışı | `TRENDYOL_RETURN_SYNC` / `RETURNS` | 3 dk | 2 |
| Açık sipariş lifecycle | `TRENDYOL_ORDER_STATUS_SYNC` | 3 dk | 0 |
| Açık iade lifecycle | `TRENDYOL_RETURN_STATUS_SYNC` | 3 dk | 2 |
| Sipariş recovery | `TRENDYOL_ORDER_RECOVERY_SYNC` | 15 dk | 6 |
| Günlük/kısa/orta uzlaştırma | ilgili reconciliation job’ı | 15 dk–24 sa | 4 |

İşletim ekranındaki sync policy kayıtları tüm bu resource türlerinin aralığını, overlap’ini, son denemesini, son başarısını, istek/kayıt/hata/retry sayaçlarını ve `HEALTHY`, `DELAYED`, `DEGRADED`, `OFFLINE` durumunu gösterir.

## Siparişler

- Trendyol Stream/last-modified okuması kullanılır. Hot akış 10 dakikalık güvenlik örtüşmesiyle ilerler.
- Stream’in 14 günlük erişim sınırı aşılırsa recovery ve reconciliation pencereleri en fazla 14 günlük parçalara bölünür; recovery hot akıştan bağımsız ilerler.
- Cursor sayfa ilerlemesini saklayabilir; `LastModifiedWatermark` ve `LastSuccessAt` yalnız bütün pencere, storefront ve sayfalar başarıyla tamamlanınca ilerler.
- Dış sipariş, satır, package ve event kimlikleri unique kayıtlarla idempotent tutulur. Aynı package event’i status history veya allocation’ı tekrar üretmez.
- Satır bazlı iptal allocation’da tutulur; bir satırın kısmi iptali tüm siparişi iptal etmez. Açık package’ler doğrudan okumayla 3 dakikada bir tekrar kontrol edilir.
- Sipariş satırı SKU/barcode üzerinden yerel varyanta çözülür. Rezervasyon miktarı `ordered - cancelled` olarak hesaplanır.

## İadeler

- `getClaims` akışı 15 dakikalık örtüşmeyle 3 dakikada bir çalışır. İlk tarama erişilebilir üç aylık geçmişi storefront ve sayfa bazında tarar.
- `Completed` ve `Cancelled` olmayan claim’ler ayrı 3 dakikalık lifecycle işinde ClaimId ile tekrar okunur.
- Kısa, orta ve günlük iade reconciliation işlerinde tarih aralığı sınırlıdır; hot akışın cursor’ı kullanılmaz.
- Claim veya bağlı order bulunamazsa yerel kayıt silinmez, final durum uydurulmaz; operasyonel issue açılır ve sonraki tarama için korunur.

## Stok kaynağı, rezervasyon ve outbox

Panel yerel stok projeksiyonunun otoritesidir. Sipariş importu sırasında rezervasyon, stok ledger kaydı, `Available` hesabı ve yüksek öncelikli stok projection outbox job’ı aynı EF transaction/save birimi içinde oluşturulur. Bu nedenle worker save sonrasında çökerse kalıcı outbox işi kaybolmaz.

Projection payload’ı bağlantı, varyant ve projection version ile dedupe edilir. Dış fiyat/stok yazması tamamlanmadan ilgili projection version başarılı sayılmaz; kısa/orta/günlük stok reconciliation geride kalan projection’ları tekrar kuyruğa alır.

## Ürün importu, katalog ve yayınlama

- Ürün importu yalnız kullanıcı aksiyonuyla başlar; approved-products verisi sayfalı ve watermark örtüşmeli okunur.
- Import hash ve son import edilen ürün sürümü tutulur. Yerelde değişmiş alanlar üzerine uzak katalog snapshot’ı yazılmaz; link `LOCAL_CHANGES_PENDING` durumuna geçer ve dirty alanlar korunur.
- Kategori, marka, kategori özellikleri, özellik değerleri, seçenekler ve görsel adresleri yerel snapshot/link modellerine aktarılır. Katalog referansı boş veya sözleşme dışı gelirse mevcut snapshot korunur.
- Yerel ürün kaydetme, Trendyol yazma işinden ayrıdır. Ürün create/update/archive ve fiyat-stok işlemleri kalıcı job, effect idempotency, retry ve read-back ile yürür.
- Batch ürün işlemleri aşamalıdır: gönderim, adaptive status polling, satır doğrulama, onay/read-back ve gerektiğinde manuel inceleme.
- Ürün güncelleme polling varsayılanı: ilk 10 dakikada 2 dk, 10–30 dakikada 5 dk, 30–60 dakikada 15 dk, sonra 30 dk. Dört saatlik toplam pencere aşılırsa iş otomatik sonsuz retry yapmaz.

## Retry, rate limit ve dış çağrı güvenliği

Retry delay’leri kaynak sözleşmesindeki birimle saniyedir: `2, 5, 15, 30, 60` saniye ve yüzde 0–20 deterministik jitter. Varsayılan maksimum deneme 6’dır. Sağlayıcının `Retry-After` başlığı güvenli sınırlar içinde bu değerin önüne geçer.

Trendyol HTTP client’ı bağlantı başına sınırlı eşzamanlılık, kayan pencere rate limit ve circuit breaker kullanır. Varsayılanlar 8 eşzamanlı istek, tüm çağrılar için 10 saniyede 50 isteklik yetkilendirme koruması ve sipariş okuma çağrıları için satıcı bazında dakikada 30 istek korumasıdır. 408/429 ve 5xx yanıtları devre kesici hatası sayılır; 5 ardışık hata sonrası 30 saniye devre açılır. İstek timeout’u `Trendyol:Timeout` ayarından okunur ve varsayılanı 30 saniyedir.

## Gözlemlenebilirlik ve gerçek zamanlı panel

Her senkronizasyon cursor’ı son deneme/başarı zamanı, watermark, hata, ardışık hata sayısı, süre ve son tur sayaçlarını tutar: istek, alınan, değişen, eklenen, güncellenen, atlanan, başarısız, retry ve rate-limit.

Sağlık sınıflandırması eşiklerle yapılandırılabilir:

- `MarketplaceSync:Health:DelayedAfterSeconds` — varsayılan 120 sn
- `MarketplaceSync:Health:DegradedAfterSeconds` — varsayılan 600 sn
- `MarketplaceSync:Health:OfflineAfterSeconds` — varsayılan 1800 sn

API sync-policy yanıtında bu durum ve sayaçlar bulunur. SignalR `/hubs/operations` bağlantısı tamamlanan job sonrası tenant grubuna resource değişikliği gönderir; web arayüzü sipariş, iade, ürün, stok ve işlem takibi sorgularını yeniden doğrular.

## Doğrulama senaryoları

`tests/MarketplaceHub.Application.Tests/MandatorySynchronizationScenariosTests.cs` aşağıdaki zorunlu senaryoları kapsar: duplicate package, out-of-order status, kısmi iptal, dört saat worker kesintisi, 45 günlük recovery chunk’ı, aynı sipariş numarası, güvenlik örtüşmesi, tekrar webhook, çift worker lease, Trendyol dışı provider sınırı, ürün importunun salt-okunur olması, crash sonrası stok outbox anahtarı, local değişikliğin korunması, adaptive product polling ve saniye tabanlı retry dizisi.

Bu testler policy/contract seviyesindedir. Gerçek Trendyol sandbox erişimi, provider rate limit davranışı ve dış sistemin idempotency uygulaması ayrıca canlı Stage smoke testiyle doğrulanır.

## Operasyon komutları

```powershell
dotnet build MarketplaceHub.sln --no-restore
dotnet test MarketplaceHub.sln --no-restore
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/MarketplaceHub.Infrastructure --startup-project src/MarketplaceHub.Api
npm.cmd run build --prefix src/MarketplaceHub.Web
```

Sunucu dağıtımında hedef `/home/ubuntu/RavenciaEntegrasyon` repository’si güncel commit’i pull eder, migration’ları uygular, container’ları yeniden başlatır ve `/health/ready` ile doğrulanır.

## Resmî sözleşme referansları

- Stream: <https://developers.trendyol.com/docs/sipari%C5%9F-paketlerini-ak%C4%B1%C5%9F-ile-%C3%A7ekme>
- Webhook modeli: <https://developers.trendyol.com/docs/webhook-model>
- İade talepleri: <https://developers.trendyol.com/docs/i%CC%87adesi-olu%C5%9Fturulan-sipari%C5%9Fleri-çekme-getclaims>
- Onaylı ürünler: <https://developers.trendyol.com/tr/v2.0/docs/product-filtering-approved-products-v2>
