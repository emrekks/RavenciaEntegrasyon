# Hata tespiti — 20 Ekim 2026 gece (birleştirme sonrası)

Kapsam: 20 Ekim'de main'e giren ~70 commit (`6c13690d..fc283126`; derin denetimin sekiz kolu, fatura teslim
yarışı düzeltmesi, desi içe aktarımı) ve canlı sunucu. Altı alan ayrı ayrı salt okunur incelendi; yüksek ve
canlıyla ilgili bulgular ayrıca kodda ve canlı veritabanında (salt okunur) doğrulandı. Hiçbir şey düzeltilmedi.

Durum etiketleri: **canlıda görüldü** · **kodda doğrulandı** · **olası** (koşula veya dış API davranışına bağlı).

## 0. Acil — canlıda süren sorun

**C1. PostgreSQL bellek yetmediği için öldürülüyor; veritabanı her seferinde yeniden başlıyor** — canlıda görüldü
- Bugün 4 kez: 21:47, 22:05, 22:21, 22:36 (Türkiye saati). Her seferinde tüm bağlantılar kopuyor, birkaç saniye
  "recovery mode"; açık ekranlar 500 alıyor (22:05–22:35 arası 34 adet, hepsi `/orders/summary`).
- Konteyner sınırı 1 GiB (`deploy/compose/compose.yaml`, shared_buffers 256 MB, work_mem 16 MB, paralel işçi 2);
  kontrol anında kullanım %104. Sunucuda 5,8 GB boş bellek var.
- Tetikleyen: yeni sipariş sekme sayacı (`MarketplaceSalesService.OrderSummaryAsync`, commit 47753bab) 15 sekmenin
  liste sorgusunu UNION ALL ile tek seferde sayıyor; 4,5–10 sn sürüyor. Panel bunu her sayfada menü rozeti için
  15 sn'de bir istiyor (`App.tsx:61`), sipariş sayfası ayrıca filtreli halini istiyor; üst üste binince sınır aşılıyor.
  22:21'de öldürülen süreç bu sorgu. 22:05 ve 22:36 deploy yedeği (pg_dump) ile aynı ana denk geliyor; 21:47 bugünkü
  birleştirmeden önce — yani 1 GiB sınır tek başına da dar.
- Öneri: menü rozeti için eski tek geçişli sayım (filtre yokken), filtreli sayımı yalnız sipariş sayfasında ve kısa
  önbellekle; PostgreSQL sınırını 2 GiB'a çıkarmak.

## 1. Yüksek

**Y1. Bir hesabın girişi dışarıdan süresiz engellenebiliyor** — kodda doğrulandı
- `AuthEndpoints.cs:52` hesap başına dakikada 20 deneme payını ağ engeli ve parola kontrolünden **önce** harcıyor
  (`SignInThrottle.TryTakeAccountAttempt`). E-postayı bilen biri iki ağdan dakikada 20 istekle sahibi her yerden 429'da
  tutar. Var olan hesapta 429, olmayanda 401 döndüğü için hesap varlığı da sızıyor.
- Ek: hesap çapındaki kilit (30 hata / 15 dk, `DependencyInjection.cs:42`) farklı IPv6 /64'lerden doldurulabiliyor;
  açık oturumdaki yeniden doğrulamayı da kapatıyor.

**Y2. Trendyol fatura kontrolü sipariş okumasının yazdığı yeni fatura bilgisini eskisiyle ezebiliyor** — mekanizma kodda doğrulandı, zamanlama olası
- `OrderInvoiceReconciliation` artık kendi kilit grubunda ("order-invoices", `MarketplaceSyncExecutionLock.cs:120`) yani
  sipariş okumasıyla aynı anda çalışıyor; paket fatura kolonlarını sürüm kontrolsüz ExecuteUpdate ile toplu yazıyor
  (`MarketplaceJobProcessor.cs:4172-4220`). Paketler 50'lik HTTP partileri bitene kadar bellekte bekletildiği için arada
  gelen daha yeni gözlem eziliyor; aynı paketi iki iş işlerse sayfa CONCURRENT_UPDATE ile atılıyor. Testi yok.

## 2. Orta

