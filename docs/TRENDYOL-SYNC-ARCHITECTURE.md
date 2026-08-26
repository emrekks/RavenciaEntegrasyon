# Trendyol Senkronizasyon Mimarisi

Bu belge, geniş kapsamlı entegrasyon metninin mevcut Ravencia kod tabanına uyarlanmış ve uygulanabilir sürümüdür. Hedef, çalışan Trendyol entegrasyonunu yeniden yazmadan veri kaybı, tekrar işleme ve uzun süre açık kalan kayıt risklerini azaltmaktır.

## Kesin iş kuralları

- Sipariş, paket durumu ve iade akışı Trendyol'dan panele otomatik gelir.
- Sipariş aynı dış kimlikle tekrar geldiğinde yeni sipariş oluşturulmaz.
- Paket ve satır değişiklikleri sipariş aggregate'i içinde işlenir; kısmi iptal bütün siparişi iptal etmez.
- Panel stok için ana kaynaktır. Dış stok yazmaları kalıcı job ve idempotency anahtarı üzerinden yapılır.
- Trendyol ürün importu kullanıcı aksiyonuyla başlar; periyodik katalog importu yapılmaz.
- Yerel ürün kaydı ile pazaryeri yayını ayrı işlemlerdir. Yerel kaydetme otomatik Trendyol yazması üretmez.
- Şu anda yalnız Trendyol ve Trendyol E-Faturam aktiftir. Gelecekteki sağlayıcılar için sahte adapter veya boş worker eklenmez.

## Mevcut mimariyle ilgili kararlar

Projede PostgreSQL tabanlı kalıcı `IntegrationJob`, lease/heartbeat, retry, dedup anahtarı, dead durum, `SyncCursor`, order/package/line modeli ve return claim modeli zaten vardır. Bu nedenle her senkronizasyon türü için ayrı process ve ayrı in-memory queue oluşturulmayacaktır.

Sorumluluk ayrımı fiziksel worker sayısıyla değil, kalıcı job türleri ve öncelikleriyle sağlanır:

| Akış | Job | Öncelik | Varsayılan |
| --- | --- | ---: | ---: |
| Sipariş hot sync | `TRENDYOL_ORDER_SYNC` | 0 | 30 saniye |
| Yeni/değişen iade | `TRENDYOL_RETURN_SYNC` | 2 | 60 saniye |
| Açık iade lifecycle | `TRENDYOL_RETURN_STATUS_SYNC` | 2 | 180 saniye |
| Referans/katalog işi | ilgili job | 5 | manuel/düşük öncelik |

Bu yaklaşım process restart sırasında iş kaybetmez, birden fazla worker instance'ında `FOR UPDATE SKIP LOCKED` ve lease fencing ile aynı işin eşzamanlı sahiplenilmesini önler.

## Sipariş senkronizasyonu

- Stream/last-modified akışı kullanılır.
- Varsayılan safety window 10 dakikadır ve bağlantı policy'sinden değiştirilebilir.
- İlk kurulum veya uzun kesinti, Trendyol erişim sınırı içinde 14 günlük pencerelere bölünür.
- Cursor her sayfada kalıcılaştırılabilir; ancak `LastSuccessAt` ve committed watermark yalnız tüm tur başarıyla tamamlandığında ilerletilir.
- Sipariş, satır ve paket dış kimliklerinde veritabanı unique constraint'leri korunur.
- Package event kimliği tekrar geldiğinde status history ve allocation tekrar oluşturulmaz.

## İade senkronizasyonu

- Yeni/değişen claim taraması varsayılan 15 dakikalık safety window ile 60 saniyede bir çalışır.
- İlk çalışma erişilebilir üç aylık dönemi tarar.
- `Completed` ve `Cancelled` olmayan claim'ler ayrı lifecycle job'ında ClaimId ile doğrudan tekrar okunur.
- Lifecycle işi en eski güncellenen açık claim'leri öne alır ve her turda sınırlı batch işler; API kotasını kontrolsüz tüketmez.
- Claim uzaktan geçici olarak bulunamazsa yerel kayıt silinmez veya final yapılmaz; operasyonel issue açılır.

## Ürün ve stok

- Ürün importu otomatik scheduler'dan çıkarılmıştır; mevcut “Ürünleri panele çek” aksiyonu kalıcı product job üretir.
- Ürün local save ile Trendyol update birbirinden ayrı kalır.
- Mevcut price/inventory dış yazmaları persistent job, retry ve external-effect idempotency korumalarını kullanmaya devam eder.

## Yapılandırma

Varsayılanlar `MarketplaceHub.Worker/appsettings.json` altındadır ve environment variable ile override edilebilir:

```text
Worker__SchedulerScanSeconds
Worker__HotPriorityCeiling
MarketplaceSync__Orders__IntervalSeconds
MarketplaceSync__Orders__SafetyWindowSeconds
MarketplaceSync__Returns__IntervalSeconds
MarketplaceSync__Returns__SafetyWindowSeconds
MarketplaceSync__ReturnLifecycle__IntervalSeconds
MarketplaceSync__ReturnLifecycle__BatchSize
```

Sipariş ve iade hot-sync aralıkları bağlantı bazında panelden de değiştirilebilir. En düşük desteklenen aralık 30 saniyedir.

## Uygulama sonrası kalan kontrollü geliştirmeler

Aşağıdaki maddeler ayrı migration ve iş kuralı doğrulaması gerektirir; bu değişiklik paketinde varsayım yapılarak eklenmemiştir:

1. Sipariş transaction'ı ile stok rezervasyonu ve outbox event'inin tek transaction içinde birleştirilmesi.
2. Sipariş/iade/stock değişiklikleri için SignalR event sözleşmeleri.
3. Provider bazlı rate limiter ve circuit-breaker state'inin kalıcılaştırılması.
4. Sync cursor üzerinde attempt/error/duration sayaçlarının migration ile genişletilmesi.
5. Hot sync'ten tamamen bağımsız düşük öncelikli deep reconciliation queue'su.
6. Gerçek Trendyol fixture'larıyla contract ve integration test projelerinin solution'a eklenmesi.

Bu işler yapılırken mevcut dış yazma güvenlik kapıları, Stage/Production ayrımı, lease fencing ve idempotency anahtarları korunmalıdır.

## Resmî sözleşme referansları

- Stream: <https://developers.trendyol.com/docs/sipari%C5%9F-paketlerini-ak%C4%B1%C5%9F-ile-%C3%A7ekme>
- Webhook modeli: <https://developers.trendyol.com/docs/webhook-model>
- İade talepleri: <https://developers.trendyol.com/docs/i%CC%87adesi-olu%C5%9Fturulan-sipari%C5%9Fleri-%C3%A7ekme-getclaims>
- Onaylı ürünler: <https://developers.trendyol.com/tr/v2.0/docs/product-filtering-approved-products-v2>
