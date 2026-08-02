# F0 İzlenebilirlik Matrisi

## Kural

Bu matris yalnız F0 dokümantasyon teslimatlarını izler. Gelecek kod ve test yolları bilerek `F1+ / henüz yok` olarak gösterilir; F0 sırasında production artefaktı oluşturulmaz. Durumlar: `DONE`, `BLOCKED_EXTERNAL`, `PENDING_F1`.

| Kimlik | Faz | Kabul ölçütü | F0 kanıtı | Gelecek kod/test | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| `F0-REQ-001` | F0 | Gereksinim-faz-kabul-kaynak-durum bağı kuruludur. | Bu matris; `Fx-plan-template.md` | F1+ / henüz yok | Yok | DONE |
| `F0-REQ-002` | F0 | ADR-001–010 karar, sonuç ve değişiklik kapısı içerir. | `docs/adr/ADR-001`–`ADR-010` | F1+ / henüz yok | Yok | DONE |
| `F0-REQ-003` | F0 | Platform sırası değişmeden kaydedilmiştir. | `F0-external-dependencies.md`; capability matrisi | Adapterlar F4+ | Test hesapları | DONE |
| `F0-REQ-004` | F0 | Her platform/capability kanıt alanlarıyla kayıtlıdır. | `docs/platform-rules/capability-matrix.md` | Adapter testleri F4+ | Resmî kaynak ve test hesabı | DONE |
| `F0-REQ-005` | F0 | Güvenli iş otoriteleri açık ve çelişkisizdir. | `F0-business-authorities.md`; ADR-006 | Domain uygulaması F2+ | Yok | DONE |
| `F0-REQ-006` | F0 | Hacim, pik x5, RPO/RTO ve backup profili kayıtlıdır. | `F0-capacity-recovery-profile.md`; ADR-010 | Load/restore testleri F1+ | Hacim baz/x5 tamamlandı; hedef restore ve RTO bekliyor | BLOCKED_EXTERNAL |
| `F0-REQ-007` | F0 | Environment/secret, threat, risk, kill switch ve rollback kayıtlıdır. | İlgili beş F0 belgesi; ADR-007 | Uygulama kontrolleri F1+ | Secret store ve hedef ortam | DONE |
| `F0-REQ-008` | F0 | Fake adapter/anonim fixture standardı tanımlıdır. | `fake-adapter-fixture-standard.md` | Fixture/test uygulaması F1+ | Test hesabı fixture'ları | DONE |
| `F0-REQ-009` | F0 | Hedef Windows VPS Linux container kanıtı vardır. | `windows-vps-runtime-validation.md` | Dağıtım F1+ | VPS erişimi/özellikleri | BLOCKED_EXTERNAL |
| `F0-REQ-010` | F0 | Stitch ileri tarihli, engelleyici olmayan bağımlılıktır. | `F0-external-dependencies.md` | UI uygulaması ilgili faz | Stitch dosyası | DONE |
| `F0-REQ-011` | F0 | Exact sürüm, resmî kaynak, tarih, lock ve digest kayıtlıdır. | `verified-versions.md`; F0 verification lock/digest kanıtları | Production lock/image F1 | Hedef child digest host runbook'una bağlı | DONE_F0 |
| `F0-VAL-001` | F0 | Her gereksinim tek faz ve ölçülebilir kabule bağlıdır. | Bu matris | Yok | Yok | DONE |
| `F0-VAL-002` | F0 | Kanıtsız capability `UNKNOWN`; uydurma sözleşme yoktur. | Capability matrisi incelemesi | Adapter sözleşmeleri F4+ | Test hesapları | DONE |
| `F0-VAL-003` | F0 | Fixture standardı secret/PII yasaklar. | Fixture standardı | Tarama testi F1+ | Fixture erişimi | DONE |
| `F0-VAL-004` | F0 | ADR'ler şartname ve birbirleriyle çelişmez. | ADR karar özeti ve çapraz bağlantılar | Yok | Yok | DONE |
| `F0-VAL-005` | F0 | F1 gerçek platform secret'ı olmadan başlayabilir. | Fake adapter standardı; tüm write anahtarları kapalı | Fake adapter F1 | Yok | DONE |
| `F0-VAL-006` | F0 | Exact sürüm + kaynak + lock/digest eksiksizdir. | F0 locked restore/dry-run, index digest ve Compose checksum kanıtı | Production aktarımı F1 | Hedef child digest host runbook'una bağlı | DONE_F0 |
| `F0-EXIT-001` | F0 | F1'i durduran mimari belirsizlik yoktur. | ADR-001–010 | Yok | Kullanıcı kabulü | DONE |
| `F0-EXIT-002` | F0 | Dış bağımlılık, blocker ve güvenli fallback kayıtlıdır. | Dependency/risk kayıtları | Yok | Dış sağlayıcılar | DONE |
| `F0-EXIT-003` | F0 | Runtime, volume ve backup uygulanabilirliği hedefte kanıtlıdır. | Runbook ve recovery profilinde kanıt yuvaları | Runtime testleri F1 öncesi | Hedef VPS | BLOCKED_EXTERNAL |
| `F0-EXIT-004` | F0 | Sürüm belgesi commitli; lock/digest tutarlıdır. | F0 verification lock/digest seti; baseline commit `00c7b78591f158babb040070bf0aa0f04acace8e` | Production aktarımı F1 | Yok | DONE_F0 |

