# F0 - Keşif, Kararlar ve Uygulama Sözleşmesi Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Aktif faz | F0 |
| Plan durumu | EXECUTED |
| F0 uygulama durumu | DOCUMENTATION COMPLETE - exit kanıtları kısmen blocked |
| F0 çıkış durumu | BLOCKED |
| Hazırlanma tarihi | 2026-07-31 |
| Yetkili ürün / çözüm | Ravencia Entegrasyon / MarketplaceHub |
| Yetkili şartname | Repository kökü `Ravencia_Entegrasyon_v3_2_Nihai_Uygulama_Surumu.pdf`, v3.2, 73 sayfa |
| Şartname SHA-256 | `E98365DC34804A478D5DBB41E1997FB6742FD0723A76C08CEE138321F0E2ECA3` |

Bu dosya yalnız F0 uygulamasını planlar ve gerçekleşen F0 dokümantasyon durumunu kaydeder. Kullanıcı onayı 2026-07-31 tarihinde verilmiş, F0 belgeleri oluşturulmuştur. Bu durum F0 çıkış kapısının geçildiği anlamına gelmez.

## Yetkili kaynak ve karar önceliği

Repository kökündeki canonical şartname aşağıdaki dosyadır:

`Ravencia_Entegrasyon_v3_2_Nihai_Uygulama_Surumu.pdf`

Bu dosya, kullanıcının sağladığı `C:\Users\emrek\OneDrive\Masaüstü\Ravencia_Entegrasyon_v3_2_Nihai_Uygulama_Surumu (1).pdf` kaynağından byte-for-byte kopyalanmıştır. Her iki dosya 73 sayfa ve aynı SHA-256 değerindedir. `RISK-SPEC-001` kapanmıştır; içerik hakkında ikinci veya çelişen şartname üretilmez.

F0 sırasında kaynak önceliği aşağıdaki sırada uygulanacaktır:

1. Yetkili şartnamenin bağlayıcı mimari, güvenlik, veri bütünlüğü ve kapsam kararları.
2. Kullanıcının daha sonra açıkça verdiği yazılı karar.
3. Doğrulama tarihi kaydedilmiş güncel resmi platform veya teknoloji dokümanı.
4. Test hesabından alınmış, anonimleştirilmiş fixture ve doğrulanmış davranış.
5. Şartnameyle çelişmeyen kayıtlı ADR.
6. Aktif F0 planı ve kayıtlı uygulama kanıtları.

Alt kaynaklar tenant kapsamını, idempotency'yi, secret güvenliğini, mali kayıt bütünlüğünü veya veri kaybını önleme kontrollerini geçersiz kılamaz.

## F0 hedefleri

F0'ın amacı, üretim kodundan önce iş otoritelerini, platform kanıt yöntemini, kapasiteyi ve operasyon sınırlarını kayıt altına almak ve F1'in güvenli biçimde başlayabilmesi için karar sözleşmesini oluşturmaktır.

F0 aşağıdaki sonuçları hedefler:

- Gereksinim-faz-kod-test izlenebilirliğini ve faz planı şablonunu tanımlamak.
- ADR-001 ile ADR-010 arasındaki başlangıç karar kayıtlarını yetkili şartnameye uygun oluşturmak.
- Bağlayıcı platform sırasını ve her platform için capability kanıt yöntemini kaydetmek.
- Stok, fiyat, depo, safety stock, iade restock ve otomatik fatura otoritelerini sabitlemek.
- Gerçek hacim ile pik x5 kapasite profilini, RPO/RTO'yu, `BACKUP_PROFILE` değerini ve off-host hedefini belirlemek.
- Environment/secret kataloğunu, threat modelini, risk kaydını, kill switch'leri ve rollback yaklaşımını tanımlamak.
- Fake adapter ve secret/PII içermeyen anonim fixture standardını hazırlamak.
- Hedef Windows VPS üzerinde Linux container runtime, restart, volume ve backup uygulanabilirliğini kanıtlamak.
- Stitch arayüz dosyasını ileri tarihli ve engelleyici olmayan UI bağımlılığı olarak kaydetmek.
- Exact teknoloji sürümlerini, resmi kaynaklarını, destek/EOL durumlarını, digest ve lock kanıtlarını kaydetmek.

## Kapsam dışı alanlar

F0 boyunca aşağıdakiler oluşturulmaz veya değiştirilmez:

- Her türlü production kodu, solution veya proje scaffold'u.
- Domain, Application, Infrastructure, Api, Worker veya Web uygulama dosyaları.
- Migration, DbContext, tablo, constraint, seed veya bootstrap uygulaması.
- Controller, endpoint, OpenAPI contract, menü, route, ekran veya placeholder.
- F1 veya daha sonraki fazlara ait production davranışı ve test uygulaması.
- F7B kapsamındaki aynı işletmede çok kullanıcı, rol matrisi, impersonation veya kullanıcı yönetimi.
- F8 kapsamındaki ikinci tenant, tenant CRUD, tenant switcher, kota, abonelik, tenant scheduler veya PostgreSQL RLS.
- Mikroservis, Kubernetes, RabbitMQ, Kafka, Redis, ikinci broker, event bus veya service mesh.
- Doğrulanmamış platform endpoint'i, alanı, enum'u, limiti, auth yöntemi, webhook davranışı veya sahte başarılı cevap.
- Gerçek platform credential'ı kullanımı, dış read/write çağrısı, canlı migration, secret rotasyonu veya production yayını.
- ADR ile alternatif mimari seçimi; ADR'ler yalnız bağlayıcı kararları ve değiştirme kapılarını kaydeder.

