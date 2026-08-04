# F2 - Ürün, Katalog, Fiyat ve Stok Çekirdeği Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `F2` |
| Başlangıç onayı | Kullanıcı 2026-07-31 tarihinde "Geçebilirsin" diyerek F2'yi açtı. |
| Yetkili şartname | v3.5 PDF içinde devamında korunan v3.2 tabanı; özellikle taban sayfa 8-10, 17-21, 24-28, 34-37, 40-41, 47, 57-58 ve 62-63 |
| Ön koşul | F1 `READY_LOCAL`, commit `f0ea0ce` |
| Hedef sonuç | `READY_LOCAL`; F3 ve gerçek platform çağrıları kapalı |

## Hedefler

- Platformdan bağımsız Product/Variant/Option/Value/Media, Category/Brand ve typed attribute çekirdeğini kurmak.
- ReferenceSnapshot/ReferenceItem ve ayrı category/brand/attribute/value mapping tablolarını sorgulanabilir hale getirmek.
- MARKETPLACE, UTF-8 CSV ve makrosuz XLSX kaynaklarını aynı ImportSession staging/preview/review/decision/apply hattından geçirmek.
- ChannelListingProfile ve override kayıtlarını merkezi Product'u değiştirmeden connection kapsamında saklamak.
- ChannelOffer, Money/currency/VAT/price-version geçmişini kurmak.
- MAIN InventoryLocation, InventoryItem projection, append-only StockLedger ve Reservation değişmezlerini uygulamak.
- Tablo 28'deki F2 API kataloğunu ve sayfa 40'taki yalnız F2 ekranlarını işlevsel hale getirmek.

## Kapsam dışı

- F3+ gerçek adapter, credential, capability discovery, platform endpoint/DTO, webhook, sipariş, paket, iade veya dış HTTP çağrısı.
- F4 fatura; ileri raporlama, çok kullanıcılı RBAC ve ikinci tenant bu teslimin dışındadır.
- F3+ `/orders`, `/shipments`, `/returns`, `/invoices`, `/integrations`, `/operations` ekran ve endpointleri.
- Fuzzy/AI ürün eşleme, title similarity merge, broker/cache/mikroservis veya aktif multi-tenant.
- Doğrulanmamış platform enum, alan, endpoint ya da limit.

## Gereksinim ve kanıt matrisi

| Kimlik | Kaynak | Kabul ölçütü | Planlanan kanıt | Durum |
| --- | --- | --- | --- | --- |
| `F2-REQ-001` | Tablo 15 | Product/Variant/Option/Value/Media ve Category/Brand tabloları tenant composite guard'larla vardır. | PostgreSQL migration/FK/UQ testleri | DONE_LOCAL |
| `F2-REQ-002` | Tablo 15, 20 | Typed attribute assignment exactly-one değer kuralı ve active-leaf kategori kuralı uygulanır. | Domain + PostgreSQL + API testleri | DONE_LOCAL |
| `F2-REQ-003` | Tablo 13, 15 | ReferenceSnapshot/Item ve dört ayrı mapping ailesi snapshot/connection kapsamında tutulur. | Persistence ve publish-validation testleri | DONE_LOCAL |
| `F2-REQ-004` | Tablo 15 | Listing profile/variant/attribute/media override'ı merkezi Product kaydını değiştirmez. | Persistence/API uygulaması | DONE_LOCAL |
| `F2-REQ-005` | Tablo 15; sayfa 28 | Deterministik eşleme sırası link → external id → benzersiz barcode → benzersiz SKU; belirsiz kayıt review olur. | PostgreSQL import repeat testi | DONE_LOCAL |
| `F2-REQ-006` | Tablo 15; Tablo 28 | ImportSession allow-list state machine, staging, candidate, CREATE/LINK/SKIP decision ve provenance kayıtları vardır. | Domain + PostgreSQL/worker testi | DONE_LOCAL |
| `F2-REQ-007` | Sayfa 47, 57 | Yalnız UTF-8 CSV ve makrosuz XLSX; macro/formula/external-link/bomb/spoof reddi; güvenli CSV hata çıktısı. | Güvenlik fixture testleri | DONE_LOCAL |
| `F2-REQ-008` | Tablo 16 | MAIN location, item projection, append-only ledger ve reservation tekillikleri uygulanır. | Ledger/duplicate event PostgreSQL testi | DONE_LOCAL |
| `F2-REQ-009` | Tablo 16; sayfa 27 | `available=max(0,on_hand-reserved)` ve publishable safety-stock hesabı kullanıcıdan yazılamaz. | Domain/persistence testi | DONE_LOCAL |
| `F2-REQ-010` | Tablo 16 | ChannelOffer decimal precision, ISO currency, VAT, rounding ve price-version history kurallarına uyar. | Money/history/concurrency testi | DONE_LOCAL |
| `F2-REQ-011` | Tablo 25, 28 | Limit 50/max 200, ETag/If-Match, ProblemDetails, Idempotency-Key ve tenant-safe 404 uygulanır. | API contract + persistence guard | DONE_LOCAL |
| `F2-REQ-012` | Tablo 28 | Ürün, katalog, import, inventory ve offer endpoint aileleri vardır; publication/sync UNKNOWN capability'de dış etki üretmez. | Route + no-job/no-HTTP testi | DONE_LOCAL |
| `F2-REQ-013` | Tablo 31-32 | F2 ekranları loading/empty/error/review/archived/concurrency durumları ve erişilebilir metin+renk taşır. | Typecheck/component/Playwright | DONE_LOCAL |
| `F2-REQ-014` | Sayfa 58 | 10.000 satır import memory-bounded streaming/cursor yaklaşımı kullanır; temsilî 1.000 ürün listesi p95 bütçesine göre ölçülür. | 10.000 CSV + 1.000 ürün/20 cursor sayfası | DONE_LOCAL |