## F0 sonucu

Dokümantasyon ve dependency kanıt kapsamı tamamlanmıştır. `F0-REQ-006`, `F0-REQ-009` ve `F0-EXIT-003` hedef VPS/restore kanıtı nedeniyle açıktır; F0 çıkışı `BLOCKED`dır.

## F1 uygulama izi

F1 ayrıntılı kabul ve kanıt eşlemesi [F1-plan.md](F1-plan.md) ve [F1-evidence-log.md](F1-evidence-log.md) içindedir. Özet bağ:

| Aralık | Uygulama | Kanıt | Sonuç |
| --- | --- | --- | --- |
| `F1-REQ-001–003` | Root solution/locks, tek AppDbContext/migration, advisory-lock bootstrap | Zero-warning build, fresh/concurrent/repeat PostgreSQL test | DONE_LOCAL |
| `F1-REQ-004–008` | Identity, custom session, CSRF, password-only, TOTP/recovery, revoke ve break-glass | HTTPS auth zinciri, parallel recovery test, audit doğrulaması | DONE_LOCAL |
| `F1-REQ-009–012` | Server-side tenant context, job/inbox/effect, append-only audit, private file | Tenant/file guards; dedup/lease/heartbeat/stale/reaper testleri | DONE_LOCAL |
| `F1-REQ-013–015` | `_FILE` secret, DP volume/PFX gate, JSON health/log, Caddy/Compose/backup | Container smoke, port/non-root/header/cookie ve restore kanıtı | DONE_LOCAL |
| `F1-REQ-016–017` | React auth/security kabuğu ve operasyon runbook'ları | Strict TS, component, bundle, Playwright Chromium; runbook incelemesi | DONE_LOCAL |
| `F1-EXIT-001–006` | Yerel F1 çıkış seti | `F1-EV-001–017` | READY_LOCAL |

Hedef VPS runtime/reboot/volume/RTO, production PFX/off-host hedef ve registry-pushed image digest'i dış bağımlılıktır; production kabulü `BLOCKED_EXTERNAL`dır. Bu F1 kayıt anında F2 henüz açılmamıştı.

## F2 uygulama izi

F2 ayrıntılı kabul ve kanıt eşlemesi [F2-plan.md](F2-plan.md) ve [F2-evidence-log.md](F2-evidence-log.md) içindedir.

| Aralık | Uygulama | Kanıt | Sonuç |
| --- | --- | --- | --- |
| `F2-REQ-001–004` | Product/Variant/katalog/typed attribute/reference mapping/listing override fiziksel modeli | PostgreSQL 18.4 fresh migration, metadata ve constraint testleri | DONE_LOCAL |
| `F2-REQ-005–007` | CSV/XLSX staging, deterministik matching, review/decision/apply ve provenance | Import repeat testi; macro/formula/malformed/CSV-neutralization fixture'ları | DONE_LOCAL |
| `F2-REQ-008–010` | MAIN inventory projection, ledger/idempotency/reservation, ChannelOffer/Money/history | Domain ve PostgreSQL duplicate/history testleri | DONE_LOCAL |
| `F2-REQ-011–012` | F2 API, cursor/ETag/ProblemDetails/idempotency ve capability fail-closed kapıları | Route guard; 1.000 ürün cursor testi; no-job kanıtı | DONE_LOCAL |
| `F2-REQ-013–014` | Onaylı F2 web yolları ve bounded hacim | Strict TS/Vite/Vitest/Playwright; 10.000 satır import testi | DONE_LOCAL |
| `F2-EXIT-001–005` | Yerel F2 çıkış seti | `F2-EV-001–015` | READY_LOCAL |