## Mevcut repository ve ortam durumu

### Repository

| Kontrol | Sonuç |
| --- | --- |
| Repository başlangıç içeriği | `.git` dışında dosya yoktu; F0 ile `docs/` ve canonical şartname PDF'si oluşturuldu |
| Git dalı | `master`, unborn branch |
| Commit sayısı | 2 F0 commit'i (baseline kanıtı + çıkış durumu) |
| Git remote | Yok |
| Çalışma ağacı | F0 belgeleri ve canonical şartname tracked; kapanış commit'i sonrası temiz; production yolu yok |
| Canonical kök PDF | Mevcut; 73 sayfa; SHA-256 şartname kaydıyla eşit |
| `MarketplaceHub.sln` ve proje yapısı | Yok |
| `global.json`, central package ve lock dosyaları | Yok |
| F0 verification lock/digest dosyaları | Mevcut; production Compose/Dockerfile/application image henüz yok |

Boş repository F0 dokümantasyonunun başlatılmasına engel değildir. Şartnamedeki solution ve production proje yapısının oluşturulması F1 kapsamındadır.

### Yerel araçlar

| Araç | Gözlenen durum | F0 yorumu |
| --- | --- | --- |
| .NET SDK | `10.0.302` | .NET 10 major tabanıyla uyumlu; exact seçim resmi kaynakla ayrıca kaydedilecek |
| ASP.NET Core runtime | `10.0.10` | ASP.NET Core 10 tabanıyla uyumlu |
| .NET runtime | `10.0.10` | .NET 10 tabanıyla uyumlu |
| Node.js | `v24.15.0` | Node.js 24 LTS tabanıyla uyumlu |
| npm | `11.12.1` | F0 verification lock üretildi; production Web lockfile'ı F1'de |
| Docker | Bulunamadı | Yerel Compose/runtime kanıtı üretilemiyor |
| Caddy | Bulunamadı | Container/digest doğrulaması gerekiyor |
| psql | Bulunamadı | PostgreSQL 18 yerel istemci/runtime kanıtı yok |
| WSL | `wsl.exe` mevcut, WSL dağıtımı/altyapısı kurulu değil | Linux container doğrulaması yapılamıyor |

Bu bilgisayar hedef Windows VPS olarak kabul edilmez. Hedef host kanıtı ayrıca üretilmelidir.

## Gereksinim kimlikleri ve izlenebilirlik

### F0 teslimat gereksinimleri