## Dosya etkisi

- Domain: `Catalog`, `Imports`, `InventoryPricing` model ve değişmezleri.
- Application: F2 command/query contract'ları ve servis portları.
- Infrastructure: tek `AppDbContext`, F2 mapping'leri, import parser/processor, catalog/inventory servisleri ve tarihsel F2 migration.
- API: kaynak grubu bazlı F2 endpointleri, ETag/If-Match ve idempotency yardımcıları.
- Worker: yalnız F2 import preview/apply işlerini dispatch eder; platform publish/sync yapmaz.
- Web: `/products`, `/products/new`, `/products/:id`, `/catalog/categories`, `/catalog/brands`, `/catalog/attributes`, `/imports`, `/imports/:id`, `/inventory`.
- Tests/docs: mevcut altı test projesi, evidence ve traceability güncellemeleri.

## Güvenlik, teknoloji ve capability kapıları

- Teknoloji major/minor hattı ve tek migration zinciri değişmez; yeni paket yalnız somut gerek varsa exact lock ile eklenir.
- Genel upload üst sınırı şartnamedeki `MAX_UPLOAD_BYTES=10 MiB` olur; platform limiti olarak sunulmaz.
- XLSX BCL `ZipArchive`/`XmlReader` ile veri olarak okunur; macro, formula ve external relationship çalıştırılmaz.
- Gerçek platform capability'leri `UNKNOWN`, `external-writes=false` kalır. Publication/stock/price sync endpointleri mapping/capability doğrulamasından önce job veya HTTP çağrısı üretmez.
- İstemci tenant id'si otorite değildir; tüm sorgular authenticated server-side tenant context kullanır.

## Çıkış kriterleri

- `F2-EXIT-001`: Repeat import duplicate Product/Variant/Link üretmez; conflict manual review görünürdür.
- `F2-EXIT-002`: Ledger/reservation, money/currency/price-version, typed attribute ve active-leaf testleri geçer.
- `F2-EXIT-003`: Mapping/capability eksikken publish/sync job'ı ve dış HTTP çağrısı yoktur.
- `F2-EXIT-004`: CSV/XLSX güvenlik, file tenant scope, archive ve listing override testleri geçer.
- `F2-EXIT-005`: F2 API/UI işlevsel; F3+ route/menu/adapter yoktur; build/format/test/container/restore kanıtları kayıtlıdır.

## Blocker ve açık kararlar

- Gerçek platform reference snapshot/capability verisi F3 bağımlılığıdır; F2 bunu anonim fixture ve doğrudan persistence testleriyle doğrular.
- Stitch dosyası yoktur; erişilebilir varsayılan UI bağlayıcı fallback'tir.
- Ürün sayısı başlangıçta yaklaşık 1.000, yıllık sipariş 15.000'dir; ürün büyümesine karşı cursor/index ve 10.000 satır import hedefi korunur.
- F2 yerel uygulaması Ubuntu sunucu gerektirmez. Hedef Ubuntu Server/registry/PFX/off-host/RTO kanıtları production kapısında `BLOCKED_EXTERNAL` kalır.

## Uygulama sonucu

F2 uygulama ve yerel PostgreSQL 18 doğrulama sonucu `READY_LOCAL`dır. Güncel kanıtlar `F2-evidence-log.md` dosyasındadır. Gerçek platform capability'leri `UNKNOWN`, dış yazmalar kapalı ve hedef Ubuntu Server/registry/PFX/off-host/RTO kanıtları `BLOCKED_EXTERNAL` kalır; bu durum F3'ü otomatik açmaz.