Gerçek platform capability/test hesapları, hedef VPS runtime, registry digest, production PFX, off-host backup ve ölçülmüş RTO dış bağımlılıktır. Production kabulü `BLOCKED_EXTERNAL`; F3 açılmamıştır.

## F3 uygulama izi

F3 ayrıntılı kabul ve kanıt eşlemesi [F3-plan.md](F3-plan.md) ve [F3-evidence-log.md](F3-evidence-log.md) içindedir.

| Aralık | Uygulama | Kanıt | Sonuç |
| --- | --- | --- | --- |
| `F3-REQ-001–004` | Trendyol connection/credential/capability, Product V2 ve reference adapter sınırı | V2 route/source guard, encrypted credential, fixture parser testleri | DONE_LOCAL_CORE |
| `F3-REQ-005–009` | Product/batch fail-closed, webhook Basic/API-key + Inbox, cursor/overlap polling | Partial batch, raw webhook guard, worker job/dedup kodu | DONE_LOCAL_CORE / WRITE BLOCKED_EXTERNAL |
| `F3-REQ-010–013` | Order/Line/Package/history, ShipmentDocument, Return/Decision/Evidence/Disposition | PostgreSQL 18.4 migration, domain invariant ve metadata testleri | DONE_LOCAL_CORE |
| `F3-REQ-014–019` | Local dry reconciliation, kill switch, F3 API/UI, adapter fixtures ve resilience | Route/repository/secret scan; .NET/Web build ve test seti | READY_LOCAL_CORE; PERFORMANCE/STAGE BLOCKED_EXTERNAL |
| `F3-EXIT-001–006` | F3 çıkış seti | `F3-EV-001–019` | BLOCKED_EXTERNAL |

## F4 uygulama izi

F4 ayrıntılı kabul ve kanıt eşlemesi [F4-plan.md](F4-plan.md) ve [F4-evidence-log.md](F4-evidence-log.md) içindedir.

| Aralık | Uygulama | Kanıt | Sonuç |
| --- | --- | --- | --- |
| `F4-REQ-001–004` | LegalEntity/Policy, invoice/line/party snapshot ve allow-list mali state modeli | PostgreSQL 18.4 fresh migration, metadata/state testleri; onaysız mali otorite fail-closed | DONE_LOCAL_CORE / POLICY BLOCKED_DECISION |
| `F4-REQ-005–010` | Provider/marketplace portları, E-Faturam sınırı, attempt/unknown/document/delivery modeli | Resmî source guard, anonim taxpayer fixture, private checksum/no-store ve job ayrımı | DONE_LOCAL_CORE / PROVIDER-STAGE BLOCKED_EXTERNAL |
| `F4-REQ-011–015` | Cancellation/adjustment fallback, due issue, korumalı API, F4 UI ve reconciliation | API/repository guard, Web build/test, local dry reconciliation | PARTIAL_LOCAL / POLICY-STAGE BLOCKED_EXTERNAL |
| `F4-REQ-016–018` | Credential/PII koruması, tek F4 migration, DB+file restore sınırı | Secret masking/protection, PostgreSQL integration; hedef restore açık | DONE_LOCAL_CORE / RESTORE BLOCKED_EXTERNAL |
| `F4-EXIT-001–004` | F4 yerel çıkış seti | `F4-EV-001–017` | READY_LOCAL_CORE; BLOCKED_EXTERNAL |

## F5 uygulama izi

F5 ayrıntılı kabul ve kanıt eşlemesi [F5-plan.md](F5-plan.md) ve [F5-evidence-log.md](F5-evidence-log.md) içindedir.

| Aralık | Uygulama | Kanıt | Sonuç |
| --- | --- | --- | --- |
| `F5-REQ-001–004` | Pinned Admin GraphQL 2026-07, canonical shop scope, encrypted token/client-secret ve capability UNKNOWN başlangıcı | Build, boundary ve adapter contract testleri | DONE_LOCAL_CORE |
| `F5-REQ-005–009` | Generic product/inventory/order portları, JSONL checkpoint, GraphQL error ayrımı ve fail-closed writes | `F5ShopifyContractTests`; development-store yok | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F5-REQ-010–014` | Raw-body HMAC, Inbox dedupe, worker dispatch, mevcut integrations UI ve no-migration reuse | HMAC testleri, source guard, Web build | DONE_LOCAL_CORE / PUBLIC_WEBHOOK BLOCKED_EXTERNAL |
| `F5-EXIT-001–004` | F5 çıkış seti | `F5-EV-001–010` | READY_LOCAL_CORE; BLOCKED_EXTERNAL |

F6 production kodu, route, menü veya placeholder oluşturulmamıştır.