| Kimlik | Yetkili kaynak | Gereksinim ve kabul ölçütü | Kanıt / gelecek dosya | Dış bağımlılık | Güncel durum |
| --- | --- | --- | --- | --- | --- |
| `F0-REQ-001` | F0 Teslimatlar; AI Çalışma Protokolü | Her gereksinimin faz, kabul kriteri, gelecek kod konumu ve test kanıtı vardır; tekrar kullanılabilir faz planı şablonu hazırlanır. | `docs/implementation/traceability-matrix.md`; `docs/implementation/Fx-plan-template.md` | Yok | DONE |
| `F0-REQ-002` | F0 Teslimatlar | ADR-001 ile ADR-010 şartnameyle çelişmeden oluşturulur; her ADR karar, gerekçe, sonuç ve değiştirme kapısını içerir. | `docs/adr/ADR-001-*.md` - `ADR-010-*.md`; tutarlılık incelemesi | Açık işletme/operasyon girdileri | DONE |
| `F0-REQ-003` | F0 Teslimatlar | Platform sırası `Trendyol -> E-Faturam -> Shopify -> Hepsiburada -> N11 -> Pazarama` olarak korunur. | İzlenebilirlik matrisi ve capability matrisi sıra kontrolü | Yok | DONE |
| `F0-REQ-004` | Platform Adaptör Sözleşmesi; F0 Teslimatlar | Her platform için capability kodu, support level, kapsam, resmi URL, API version, scope, test hesabı ve kanıt durumu kayıtlıdır. Kanıtsız capability `UNKNOWN` kalır. | `docs/platform-rules/capability-matrix.md`; kaynak erişim ve fixture kayıtları | Platform dokümanı, credential/test hesabı | DONE; capability'ler UNKNOWN |
| `F0-REQ-005` | Başlangıçta Güvenli Varsayılanlar; F0 Teslimatlar | Merkezi StockLedger, merkezi fiyat ve açık kanal override, tek `MAIN` depo, safety stock `0`, yalnız `PASS` iade restock ve otomatik fatura kapalı varsayımları kaydedilir. | `docs/implementation/F0-business-authorities.md`; ADR-006 | İş sahibi değişiklik talebi yoksa bağımlılık yok | DONE |
| `F0-REQ-006` | F0 Teslimatlar; Yedekleme Profili Kararı; Tablo 48 | Gerçek hacim ve pik x5 profilinin yanı sıra RPO/RTO, `BACKUP_PROFILE` ve varsa off-host hedef kayıtlıdır. | `docs/implementation/F0-capacity-recovery-profile.md`; risk ve restore kanıtı | Hacim baz/x5 tamamlandı; hedef disk/volume ve restore-RTO bekliyor | BLOCKED_EXTERNAL |
| `F0-REQ-007` | Güvenlik, Dağıtım ve Operasyon; F0 Teslimatlar | Environment/secret kataloğu, threat model, risk kaydı, global/platform kill switch başlangıçta kapalı davranışı ve rollback yaklaşımı yazılıdır. | `docs/implementation/F0-environment-secret-catalog.md`; `F0-threat-model.md`; `F0-risk-register.md`; `F0-operational-controls.md` | Hedef ortam ve backup tercihi | DONE; ortam kanıtı açık |
| `F0-REQ-008` | F0 Teslimatlar; Test Verisi ve Fixture Kuralları | Fake adapter standardı deterministik senaryoları tanımlar; fixture secret veya doğrudan PII içermez, kaynak/checksum ve doğruladığı mapping kaydedilir. | `docs/platform-rules/fake-adapter-fixture-standard.md`; fixture tarama kanıtı | Anonim platform fixture'ları ileri adaptör fazlarında gerekir | DONE; fixture uygulaması F1+ |
| `F0-REQ-009` | Docker Runtime Önkoşulu; F0 Teslimatlar | Hedef Windows VPS'te Hyper-V/WSL2/Docker Linux container desteği, production desteği, otomatik restart, disk/volume kalıcılığı ve backup hedefi doğrulanır. Native Windows container'a sessiz geçiş yapılmaz. | `docs/runbooks/windows-vps-runtime-validation.md`; komut çıktısı ve restart/volume/restore kanıtı | Hedef Windows VPS erişimi ve sağlayıcı bilgisi | BLOCKED_EXTERNAL |
| `F0-REQ-010` | F0 Teslimatlar; Tablo 48 | Stitch dosyası planlı, ileri tarihli ve markalı görsel fidelity bağımlılığı olarak kaydedilir; yokluğu F1'in işlevsel yerel geliştirmesini engellemez. | `docs/implementation/F0-external-dependencies.md` | Stitch dosyası daha sonra | DONE; engelleyici değil |
| `F0-REQ-011` | Teknoloji ve Sürüm Sözleşmesi; F0 Teslimatlar | Her bileşen için hedef major/minor, seçilen exact sürüm, resmi URL, tarih, destek/EOL, uyumluluk, digest veya N/A gerekçesi ve lock konumu bulunur; floating/latest kullanılmaz. | `docs/dependencies/verified-versions.md`; F0 verification lock, registry index digest ve Compose checksum kanıtı | Production aktarımı F1; host child digest'i runtime runbook'unda | DONE_F0 |

### F0 zorunlu doğrulamaları

| Kimlik | Kabul ölçütü | Kanıt | Güncel durum |
| --- | --- | --- | --- |
| `F0-VAL-001` | Her gereksinim tek bir faza ve ölçülebilir kabul kriterine bağlıdır. | İzlenebilirlik matrisi completeness kontrolü | DONE |
| `F0-VAL-002` | Doğrulanmamış capability `UNKNOWN` durumundadır; endpoint, alan, enum veya limit uydurulmaz. | Capability matrisi varsayılan ve kaynak kontrolü | DONE |
| `F0-VAL-003` | Fixture'larda secret veya PII yoktur. | Fixture standardı; gerçek fixture ilgili fazda taranacak | DONE_FOR_F0 |
| `F0-VAL-004` | ADR'ler birbirleriyle ve yetkili şartnameyle çelişmez. | ADR karar matrisi ve çapraz inceleme | DONE |
| `F0-VAL-005` | F1 yerel geliştirmesi gerçek platform secret'ı olmadan başlayabilir. | Fake adapter sözleşmesi, `_FILE` secret planı ve tüm dış write anahtarlarının kapalı olması | DONE |
| `F0-VAL-006` | Her teknoloji bileşeninin exact sürümü ve resmi kanıtı doludur; latest/floating image yoktur. | Locked NuGet restore, locked npm dry-run, registry index digest ve Compose checksum incelemesi | DONE_F0 |

### F0 çıkış kriterleri

| Kimlik | Geçiş koşulu | Gerekli kanıt | Mevcut durum |
| --- | --- | --- | --- |
| `F0-EXIT-001` | F1'i durduran mimari belirsizlik kalmamıştır. | ADR-001-010 kabulü; açık kararların kayıtlı güvenli fallback'i | DONE |
| `F0-EXIT-002` | Dış bağımlılıklar blocker ve güvenli fallback ile kayıtlıdır. | External dependency ve risk kayıtları | DONE |
| `F0-EXIT-003` | Runtime, volume ve backup hedefinin uygulanabilirliği doğrulanmıştır. | Hedef Windows VPS Linux-container, reboot, volume ve restore kanıtı | BLOCKED_EXTERNAL |
| `F0-EXIT-004` | `docs/dependencies/verified-versions.md` commit edilmiştir ve lockfile/image digest'leriyle tutarlıdır. | F0 verification seti; NuGet/npm lock hash'leri; registry index digest'leri; baseline commit `00c7b78591f158babb040070bf0aa0f04acace8e` | DONE_F0 |

## Oluşturulacak veya değiştirilecek dosyalar

### Bu ilk görev

