# F2 Kanıt Günlüğü

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

F2 uygulama sonucu `READY_LOCAL`dır. Bu makinede Docker CLI/engine güncel oturumda bulunmadığından F2 image/Compose smoke tekrarlanmadı; F2 migration ve tüm persistence testleri doğrudan PostgreSQL 18.4 üzerinde geçti. F1'in mevcut container/backup kanıtı değiştirilmedi. Hedef Windows VPS, registry-pushed immutable digest, production PFX, off-host backup ve ölçülmüş RTO kanıtları VPS kiralandığında tamamlanacaktır; production sonucu `BLOCKED_EXTERNAL`dır.

Gerçek platform test hesapları ve resmî capability kanıtları gelene kadar capability `UNKNOWN`, `FeatureFlags__ExternalWrites=false`; publication, stock-sync ve price-sync dış etki üretmez. F3 ve sonrası açılmamıştır.