| # | Bulgu | Yer | Durum |
|---|---|---|---|
| O1 | Pano net cirosu iptal ve iade edilmiş paketlerin tutarını da sayıyor: tutar sipariş düzeyinde (`NetAmount`), iptal/iade yalnız sipariş durumuna bakılarak düşülüyor; kısmi iade hiç düşülmüyor | `DashboardReadService.cs:168-185` | kodda doğrulandı |
| O2 | Bağlantı kimlik bilgisi güncellenince (DRAFT) veya kapatılınca o mağazanın geçmiş cirosu panodan kayboluyor — sorgu bağlantının *şu anki* durumuna bakıyor | `DashboardReadService.cs:171` | kodda doğrulandı |
| O3 | Türkçe arama: "ışık" araması "Işıklı Ayna" başlıklı siparişi bulmuyor (veritabanı `en_US`) — ürün başlığı ve iade nedeni aramasında | `MarketplaceSalesService.cs:3097` | canlıda denendi |
| O4 | Trendyol'da `vatRate` gelmeyen satır artık atılmıyor; tek satırlı pakette %0 KDV ile kalıyor ve fatura kontrolü %0'ı geçiriyor | `TrendyolJsonMapper.cs:45-145`, `InvoicingBillingService.cs:1363` | kodda doğrulandı; canlıda henüz Trendyol'da %0 satır yok |
| O5 | Elle yüklenen faturanın numarası GİB biçiminde değilse (`2026/0045` gibi) Trendyol linki hiç gönderilmiyor; yurt içinde numara isteğe bağlı olduğu halde | `TrendyolHttpClient.cs:809-828` | kodda doğrulandı |
| O6 | Boşta oturum kapanması (W1) fiilen çalışmıyor: zamanlı sorguların çoğu `X-Background-Poll` taşımıyor, açık kalan Sorular/İadeler/Ürünler sekmesi oturumu canlı tutuyor | `data-freshness.tsx:31`, `ReturnPages.tsx:348`, `ProductsPage.tsx:467`, `QuestionsPage.tsx:186-202` vd. | kodda doğrulandı |
| O7 | Medya ve küçük görsel istekleri ağ başına genel tavanı (dk'da 1800) tüketiyor: toplu yayında pazaryerinin görsel çekimi veya kalabalık bir ofis 429 alabilir | `RateLimitPolicies.cs:41-43` | olası |
| O8 | Webhook adres başına sınır 600/dk'ya indi (bağlantı sınırı 1200'ün altında); kampanya yoğunluğunda gerçek teslimatlar 429 alabilir | `WebhookRateLimits.cs:16-19` | olası |
| O9 | HB açık paket listesi `offset` yerine `Offset` gönderiyor (commit "SIT'te doğrula" diyor); büyük/küçük harf duyarlıysa ilk sayfa tekrar tekrar okunur, sonrakiler gelmez | `HepsiburadaHttpClient.cs:773` | olası |
| O10 | Etki doğrulama işi sonsuza dek dönebilir: okumalar arası boşluk hep 5 dk'yı aşarsa süre sayılmıyor, `Continue` deneme hakkı tüketmiyor | `SynchronizationPolicies.cs:772-790` | olası |
| O11 | Lease sorgusunda kiracı adaleti yalnız ilk 50 aday içinde; bir kiracının 500 eski işi diğerlerini bekletir | `JobLeaseService.cs:65,80` | kodda doğrulandı (tek kiracıda etkisiz) |
| O12 | Web, Trendyol dışı paketlerde artık reddedilen kargo değişikliğini sunuyor (Pazarama tekli; HB/N11/Pazarama toplu); karışık toplu seçimde yarıda kalıp "güncellenemedi" diyor | `OrdersPage.tsx:509, ~991` | kodda doğrulandı |
| O13 | Shopify'da elle verilen durum yalnız Kargoda/Teslim/İptal sekmelerinde dikkate alınıyor; elle "Kargoda" yapılan sipariş hem Yeni hem Kargoda'da sayılıyor | `MarketplaceSalesService.ApplyOrderFilters` | kodda doğrulandı |
| O14 | Panelden HB paketi açılan (UNPACK) sipariş Yeni sekmesinden ve sayacından düşüyor | `MarketplaceJobProcessor.cs ~9900`, `HepsiburadaOrderVisibility` | kodda doğrulandı |
| O15 | Ürün düzenleyicide tek ürünü kaydetmek tüm model-kodu ailesini "yerel değişiklik var" işaretliyor, arşivli kardeşi aktife çevirebiliyor; sonraki içe aktarımlar o kardeşleri güncellemiyor | `CatalogService.cs:1018, 1135-1143, 1232-1243` | kodda doğrulandı |
| O16 | CSV'de kategori adı (`Tayt`) yol anahtarıyla (`GIYIM-ALT-GIYIM-TAYT`) karşılaştırılıyor; pazaryerinden gelen kategoriler eşleşmiyor, ürün kategorisiz ve uyarısız oluşuyor | `ImportJobProcessor.cs:415-421` | kodda doğrulandı |
| O17 | CSV okuyucu: alan ortasındaki tırnak artık satır sonunu da yutuyor; `24" Monitör` gibi bir satır sonraki satırla birleşip sütunlar kayabiliyor | `ImportFileReader.cs:110` | olası |
| O18 | NOT_DELIVERED sayılan soru cevabı pazaryerinde geç görünürse panel yeniden gönderime izin veriyor; müşteri iki cevap alabilir | `MarketplaceQuestionService.cs:231-266, 505-525` | olası |
| O19 | Canlı duyuru özet yeniden kurulmadan gidiyor; yan menüdeki bekleyen iade rozeti ve geciken sayısı bir sonraki olaya kadar eski kalıyor | `OperationsRealtimeBroadcaster.cs`, `DashboardSnapshotRefresher.cs` | kodda doğrulandı |

