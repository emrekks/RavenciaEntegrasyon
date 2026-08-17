# F2 Kanıt Günlüğü

## 2026-08-17 - Özellik kitaplığı, varyant araçları ve zengin metin

- Kategori bağımsız yerel attribute library görünümü ve mevcut kategori-scoped attribute eşleşmelerinin `scopeExternalId=*` salt-okunur özeti eklendi.
- Varyant araçları 5. bölümde birlikte yer alır; UI'daki 100 varyant göstergesi kaldırıldı. Gerçek servis sınırı 1000, SKU/barkod, envanter ve yayın korumaları değişmedi.
- Açıklama editorü genişletildi; web typecheck ve hedefli `CatalogWorkspacePages` + `TrendyolOperationsPages` testleri `10/10 PASS`. Browser/Stage görsel kabulü `NOT_RUN`.

## 2026-08-17 - Attribute checkbox ve varyant aksiyonu yerleşimi

- Varyant özelliği seçicileri sabit boyutlu, erişilebilir native checkbox olarak normalize edildi; geniş input stilleri checkbox'a sızmaz.
- `Ürünleri ekle` aksiyonu 5 numaralı özellik bölümünün üstünde konumlandırıldı; oluşan satır işlemleri 6 numaralı bölümde kaldı.
- Browser/Stage görsel kabulü `NOT_RUN`; takip eden hedefli web typecheck sonucu bu kayıtla birlikte güncellenecek.

## 2026-08-17 - Düzenleme varyant append kalıcılığı

- `UpdateProductCommand.VariantsToCreate` yalnız yeni varyantları ekler; mevcut varyantların silinmesi veya dış liste/enventory bağlantılarının kopması söz konusu değildir.
- Servis SKU/barkod tenant tekilliği, kategori zorunlu özellikleri, 1000 varyant üst sınırı ve her yeni varyant için MAIN envanter kaydını doğrular.
- Hedefli doğrulama: `dotnet build MarketplaceHub.sln --no-restore` `PASS` (0 warning/error); `npm.cmd run typecheck` `PASS`; `CatalogWorkspacePages.test.tsx` `3/3 PASS`, yeni düzenleme PATCH gövdesi senaryosu dahil. Tarayıcı/Stage kabulü `NOT_RUN`.
- Takip sözleşme doğrulaması: tüm web Vitest kümesi `27/27 PASS` ve Vite production build `PASS`; mevcut varyant eklenmediğinde PATCH gövdesinin `variantsToCreate: []` ile açık kalması doğrulandı.

## 2026-08-08 production v9 hotfix

- Dashboard ve yeni ürün çalışma alanındaki sayfalı API koleksiyonları eksik/null `items` alanında boş listeye güvenli düşecek şekilde düzeltildi.
- Canlı yenilemede görülen `undefined.filter` hatası yeniden release doğrulaması bekliyor; katalog veya runtime verisi değiştirilmedi.

Doğrulama tarihi: 2026-07-31. Ortam: Windows 10 geliştirme makinesi, .NET 10 ve repository altında geçici/izole PostgreSQL 18.4 cluster'ı. Geçici cluster test sonunda düzgün kapatılıp silindi.