| Dosya | İşlem | Amaç |
| --- | --- | --- |
| `docs/implementation/F0-plan.md` | Oluşturulacak | F0 hedefi, kapsamı, izlenebilirliği, teslimatları, doğrulama ve çıkış kapılarını tanımlamak |

Bu görevde başka dosya oluşturulmaz veya değiştirilmez.

### Kullanıcı F0 uygulamasını onayladıktan sonra planlanan F0 dosyaları

| Dosya | Amaç |
| --- | --- |
| `docs/implementation/traceability-matrix.md` | Gereksinim-faz-kod-test-kabul-kaynak-durum izlenebilirliği |
| `docs/implementation/Fx-plan-template.md` | Sonraki fazların zorunlu plan şablonu |
| `docs/implementation/F0-business-authorities.md` | Stok, fiyat, depo, safety stock, iade ve fatura otoriteleri |
| `docs/implementation/F0-capacity-recovery-profile.md` | Hacim, pik x5, RPO/RTO, backup profili ve off-host hedefi |
| `docs/implementation/F0-environment-secret-catalog.md` | Environment değişkenleri, secret sınıfı, kaynağı ve yaşam döngüsü |
| `docs/implementation/F0-threat-model.md` | Şartnamedeki tehdit/kontrol/kanıt tabanı |
| `docs/implementation/F0-risk-register.md` | Risk, etki, sahip, kabul/azaltma ve sonraki adım |
| `docs/implementation/F0-operational-controls.md` | Kill switch, rollback ve güvenli dış write kapıları |
| `docs/implementation/F0-external-dependencies.md` | Dış girdiler, fallback ve canlı kapısı |
| `docs/platform-rules/capability-matrix.md` | Platform ve capability bazında kanıt matrisi |
| `docs/platform-rules/fake-adapter-fixture-standard.md` | Fake adapter, anonim fixture ve checksum standardı |
| `docs/runbooks/windows-vps-runtime-validation.md` | Windows VPS üzerinde Linux container, restart, volume ve backup kanıtı |
| `docs/dependencies/verified-versions.md` | Exact sürüm, resmi kaynak, tarih, digest/lock, EOL ve uyumluluk |
| `docs/dependencies/verification/` | Non-production SDK/NuGet/NPM lock, registry digest ve Compose checksum kanıtı |
| `docs/implementation/F0-phase-boundary-decision.md` | F0/F1 lock faz gerilimi için kullanıcı onaylı dar kapsamlı karar |
| `docs/implementation/F0-evidence-log.md` | Çalıştırılan F0 doğrulamalarının gerçek sonuçları |
| `docs/adr/ADR-001-modular-monolith-process-boundaries.md` | Modüler monolit, Clean Architecture, ayrı API/Worker sınırları |
| `docs/adr/ADR-002-single-tenant-skeleton.md` | Tek işletme/Owner, tenant iskeleti ve aktif multi-tenant yasağı |
| `docs/adr/ADR-003-postgresql-data-and-migration-chain.md` | Tek PostgreSQL, tek AppDbContext ve tek migration zinciri |
| `docs/adr/ADR-004-postgresql-job-inbox-idempotency.md` | PostgreSQL job queue, Inbox ve idempotency/reconciliation |
| `docs/adr/ADR-005-adapter-capability-evidence.md` | Adapter sınırı, capability ve `UNKNOWN` kanıt politikası |
| `docs/adr/ADR-006-business-authorities-safe-defaults.md` | Bağlayıcı iş otoriteleri ve güvenli varsayılanlar |
| `docs/adr/ADR-007-identity-and-secret-security.md` | Bootstrap, identity, TOTP, credential ve Data Protection kararları |
| `docs/adr/ADR-008-private-file-storage.md` | Yerel private volume, `IFileStorage` ve tenant kapsamlı yollar |
| `docs/adr/ADR-009-windows-vps-linux-container-runtime.md` | Windows VPS üzerinde Linux container ve Compose dağıtımı |
| `docs/adr/ADR-010-backup-rpo-rto-restore.md` | Backup profili, RPO/RTO, off-host ve restore yaklaşımı |

ADR dosya adları kayıt konularını gösterir; içerikleri yeni mimari seçenek üretmeyecek, şartnamenin bağlayıcı kararını ve yalnız izin verilen değiştirme kapısını kaydedecektir.

## Teknoloji ve sürüm doğrulama planı

### Değiştirilemez teknoloji tabanı

