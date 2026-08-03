# F3 Trendyol Dikey Dilim Kanıt Kaydı

İlk doğrulama tarihi: `2026-07-31`; Fake adapter/regresyon ek doğrulaması: `2026-08-02`; üretim paneli salt-okunur Stage doğrulaması: `2026-08-03`; ek read-capability probe doğrulaması: `2026-08-04`. Yerel ortam Windows geliştirme makinesi, .NET SDK `10.0.302`, Node `24.15.0`, npm `11.12.1`, PostgreSQL `18.4`; gerçek bağlantı testi AWS Ubuntu üretim dağıtımından çalıştırıldı. Production write kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F3-EV-001` build/format | PASS | `dotnet format --verify-no-changes`; solution build `0 warning / 0 error` |
| `F3-EV-002` migration | PASS | Fresh PostgreSQL 18.4; F1→F2→F3 tek zincir; `sales` + mevcut şemalar; seed yok; migration SHA-256 `3DF8E760B46B659EA88CA9727A6B97F390D8647EAB8854E37D91E29A5DF09938` |
| `F3-EV-003` connection/capability | PASS_LOCAL | STAGE/PRODUCTION allow-list, yalnız V2, şifreli credential rotasyonu, evidence scope, `UNKNOWN` başlangıcı ve çift write kill switch kod/test incelemesi geçti; gerçek identity testi `BLOCKED_EXTERNAL` |
| `F3-EV-004` Product/reference fixtures | PASS_LOCAL | Approved product, category/attribute/value/brand mapping sınırı ve Product V2 exact path; gerçek Stage read `BLOCKED_EXTERNAL` |
| `F3-EV-005` stock/price/batch | PARTIAL_LOCAL | Item-level partial batch fixture geçti; ayrık stock/price portunun resmî birleşik payload'ı uydurması engellendi; safe-write `BLOCKED_EXTERNAL` |
| `F3-EV-006` webhook duplicate | READY_LOCAL_REVALIDATION | Route-token HMAC hash, constant-time auth, Inbox UQ ve job dedup uygulandı. Eşzamanlı duplicate yarışında yalnız doğrulanmış aynı Inbox+Job kaydı duplicate-as-success olur; farklı webhook'ların subscription metadata güncellemesi atomiktir. 20 paralel PostgreSQL/ACK p95 testi eklendi; yerel Docker/test DB credential olmadığı için çalıştırılması CI/uygun PostgreSQL ortamını bekliyor. |
| `F3-EV-007` out-of-order/overlap | PASS_LOCAL_CORE | State machine regression testleri, status event UQ, last-modified/cursor overlap ve webhook/poll ortak ingestion hattı mevcut |
| `F3-EV-008` split/partial cancel | PASS_DOMAIN | Quantity invariant ve regression testleri geçti; gerçek split fixture `BLOCKED_EXTERNAL` |
| `F3-EV-009` webhook security/ACK | PARTIAL_LOCAL | Basic/API-key auth, raw JSON contract, body bound, durable Inbox-before-ACK uygulandı; public HTTPS ve p95 ölçümü `BLOCKED_EXTERNAL` |
| `F3-EV-010` resilience | PASS_LOCAL_CORE | 401/403/429/5xx/timeout sınıfları, Retry-After, worker allow-list/lease/reaper; gerçek platform 429/5xx `BLOCKED_EXTERNAL` |
| `F3-EV-011` shipment document | MODEL_READY | Private FileAsset FK, versioned document/attempt ve capability-driven format UI; Stage label/cargo + public/private delivery `BLOCKED_EXTERNAL` |
| `F3-EV-012` return/disposition | PASS_LOCAL_CORE | Append-only decision, evidence FK, idempotency key ve yalnız `PASS` ledger artışı production kodunda; gerçek return action `BLOCKED_EXTERNAL` |
| `F3-EV-013` reconciliation/kill switch | PASS_LOCAL_DRY | Hash'li/açıklanabilir local dry-run differences; global + connection writes varsayılan kapalı; remote comparison ve restart `BLOCKED_EXTERNAL` |
| `F3-EV-014` API surface | PASS | Route guard F3 connection/order/shipment/return/opaque hook yollarını doğruladı; F4+ yasaklı yüzey yok |
| `F3-EV-015` UI | PASS_BUILD | Strict TypeScript, Vitest ve Vite build geçti; Teal Precision F3 ekranları faz filtreli; otomatik localhost görsel kontrolü browser güvenlik politikası nedeniyle çalıştırılamadı |
| `F3-EV-016` 5x/performance | OPEN | F2 temel hacim kanıtı korunuyor; gerçek F3 order/webhook p95 ölçümü Stage verisi olmadan kapanmadı |
| `F3-EV-017` secret/PII scan | PASS | F3 source/fixture taramasında gerçek Basic token, 11 haneli kimlik veya e-posta eşleşmesi yok; demo ileri-faz route taraması yalnız guard testlerinde bulundu |
| `F3-EV-018` Stage/SIT | PARTIAL_PASS_READ | 2026-08-04 üretim paneli tekrar testi `ACTIVE`; `ConnectionTest`, `OrderRead` ve `ProductRead` `SUPPORTED`. `ReturnRead`, `ReferenceRead`, webhook ve bütün write capability'leri `UNKNOWN`/off |
| `F3-EV-019` production smoke | BLOCKED_EXTERNAL | Ubuntu sunucu/domain/credential ve işlem başına etki onayı yok; dış yazma kapalı |
| `F3-EV-020` deterministic Fake adapter | PASS_FULL_LOCAL_FAKE_RC / SANDBOX OPEN | Test-only adapter bütün generic portları uygular; success/empty/partial/auth/429/5xx/timeout/validation/contract senaryoları, deterministic clock, varsayılan write-off ve replay’de tek etki test edildi. PostgreSQL job→lease→processor→worker-kill/reaper→retry→completion zinciri tek Order/OrderLine/cursor üretti. Ayrı RC testi gerçek Chromium oturumu→API→PostgreSQL job→gerçek Worker→Fake adapter→sipariş listesi ve detay UI zincirini tamamladı. Production DI/ağ/auth/secret bağımlılığı yok; gerçek platform sandbox/SIT bölümü açıktır |
| `F3-EV-021` Trendyol production read-only sync | PASS_TARGET_READ_ONLY | `release-2026-08-03-7` (`a5f3eac`) immutable app `sha256:2029f449…75070e8` ve edge `sha256:99cb59ea…120902` ile production deploy edildi; API/PostgreSQL/Caddy healthy, Worker running ve HTTPS readiness `200`. Trendyol Stage bağlantısı `ACTIVE`; `CONNECTION_TEST` ve `ORDER_READ` `SUPPORTED`, diğer capability’ler `UNKNOWN`/off. `TRENDYOL_ORDER_SYNC` tek denemede `SUCCEEDED`, `LastErrorCode=null`; panel sipariş listesi doldu. Hiçbir dış write capability veya butonu açılmadı. |
| `F3-EV-022` Trendyol satır/paket doğrulaması | PASS_TARGET_READ_ONLY | 2026-08-03 salt-okunur tekrar eşitlemede 4.037 sipariş satırı aktarıldı. Resmî stream yanıtında `lines` dizisi ve 1–3 kalemli paketler görüldü. Tahsisli kalemlerle paket tutarı karşılaştırılan 13 faturalanabilir paketin 13'ünde toplam eşleşti; dış write yapılmadı. |
| `F3-EV-023` Trendyol ek read-capability keşfi | PASS_TARGET_READ_ONLY | `release-2026-08-04-2` / `68ee1ae` için GitHub Actions run `30855835471` başarılı oldu; app `sha256:89e87332…a9edf`, edge `sha256:ff29e11b…e9c8b` ile production deploy edildi. İkinci backup seti `20260803T214803Z` checksum doğrulamasını geçti; API/Caddy healthy, readiness/live `200`. Yeni panel tıklaması önceki işten ayrı `TRENDYOL_CONNECTION_TEST` işi üretti ve tek denemede `SUCCEEDED`; `ConnectionTest`, `OrderRead`, `ProductRead` `SUPPORTED`. `ReturnRead`, `REMOTE_RESOURCE_NOT_FOUND` nedeniyle `UNKNOWN`; otomatik probe edilmeyen `ReferenceRead` `UNKNOWN`. Global ve connection write anahtarları kapalı kaldı. Adaptör sözleşme paketi `55/55`, API yüzey paketi `2/2 PASS`. |
| `F3-EV-024` Trendyol katalog referansı probu | READY_LOCAL_READ_ONLY | Resmî kategori ağacı Stage endpoint'i mevcut `IReferenceDataPort` üzerinden capability keşfine eklendi. Başarılı response `ReferenceRead=SUPPORTED`, HTTP/contract hatası `UNKNOWN` üretir; hiçbir write metodu yoktur. Adaptör sözleşme paketi `55/55`, API yüzey paketi `2/2` ve format kontrolü geçti. Production dağıtımı ve yeni panel testi bekleniyor; capability henüz açılmadı. |
| `F3-EV-025` paralel webhook yarış koruması | READY_LOCAL_REVALIDATION | `F3WebhookService`, check-then-insert yarışında PostgreSQL unique violation'ını yalnız aynı payload hash'li Inbox ve aynı dedup anahtarlı Job gerçekten commit edilmişse güvenli başarıya çevirir. Subscription `LastReceivedAt`/version güncellemesi ayrı atomik SQL update'tir; farklı olaylar optimistic-concurrency nedeniyle düşmez. Yeni test 20 eşzamanlı aynı webhook için 20 başarılı ACK, tek Inbox, tek Job ve `<500 ms` p95 ister. Solution build `0 warning / 0 error`, Application `19/19`, Adapter `55/55` ve format geçti; PostgreSQL ölçümü ortam bekliyor. |

## Fixture checksum'ları

| Fixture | SHA-256 |
| --- | --- |
| `batch-partial.json` | `EF8E38ABFCEAFF7FECCE45CB108AA8F349EDD2C31F76200D7A3570B210E50536` |
| `order-success.json` | `6791DC669CFE5434A31FBD480E199865957BDF0BDCA339EBA5B84407566E35EC` |
| `product-approved.json` | `565AE2EE9C50BCDAA6B0B8EF1F3F846443CF3614F9C395A6BA3E43EAF727E8CA` |
| `remote-error-empty.json` | `CA3D163BAB055381827226140568F3BEF7EAAC187CEBD76878E0B63E9E442356` |
| `return-success.json` | `ADC1BBB7849D117F7195E7F75BA71BCF608CA7E9485A7B69F6D65A5EE3749779` |

## Güncel regresyon sayımı

- Domain: `20/20`
- Application/model metadata: `19/19`
- Adapter contract: `45/45`
- API surface: `2/2`
- End-to-end guard/Fake/PostgreSQL worker-kill/full-stack browser senaryoları: `15/15`
- PostgreSQL integration: `7/7`
- Toplam: `108/108` .NET testi başarılı; Web component/typecheck/build ayrıca geçmiştir.
- Solution `dotnet format --verify-no-changes` ve `dotnet restore --locked-mode` geçti; doğrudan ve transitive NuGet vulnerability taramasında 11 projenin hiçbirinde bilinen advisory bulunmadı.

## Faz durumu

F3 çekirdek yerel uygulaması `READY_LOCAL_CORE` durumundadır. Tam yerel Fake release-candidate E2E geçmiştir. `F3-EV-016`, gerçek Stage/SIT capability kanıtları, public HTTPS webhook/medya, label/cargo ve safe-write/smoke kanıtları tamamlanmadığından şartname F3 çıkışı `BLOCKED_EXTERNAL` kalır. Sonraki yerel fazların uygulanmış olması bu tarihsel F3 production kapısını kendiliğinden kapatmaz.