| Kanıt | Sonuç | Ölçüm |
| --- | --- | --- |
| `F2-EV-001` format ve warnings-as-errors build | PASS | `dotnet format --verify-no-changes`; solution build: 0 warning, 0 error |
| `F2-EV-002` tarihsel migration zinciri | PASS | F1 korunarak `20260731173343_F2CatalogInventoryCore`; 43 F2 tablo oluşturma işlemi; F1→F2 SQL üretimi 40.425 byte |
| `F2-EV-003` fresh PostgreSQL migration | PASS | PostgreSQL 18.4 üzerinde `iam`, `ops`, `integration`, `catalog`, `inventory`; bootstrap/user/tenant seed yok |
| `F2-EV-004` catalog fiziksel guard'ları | PASS | Tenant composite FK/UQ; SKU ve barkod tenant tekilliği; typed-value ve leaf-category uygulaması |
| `F2-EV-005` import state/matching/apply | PASS | CSV → preview → manual CREATE → apply; ikinci aynı import `UNIQUE_BARCODE` → LINK; Product/Variant/MAIN item sayıları 1 kaldı |
| `F2-EV-006` dosya güvenliği | PASS | Strict UTF-8/malformed CSV; XLSX macro ve formula reddi; CSV formula-prefix neutralization testleri |
| `F2-EV-007` büyük import hedefi | PASS | 10.000 CSV satırı streaming enumerable ile işlendi |
| `F2-EV-008` stok değişmezleri | PASS | available/publishable domain testleri; PostgreSQL projection check; aynı idempotency key iki çağrıda tek ledger satırı |
| `F2-EV-009` fiyat geçmişi | PASS | Decimal/currency domain testi; offer update price-version `1→2`; tek append-only history satırı |
| `F2-EV-010` capability fail-closed | PASS | Publication/mapping eksikliği ve stock sync `CAPABILITY_UNKNOWN`; IntegrationJob sayısı 0; dış HTTP adapter'ı yok |
| `F2-EV-011` sayfalama/hacim | PASS | 1.000 Product, 50 kayıtlık 20 imzalı cursor sayfası; 1.000 benzersiz kayıt, tekrar/kayıp yok; ölçülen p95 `136,85 ms` (`< 2.000 ms`) |
| `F2-EV-012` .NET test seti | PASS | 32 test: Domain 7, Application 14, Persistence 7, API 1, Adapter 1, repository guard 2; 0 failed |
| `F2-EV-013` web doğrulama | PASS | TypeScript strict + Vite production build; 1 Vitest; 2 Playwright F2/navigation testi |
| `F2-EV-014` faz yüzeyi | PASS | Yalnız onaylı F2 API/UI yolları; F3+ order/shipment/return/invoice/integration/webhook route/menu yok |
| `F2-EV-015` upload/idempotency/concurrency | PASS | 10 MiB genel sınır; MIME/magic; If-Match güçlü ETag; POST idempotency kayıt/tekrar engeli; ProblemDetails kodu |

## Yerel ve production ayrımı

F2 uygulama sonucu `READY_LOCAL`dır. Bu makinede Docker CLI/engine güncel oturumda bulunmadığından F2 image/Compose smoke tekrarlanmadı; F2 migration ve tüm persistence testleri doğrudan PostgreSQL 18.4 üzerinde geçti. F1'in mevcut container/backup kanıtı değiştirilmedi. Hedef Ubuntu Server, registry-pushed immutable digest, production PFX, off-host backup ve ölçülmüş RTO kanıtları sunucu kiralandığında tamamlanacaktır; production sonucu `BLOCKED_EXTERNAL`dır.

Gerçek platform test hesapları ve resmî capability kanıtları gelene kadar capability `UNKNOWN`, `FeatureFlags__ExternalWrites=false`; publication, stock-sync ve price-sync dış etki üretmez. F3 ve sonrası açılmamıştır.

## 2026-08-06 — v9 katalog ve varyant çalışma alanı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Kategori özellik gereksinimi read API | CODED_STATIC_VERIFIED | Kategoriye bağlı özellik başlıkları ve değerleri ürün formuna tek modelle döner. |
| Varyant seviyeli özellik kalıcılığı | CODED_STATIC_VERIFIED | Normal özellikler ürün, varyant özellikleri ilgili varyant kapsamında `ProductAttributeAssignment` olarak saklanır. |
| Varyant matrisi ve doğrulama | TYPESCRIPT_STATIC_PASS | En fazla 100 UI kombinasyonu; yinelenen kombinasyon, SKU ve barkod engeli; toplu stok/fiyat uygulama. |
| Güvenli yayın hazırlığı | PASS_STAGE_CREATE / APPROVAL_PENDING | ACTIVE Trendyol bağlantısında teklifler, listing profile ve publication job zinciri Stage create batch kabulünden geçti. Stage manuel akış bağlantı/auth/input/idempotency ile çalışır; Production master + connection write switch zinciri korunur. Terminal approval readback `PENDING`. |
| Dinamik suite | BLOCKED_ENVIRONMENT | Exact Node/npm registry ve .NET SDK yok; Vitest/Vite ve `dotnet test` çalıştırılamadı. |