| Katman | Şartname tabanı | F0 doğrulaması |
| --- | --- | --- |
| Backend | .NET 10 LTS, ASP.NET Core 10, C# 14 | Exact SDK/runtime patch, destek politikası ve uyumluluk |
| ORM | EF Core 10, Npgsql/EFCore.PG 10 | Exact NuGet sürümü, resmi release note, merkezi paket konumu |
| Veritabanı | PostgreSQL 18; belgede 18.4 doğrulanmış | Aynı major dalında güncel güvenlik patch'i, exact image digest, UTC ve platform desteği |
| Frontend runtime | Node.js 24 LTS, npm lockfile | Exact Node/npm sürümü, LTS/EOL ve lock üretim politikası |
| UI | React 19.2, TypeScript 6.0, Vite 8.1 | Exact npm sürümleri, resmi release kaynakları ve birlikte çalışma notu |
| Routing/veri | React Router 7, TanStack Query 5, TanStack Table 8 | Exact npm sürümleri ve destek durumu |
| Form/doğrulama | React Hook Form 7, Zod 4 | Exact npm sürümleri ve uyumluluk |
| Stil | Tailwind CSS 4.3, Radix tabanlı yerel bileşenler | Exact sürüm ve runtime CDN kullanılmadığına dair kayıt |
| Proxy | Caddy 2.11; Compose sözleşmesinde 2.11.3 | Aynı dalın seçilen exact patch'i ve image digest'i |
| Log/dayanıklılık | Serilog JSON, `Microsoft.Extensions.Http.Resilience` | Exact NuGet sürümleri, lisans ve bakım durumu |
| Test | xUnit v3, Testcontainers PostgreSQL, Playwright | Exact sürüm, PostgreSQL 18/Chromium uyumluluğu |
| Dağıtım | Docker Compose v2 | Exact aday `2.40.2`; hedef binary/checksum ve Windows VPS Linux-container desteği açık |

### Doğrulama yöntemi

1. Şartnamenin T1-T26 resmi teknoloji kaynakları doğrudan açılır; yönlendirme veya erişim sorunu ayrıca kaydedilir.
2. Şartnamede hedeflenen major/minor korunarak aynı dalın güncel güvenlik patch'i seçilir.
3. Her bileşen için doğrulama tarihi, doğrudan resmi URL, destek/EOL ve platform uyumluluğu yazılır.
4. Container bileşenleri tag yanında immutable digest ile kaydedilir; `latest` ve floating tag reddedilir.
5. NuGet/NPM bileşenleri F0 verification manifest/lock'larında exact çözülür; F1 production konumları ayrıca belirtilir ve aktarımda karşılaştırılır.
6. Paket lisansı ve bakım durumu dependency kaydına eklenir; gereksinimsiz paket seçilmez.
7. `global.json`, `Directory.Packages.props`, `package-lock.json` ve Compose/image pinlerinin oluşturulacağı faz sınırı açık kararla netleştirilir.
8. Sonuç `docs/dependencies/verified-versions.md` içinde her satır için `VERIFIED`, `BLOCKED_EXTERNAL` veya kanıtı eksik açıklamasıyla raporlanır; eksik kanıt başarı sayılmaz.

## Capability doğrulama planı

### Sıra ve varsayılan

Bağlayıcı platform sırası şöyledir:

1. Trendyol
2. E-Faturam
3. Shopify
4. Hepsiburada
5. N11
6. Pazarama

Her capability başlangıçta `UNKNOWN` olacaktır. `SUPPORTED`, yalnız güncel resmi doküman ile test hesabı veya anonim fixture kanıtı birlikte bulunduğunda kullanılacaktır. Resmi belgede bulunmayan bir davranış otomatik olarak `NOT_SUPPORTED` sayılmaz. `UNKNOWN` veya `TEMPORARILY_UNAVAILABLE` durumunda dış yazma kapalıdır. `NOT_SUPPORTED` sessiz başarı değildir.

### Kayıt alanları

Her capability kaydı aşağıdaki şartname alanlarını taşıyacaktır:

- Capability code.
- Support level: `SUPPORTED`, `NOT_SUPPORTED`, `UNKNOWN` veya `TEMPORARILY_UNAVAILABLE`.
- Tenant, connection, environment, platform API version ve external store kapsamı.
- `verifiedAt`, `sourceUrl`, `sourceVersion`, `requiredScope`, constraints ve evidence note.
- Test hesabı/fixture durumu, anonim fixture checksum'u ve doğrulanan mapping/senaryo.
- Read ve write ayrımı, bağlantı kimliği ve güvenli write kapısı.

### Minimum capability grupları

| Grup | Şartnamedeki capability kodları |
| --- | --- |
| Connection | `ConnectionTest`, `CredentialRefresh`, `CapabilityDiscovery` |
| Reference | `CategoryRead`, `AttributeRead`, `AttributeValueRead`, `BrandRead`, `CargoProviderRead` |
| Product | `ProductRead`, `ProductCreate`, `ProductUpdate`, `ProductArchive`, `ProductDelete`, `BatchResultRead` |
| Inventory | `InventoryRead`, `InventoryWrite`, `PriceRead`, `PriceWrite` |
| Order | `OrderRead`, `OrderSingleFetch`, `OrderWebhook`, `PackageRead`, `ShipmentAction`, `LabelRead` |
| Return | `ReturnRead`, `ReturnApprove`, `ReturnReject`, `ReturnDispute`, `ReturnEvidence` |
| Invoice | `TaxpayerQuery`, `InvoiceSubmit`, `InvoiceStatusRead`, `InvoiceDocumentRead`, `InvoiceCancel`, `InvoiceDeliver` |

### Kanıt akışı