## 3. Düşük

- Desi (benim değişikliğim): Pazarama/N11/HB'de desi metin olarak "2,5" gelirse 25 okunuyor; HB desi'yi iç içe özellik
  listesinde verdiği için HB'den pratikte hiç okunmuyor. Trendyol (sayı olarak geliyor) doğru — canlıdaki tek kaynak o.
- `CloseDeliveryJobsSettledElsewhere` en eski 200 işe bakıyor; 200'den fazla çözülmemiş iş birikirse yenileri kapanmaz,
  kapanan işler için panel olayı yazılmıyor (`InvoicingJobProcessor.cs:1626`).
- Kurtarma taraması fatura önekiyle atladığı için aynı faturanın başka paketi gecikebiliyor (`InvoicingJobProcessor.cs:1776`).
- Fatura iptalinde bekleyen teslim işleri ayrı bir kayıtla iptal ediliyor (atomik değil, `InvoicingBillingService.cs:1474`).
- Sürüm çakışması ve tekrarlı ekleme aynı sayacı paylaşıyor; iki çakışmadan sonraki ilk tekrarlı ekleme doğrudan incelemeye gidiyor (`JobConflictPolicy`).
- Kuyruk metrikleri `Worker:HotPriorityCeiling` ayarını okumuyor (`QueueHealthService.cs:20`).
- Trendyol iade okuması kalıcı 4xx veren kayıtlı sayfada takılı kalabilir; eskiden baştan başlıyordu (`MarketplaceJobProcessor.cs:9140`).
- Doğru parola ikinci adımdan önce tüm sayaçları sıfırlıyor; parolayı bilen biri TOTP denemelerini sınırsız yayabilir (`AuthEndpoints.cs:71`).
- StepUpPrompt güvenlik durumu okunamazsa kodsuz gönderip bir MFA hakkı harcıyor (`StepUpPrompt.tsx:44`).
- Idempotency anahtarı biçimi değişti (`{kullanıcı}:{anahtar}`); deploy anında belirsiz kalmış istekler tekrar çalışabilir (dar pencere).
- HB etiket ucunda her 400 "ortak barkod yok" diye gösteriliyor (`HepsiburadaPackageOperations.cs:281`).
- HB fatura gövdesi `application/pdf` → `pdf` ve ek alanlar; kodun kendisi "SIT'te doğrulanmalı" diyor (`HepsiburadaInvoiceDeliveryPolicy.cs:70`).
- Trendyol devresi yarı açıkken reddedilen istekler de kota jetonu harcıyor (`TrendyolResilienceHandler.cs:45`).
- TurkeyTime `"… 14:30:00 +03:00"` (boşluklu) biçimini artık tanımıyor; böyle bir değer 3 saat kayar (`TurkeyTime.cs:40`).
- İşlem Takibi'nde `?sayfa=N` bağlantısı istenen sayfayı açmıyor; yayın işi sorgusu MANUAL_REVIEW/BLOCKED'da 2,5 sn'de bir sonsuza dek sürüyor.
- İade stok kararında her gönderim yeni anahtar alıyor; yanıt kaybolup tekrar basılırsa kısmi adet iki kez stoğa eklenebilir.
- Sipariş sekmelerinde UNVERIFIED ve ödeme bekleyen siparişlerin kendi sekmesi/sayacı yok; "Tümü" toplamı sekmelerle tutmuyor.
- Dosya içe aktarımı pasif markayı yeni ürünlere atıyor; UTF-16 CSV artık tanınmıyor; seçenek imzası Türkçe harfli seçeneklerde bir kez yeniden yazılıyor.

## 4. Canlıda ayrıca görülenler

- `/api/v1/dashboard/revenue-series` 30 dk'da 180 kez 404 — kaldırılan eski uç; güncel kod çağırmıyor, açık kalmış eski
  sürüm sekmesinden geliyor olmalı (sayfa yenilenince biter).
- Bir Pazarama bağlantısı saatte bir yeniden test ediliyor ve "kimlik bilgisi kabul edilmedi" alıyor (6 saatte 6 kez).
- Yeni sürümle başarısız/takılı iş ve kuyruk birikmesi yok; fatura teslim yarışı düzeltmesi çalışıyor.

## 5. Sorunsuz bulunanlar (özet)

Migration'lar ve boş kolon davranışı, step-up döngüsü (yok), webhook token kapısı, HubOriginFilter (Caddy arkasında doğru
köken), BoundSecret, bildirim geçmişinin kiracı/kullanıcı ayrımı, İstanbul gün gruplaması, birleştirme çakışma
çözümleri, Shopify If-Match çağrıları, CSV içe aktarma kilidi, stok yazmada beklenen stok kontrolü, eski katalog
anahtarlarıyla arama, desi doldurmanın doğru varyanta yazması. Tüm PostgreSQL testleri (2.847) birleştirme sonrası
gerçek PostgreSQL'de geçti.