## 2026-08-09 — v10.20 ürün formu ve desi modeli

| Kanıt | Durum | Not |
| --- | --- | --- |
| Temel ürün alanları | PASS_LOCAL_WEB | Kategori arama kutusu kaldırıldı; ürün/marka/açıklama/kategori/model/SKU/Barkod alanları ortak grid içinde hizalandı. |
| Doğrudan ve hesaplanan desi | PASS_LOCAL_WEB | Kapalı durumda Desi varsayılan `1`; açık durumda ağırlık ve ölçüler gösterilip `en × boy × yükseklik / 3000` sonucu gönderilir. |
| Kalıcılık ve migration | CODED_BUILD_PASS | `ProductVariant.Desi` nullable `numeric(19,4)` olarak eklendi; migration yalnız sütun ekler, mevcut satırları dönüştürmez. |
| Web ve .NET doğrulama | PASS_LOCAL_PARTIAL | 19/19 Vitest, TypeScript, Vite build, .NET build ve Docker gerektirmeyen 142 test geçti; PostgreSQL Testcontainers/full-stack `BLOCKED_ENVIRONMENT`. |
# 2026-08-11 - Ürün oluşturma çalışma alanı hızlı kabulü

- HTML açıklama editörü güvenli iframe ön izlemesiyle eklendi; açıklama temel bilgiler kartının sonuna taşındı.
- JPEG/PNG dosyaları ürün kaydından sonra mevcut özel medya endpointine yüklenir; varyant kombinasyon mantığı korunur.
- Web production build ve typecheck: `PASS`; web testleri `19/19 PASS`; production bağımlılık taraması `0 vulnerability`. Ayrıntılı tarayıcı/Stage kabulü: `NOT_RUN`.

## 2026-08-17 - Ürün düzenleme çalışma alanı eşitliği

| Kanıt | Durum | Not |
| --- | --- | --- |
| Ürün seviye özellik read modeli | CODED_BUILD_PASS | `ProductView`, yalnız ürün seviyesindeki typed attribute atamalarını döner; varyant atamaları kapsam dışı bırakılır. |
| Düzenleme alanları | CODED_LOCAL | `/products/:id`, ayrı bir JSX düzeni yerine doğrudan ürün oluşturma bileşeninin düzenleme modunu kullanır; temel bilgiler, kategori/marka, özellikler, varyant stok/fiyat, ölçü/desi, görsel ve Trendyol yayın alanları tek render kaynağındadır. |
| Hedefli web doğrulaması | PASS_LOCAL | `npm.cmd run typecheck` ve `npm.cmd test -- TrendyolOperationsPages.test.tsx`: 7/7 PASS. |
| Ayrıntılı tarayıcı/Stage kabulü | NOT_RUN | Bu kullanıcı arayüzü/katalog değişikliği için çalıştırılmadı; dış yazma başlatılmadı. |

## 2026-08-17 - Varyant sıralama ve tema kontrastı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Varyant satır sıralama | CODED_LOCAL | Sol tutma kolu, drag-and-drop ile satırları yeniden sıralar; yeni ürün gönderimindeki varyant dizisi bu sırayı korur. |
| Seçenek değeri görünürlüğü | CODED_LOCAL | Pasif çip metni tema bağımsız koyu renge, seçili çip metni beyaza sabitlendi. |
| Hedefli web doğrulaması | PASS_LOCAL | `npm.cmd run typecheck`; `CatalogWorkspacePages.test.tsx` ve `TrendyolOperationsPages.test.tsx`: 9/9 PASS. |
| Tarayıcı/Stage kabulü | NOT_RUN | Yayın öncesi çalıştırılmadı; dış yazma başlatılmadı. |