1. Şartnamenin P1-P12 resmi platform kaynakları güncel URL/version/tarih bilgisiyle kontrol edilir.
2. Credential, ortam, mağaza/şirket kimliği ve scope yalnız kullanıcı tarafından sağlanır; tahmin edilmez.
3. Okuma desteği yazma desteği olarak yorumlanmaz; her write capability ayrı kanıtlanır.
4. Gerçek test hesabı yoksa Fake adapter ve anonim fixture kullanılır, capability `UNKNOWN` kalır.
5. Fixture secret veya doğrudan PII içermez; kaynak yapısını koruyan anonimleştirme ve SHA-256 checksum kaydedilir.
6. Auth, validation, rate limit, 5xx, timeout, unknown field, missing required field ve partial-result kanıtları ilgili adaptör fazında ayrı fixture olur.
7. Tüm global ve platform write kill switch'leri kapalı kalır; F0'da dış çağrı veya dış yan etki yapılmaz.
8. Resmi kaynak erişilemiyorsa internet örneğinden endpoint üretilmez; ilgili kayıt `UNKNOWN` ve dış bağımlılık `BLOCKED_EXTERNAL` olur.

## Test ve kanıt planı

| Kanıt kimliği | Kontrol | Beklenen sonuç / kanıt |
| --- | --- | --- |
| `F0-EV-001` | Yetkili PDF kimliği | 73 sayfa, başlık/sürüm ve SHA-256 kayıtla eşleşir |
| `F0-EV-002` | Repository baseline | Başlangıç dosya listesi, dal, commit ve remote durumu kaydedilir |
| `F0-EV-003` | Gereksinim kapsamı | `F0-REQ-001-011`, `F0-VAL-001-006` ve `F0-EXIT-001-004` eksiksiz ve tekil bulunur |
| `F0-EV-004` | İzlenebilirlik | Her gereksinimin fazı, kabul kriteri, kaynak, gelecek dosya ve kanıtı vardır |
| `F0-EV-005` | Exact sürüm ve kaynak | Her bileşende resmi URL, tarih, exact patch, EOL, uyumluluk ve digest/lock durumu bulunur |
| `F0-EV-006` | Floating sürüm taraması | `latest` veya kanıtsız floating image/package referansı bulunmaz |
| `F0-EV-007` | Capability matrisi | Bütün platform/capability satırları vardır; kanıtsız olanlar `UNKNOWN`, write kapalıdır |
| `F0-EV-008` | Fixture güvenliği | Secret/PII taraması temizdir; anonimleştirme ve checksum kayıtlıdır |
| `F0-EV-009` | ADR tutarlılığı | ADR-001-010 şartnameye ve birbirlerine karşı çapraz incelenmiştir |
| `F0-EV-010` | F1 secretsiz başlangıç | Fake adapter standardı ve kapalı write flag'leri gerçek platform secret'ı olmadan geliştirmeyi mümkün kılar |
| `F0-EV-011` | Windows VPS runtime | Linux container, Hyper-V/WSL2/runtime desteği, reboot/restart ve production desteği gerçek hedef hostta doğrulanır |
| `F0-EV-012` | Volume ve restore | DB/app-files/key ring/backup volume sınırları, checksum ve boş ortama restore smoke kanıtlanır |
| `F0-EV-013` | Kapsam koruması | Git diff yalnız onaylı F0 dokümanlarını gösterir; production dosyası, migration veya placeholder yoktur |
| `F0-EV-014` | Faz sonu raporu | Değişen dosyalar, komutlar, testler, riskler, ADR'ler ve `PASSED/BLOCKED/FAILED` kararı gerçek sonuçlarla yazılır |

Bu ilk görev tamamlandığında `git status --short` ve dosya listesiyle yalnız `docs/implementation/F0-plan.md` oluşturulduğu; zorunlu başlıklar ve kimliklerin metin aramasıyla bulunduğu doğrulanacaktır.

## Dış bağımlılıklar ve güvenli fallback'ler

| Bağımlılık | Gerektiği kapı | Gelene kadar güvenli davranış | Engel durumu |
| --- | --- | --- | --- |
| Canonical kök şartname kopyası | Kaynak bütünlüğü | Kök kopya sağlanan kaynakla aynı SHA-256 ve sayfa sayısında tutulur | CLOSED |
| Hedef Windows VPS erişimi ve sağlayıcı desteği | F0 runtime/volume/backup çıkışı | Yerel ortam yalnız keşif bilgisi sayılır; native Windows container'a geçilmez | F0 çıkışı için blocker |
| Gerçek ürün/sipariş hacmi ve pik bilgisi | Kapasite onayı | Kullanıcı baz değeri: `1.000` ürün, `15.000` sipariş/yıl; x5: `5.000` ürün, `75.000` sipariş/yıl | CLOSED; ikincil metrikler izlenecek |
| RPO/RTO ve `BACKUP_PROFILE` kararı | F0 recovery kararı | Varsayılan `PILOT_LOCAL`; aynı diskse `RISK-DR-001`; restore smoke zorunlu | F0 çıkışı için blocker |
| Off-host backup hesabı | `PRODUCTION_RESILIENT` kapısı | Yalnız seçilen profilde beklenir; credential uydurulmaz | Profile bağlı blocker |
| Platform credential/test hesabı | Adapter sandbox/SIT ve gerçek capability | Fake adapter + anonim fixture; capability `UNKNOWN`; write kapalı | F1 local için engel değil, dış read/write için blocker |
| Trendyol E-Faturam test firması | F4 E2E | Provider fake/contract; `AUTO_INVOICE_ENABLED=false` | F4 submit blocker'ı |
| Production domain/DNS | Caddy TLS ve canlı erişim | Localhost/development config | F0/F1 local için engel değil |
| Mali müşavir ve KVKK/mali retention kararı | Otomatik mali işlem ve production yaşam döngüsü | Minimum data, mask, hard-delete yok, otomatik fatura kapalı | İlgili production kapısı için blocker |
| Stitch arayüz dosyası | Markalı nihai görsel fidelity | İşlevsel, responsive ve erişilebilir varsayılan panel | Engelleyici değil |
| Kargo/etiket format tercihi | İlk shipment write | Yalnız kanıtlanmış capability; unsupported çağrı yok | İleri faz blocker'ı |
| Ek kullanıcı / ikinci tenant kararı | F7B / F8 | Tek Owner korunur; kullanıcı/rol/tenant ekranı üretilmez | F7B/F8 tamamen kapsam dışı |

## Riskler ve blocker'lar

| Kimlik | Tür | Risk / blocker | Etki | Güvenli davranış / sonraki adım |
| --- | --- | --- | --- | --- |
| `RISK-SPEC-001` | Kaynak | Canonical PDF repository kökünde değildi. | Kaynak taşınabilirliği/yanlış kopya riski vardı. | Aynı SHA-256 ve 73 sayfa doğrulanarak canonical kök dosya oluşturuldu; CLOSED. |
| `BLOCK-HOST-001` | Dış ortam | VPS daha sonra kiralanacak; hedef erişim, özellikler ve Linux container production desteği henüz bilinmiyor. | `F0-REQ-009` ve `F0-EXIT-003` kanıtlanamaz. | Kiralama sonrası hedef hostta doğrulama runbook'unu çalıştır; destek yoksa kullanıcıdan dağıtım ortamı kararı iste. |
| `BLOCK-CAPACITY-001` | İş girdisi | Başlangıçta gerçek hacim ve pik değerleri yoktu. | Kapasite onayı gerçek veriye dayandırılamıyordu. | Kullanıcı `1.000` ürün ve `15.000` sipariş/yıl sağladı; x5 profil kaydedildi. CLOSED. |
| `BLOCK-DR-001` | Operasyon | `PILOT_LOCAL` ve en fazla 6 saat pilot RPO tanımlı; hedef volume, gerçek restore ve ölçülmüş RTO kanıtı yok. | Recovery uygulanabilirliği ve F0 çıkışı tamamlanamaz. | Aynı fiziksel diskse `RISK-DR-001`; hedefte checksum ve restore smoke olmadan geçme. Off-host yalnız resilient profilde zorunludur. |
| `BLOCK-VERSION-001` | Faz sınırı | F0 çıkışı lockfile/image digest tutarlılığı isterken production lock'ları F1 teslimatıdır. | Faz sırası gerilimi vardı. | Kullanıcı onayıyla yalnız `docs/dependencies/verification/` altında F0 lock/digest kanıtı üretildi; baseline commit `00c7b78591f158babb040070bf0aa0f04acace8e`. CLOSED. |
| `RISK-CAP-001` | Dış platform | Platform credential/test hesabı ve anonim fixture sağlanmadı. | Hiçbir platform capability'si `SUPPORTED` olarak kanıtlanamaz. | Capability'leri `UNKNOWN`, write flag'lerini kapalı tut; Fake adapter standardıyla F1 local hazırlığını sürdür. |
| `RISK-SUPPLY-001` | Tedarik zinciri | F1 production lock/image'ları henüz yok. | Aktarımda resolved tree veya image drift riski. | F0 lock ve index digest kanıtı tamamlandı; F1 aktarımı fail-closed karşılaştırılacak. MITIGATED_F0. |
| `DEP-STITCH-001` | Tasarım | Stitch dosyası daha sonra sağlanacak. | Marka fidelity doğrulaması ertelenir. | İşlevsel ve erişilebilir varsayılan UI planı ileride devam eder; F0/F1'i durdurmaz. |

## ADR gerektiren karar kayıtları

ADR-001 ile ADR-010 yeni seçenek seçmek için değil, şartnamenin yürürlükteki kararlarını, güvenli varsayılanlarını ve değiştirme kapılarını kalıcılaştırmak için oluşturulmuştur.

| ADR | Kaydedilecek bağlayıcı karar | Yalnız izin verilen değiştirme kapısı |
| --- | --- | --- |
| `ADR-001` | Clean Architecture sınırlarında modüler monolit; ayrı API ve Worker; aynı Domain/Application ve PostgreSQL | Ölçülmüş ölçek ihtiyacı ve kullanıcı onayı |
| `ADR-002` | Tek işletme, tek Owner, tek aktif tenant; tenant kapsamlı iskelet; aktif multi-tenant yok | F7B ve F8 için ayrı açık kullanıcı kararı |
| `ADR-003` | PostgreSQL operasyonel gerçek kaynak; tek AppDbContext ve tek migration zinciri | Şema/aggregate/tekillik değişiminde ayrı ADR ve migration incelemesi |
| `ADR-004` | PostgreSQL dayanıklı job queue, Inbox, idempotency ve reconciliation; ikinci broker yok | Ölçülmüş darboğaz ve onaylı ADR |
| `ADR-005` | Platform farkları adapter'da; capability kanıtı zorunlu; kanıtsız davranış `UNKNOWN` | Güncel resmi kaynak ve test/fixture kanıtı |
| `ADR-006` | Merkezi StockLedger/fiyat, tek `MAIN`, safety stock `0`, yalnız `PASS` restock, otomatik fatura kapalı | İş sahibi veya mali onay ve ilgili test kanıtı |
| `ADR-007` | Secret koruması, `_FILE` kaynakları, persistent Data Protection key ring, tek Owner bootstrap ve TOTP başlangıçta kapalı | Şartnamenin güvenlik kapılarını azaltmayan yazılı karar |
| `ADR-008` | Yerel private volume ve `IFileStorage`; tenant kapsamlı göreli yollar; public kalıcı dosya yok | Kapasite/DR gereğiyle depolama adapter kararı |
| `ADR-009` | Docker Compose ile Caddy/API/Worker/PostgreSQL/backup; hedef Windows VPS'te Linux container | Host güvenilir çalıştıramazsa kullanıcı dağıtım ortamı kararı; sessiz Windows container dönüşümü yok |
| `ADR-010` | Profil bazlı backup, checksum, restore smoke, RPO/RTO ve gerektiğinde off-host hedef | RPO 6 saat yetersizse WAL archiving için ayrı ADR |

## Açık kararlar

| Kimlik | Açık karar | Varsayılan / karar gelene kadar davranış | F0 etkisi |
| --- | --- | --- | --- |
| `OPEN-F0-001` | Sağlanan PDF'nin canonical kök kopyasıyla hash eşitliği | Her iki kopya 73 sayfa ve SHA-256 `E98365DC34804A478D5DBB41E1997FB6742FD0723A76C08CEE138321F0E2ECA3`; karar kapandı. | CLOSED |
| `OPEN-F0-002` | F0 lock/digest çıkış kriteri ile F1 lockfile teslimatı arasındaki faz sınırı | Kullanıcı onayıyla non-production F0 verification lock/digest seti oluşturuldu ve commit edildi; F1 scaffold'u üretilmedi. | CLOSED |
| `OPEN-F0-003` | Hedef Windows VPS sağlayıcısı, sürümü, sanallaştırma/runtime desteği ve erişim yöntemi | VPS daha sonra kiralanacak; hedef kanıt gelene kadar yerel host production kanıtı sayılmaz. | F0 çıkış blocker'ı |
| `OPEN-F0-004` | Gerçek veri hacmi ve pik değerleri | `1.000` ürün ve `15.000` sipariş/yıl baz; x5 profil `5.000` ürün ve `75.000` sipariş/yıl. İkincil metrikler F1+ izlenir. | CLOSED_FOR_F0 |
| `OPEN-F0-005` | Hedef volume, restore süresi ve RTO | Profil `PILOT_LOCAL`, pilot RPO en fazla 6 saat; off-host bu profilde zorunlu değildir. Hedef restore ölçülmeden RTO uydurulmaz. | F0 çıkış blocker'ı |
| `OPEN-F0-006` | Platform test hesabı/credential/fixture erişim durumu | Tümü `UNKNOWN`; Fake adapter; dış write kapalı. | F1 local için engel değil |

Güvenli varsayılanı şartnamede bulunan iş kararları açık karar sayılmaz; ADR-006'ya aynen kaydedilir ve uygulama sırasında ayrıca soru sorulmadan korunur.

## F0 çıkış değerlendirmesi

F0 ancak aşağıdaki koşulların tamamı gerçek kanıtla sağlandığında `PASSED` olabilir:

- Bütün F0 gereksinimleri faz ve kabul kriterine bağlıdır.
- Kanıtsız capability'ler `UNKNOWN` ve tüm dış write flag'leri kapalıdır.
- Fixture'lar secret/PII taramasından geçer.
- ADR-001-010 arasında ve şartnameyle çelişki yoktur.
- F1 yerel geliştirmesi gerçek platform secret'ı olmadan başlayabilir.
- Exact sürüm dosyası resmi kaynak ve immutable digest/lock kanıtıyla tamamlanmıştır.
- F1'i durduran mimari belirsizlik kalmamıştır.
- Dış bağımlılıkların her birinde blocker, güvenli fallback ve sonraki adım yazılıdır.
- Hedef runtime, volume ve backup/restore uygulanabilirliği kanıtlanmıştır.
- `docs/dependencies/verified-versions.md` commit edilmiş ve izin verilen sürüm artifact'leriyle tutarlıdır.

Mevcut değerlendirme:

- **Bu ilk planlama görevi: READY.**
- **F0 dokümantasyon uygulaması: COMPLETE.** İzlenebilirlik, capability, iş otoritesi, güvenlik/operasyon, dependency/sürüm kayıtları ve ADR-001–010 oluşturuldu.
- **F0 çıkış kapısı: BLOCKED_EXTERNAL.** `BLOCK-HOST-001` ve buna bağlı `BLOCK-DR-001`, VPS kiralanıp hedef runbook çalışana kadar kapatılamaz. `BLOCK-CAPACITY-001` ve `BLOCK-VERSION-001` kapanmıştır.
- Platform test hesaplarının yokluğu capability'leri `UNKNOWN` bırakır; şartnameye göre bu durum tek başına F1 local geliştirmesini engellemez.

## Sonraki güvenli adım

Production artefaktı oluşturulmamıştır. Sonraki adım VPS kiralandığında hedef runtime/volume/restore-RTO runbook'unu çalıştırmaktır. Kullanıcının ayrıca F1 faz başlatma talebi olmadan F1 production koduna geçilmez.
