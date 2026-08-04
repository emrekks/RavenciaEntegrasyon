# Ravencia MarketplaceHub — Baştan Sona Proje İncelemesi

**İnceleme tarihi:** 2026-08-04  
**İncelenen girdi:** `V3-Ravencia-Entegrasyon(1).rar`  
**Hedef kapsam:** Yalnız Trendyol + Trendyol E-Faturam

## 1. Yönetici özeti

Projenin temel mimarisi iyi bir yönde kurulmuş: Domain bağımsız, adapter sınırları ayrılmış, PostgreSQL tabanlı job lease/idempotency kullanılıyor, dış yazmalar varsayılan kapalı, secret’lar dosya tabanlı yükleniyor ve production image’ları digest ile sabitleniyor.

Buna karşın arşiv ve aktif kapsam temiz değildi. 250 MB’lık arşivde yaklaşık 15.635 kayıt vardı; gerçek kaynak/yapılandırma/dokümantasyon niteliğindeki dosya sayısı yaklaşık 285’ti. `.git`, `bin/obj`, frontend build/test çıktıları, geçici PDF görselleri ve yerel PostgreSQL data/WAL dizini kaynak teslimine karışmıştı. Kod, UI, Worker, test ve dokümanlar da eski platformları hâlâ aktif veya ertelenmiş kapsam olarak taşıyordu.

Temiz kopya **265 dosya / yaklaşık 4,4 MB** kaynak paketine indirildi. Eski adapterlar ve aktif faz dosyaları kaldırıldı; DI, Worker, connection service, webhook, reconciliation ve UI yalnız Trendyol/E-Faturam ile sınırlandı. Ancak sistem henüz “tam çalışır” değildir. En kritik eksikler Trendyol ürün yayınlama orkestrasyonu, birleşik stok-fiyat yazma, E-Faturam taxpayer/status/cancel ve gerçek Stage E2E kanıtlarıdır.

## 2. Yapılan temizlik ve güncellemeler

### 2.1 Kaynak paketi

Kaldırılan veya pakete alınmayan sınıflar:

- `.git` geçmişi
- `bin`, `obj`, `node_modules`, `dist`
- Playwright/Vitest/test sonuçları ve debug logları
- geçici PDF/PNG üretim çıktıları
- yerel PostgreSQL `pgdata` ve WAL
- arşiv dosyaları ve kök şartname PDF kopyaları
- eski platform adapter klasörleri
- eski platform contract testleri, runbook ve aktif faz plan/evidence dosyaları

Eklenen korumalar:

- `.gitignore` ve `.dockerignore` genişletildi.
- `scripts/verify-repository-cleanliness.py` eklendi; Git çalışma ağacı modu root `.git` dizinine izin verir, teslim paketi modu `--package` ile bunu da reddeder.
- CI, bağımlılık kurmadan önce temizlik denetimini çalıştırıyor.

### 2.2 Aktif kapsam

Aşağıdaki katmanlar yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM` kabul edecek şekilde güncellendi:

- `ActiveIntegrationScope`
- `F3ConnectionService`
- dependency injection adapter kayıtları
- Worker job dispatch
- sipariş/return sync servisleri
- webhook doğrulama ve ingest
- reconciliation policy
- integrations UI ve bağlantı formu
- unit, UI ve repository guard testleri

Eski platform adı taşıyan uygulanmış EF Core migration silinmedi. Bu dosya yalnız job retry şemasını taşır; migration kimliğini değiştirmek mevcut veritabanı geçmişini bozabilir.

### 2.3 Production çalıştırma

- Production `AllowedHosts` değeri artık açıkça verilmek zorunda.
- Deployment initializer, production HTTPS origininden `MARKETPLACEHUB_ALLOWED_HOSTS` değerini üretir; daha önce zorunlu değişkeni yazmadığı için deployment doğrulaması kendi ürettiği dosyayla başarısız olabilirdi.
- Base Compose, `MARKETPLACEHUB_SITE_ADDRESS` değişkenini gerçekten tüketir; `.env.example` yalnız Compose tarafından kullanılan operatör değişkenlerini içerir.
- Local ve production Caddy ayrımı doğrulandı: local `Caddyfile` internal CA kullanırken release image’ı ayrı `Caddyfile.production` ile public automatic TLS kullanır.
- API healthcheck, yalnız PID/proses kontrolü yerine `/health/ready` endpoint’ini çağırıyor.
- Readiness PostgreSQL health check’ine bağlı.

## 3. Güçlü yönler

### Mimari

- Domain projesinde paket/proje bağımlılığı yok.
- Application portları adapter uygulamasından ayrılmış.
- API ve Worker aynı persistence çekirdeğini kullanıyor ancak iş çalıştırması durable job üzerinden yürütülüyor.
- Tenant alanı veri modellerinde yaygın ve servis filtreleri mevcut.

### Güvenlik

- Credential değerleri Data Protection ile korunuyor ve maskeli hint dışında geri gösterilmiyor.
- File-backed secret sözleşmesi mevcut.
- Production’da Data Protection sertifikası zorunlu.
- Session, CSRF/request security, rate limit ve MFA altyapısı var.
- Dış yazmalar global + connection düzeyinde çift anahtarla kapalı.

### Dayanıklılık

- Job lease token, heartbeat ve fencing kontrolü var.
- Inbox/idempotency ve overlap cursor tasarımı duplicate riskini azaltıyor.
- Paket miktarı ve durum geçişi için fail-closed guardlar var.
- Immutable image ve digest-only deployment yaklaşımı doğru.

## 4. Sorunlar, etkiler ve çözüm önerileri

### P0 — Teslim arşivinde runtime verisi ve geçmiş

**Sorun:** `.git`, build çıktıları ve PostgreSQL data/WAL kaynak arşivindeydi.  
**Etki:** Gereksiz büyüklük; kişisel/iş verisi veya secret sızıntısı; başka makinede hatalı DB kopyası.  
**Çözüm:** Temiz kaynak-only paket, ignore kuralları, CI precheck. PostgreSQL yedeği yalnız `pg_dump` + private file/data-protection seti olarak ayrı şifreli backup paketinde tutulmalı.

### P0 — Aktif kapsamın kod ve dokümanda çelişmesi

**Sorun:** Eski platformlar adapter, DI, Worker, UI, test ve planlarda bulunuyordu.  
**Etki:** Operatör yanlış bağlantı oluşturabilir; dead code büyür; test yükü dağılır.  
**Çözüm:** ADR-016 ve yalnız iki platformlu active scope. Bu temiz kopyada uygulandı.

### P0 — Tamamlanmamış capability’nin hazır gibi algılanması

**Sorun:** Bazı adapter metotları var fakat gerçek dış kanıt veya application akışı yok.  
**Etki:** UI/operasyon “hazır” zannedip veri yazabilir.  
**Çözüm:** Capability `UNKNOWN` iken job oluşturma; UI’da “Kodlandı / Stage kanıtı yok / Desteklenmiyor” ayrımı; completion checklist.

### P1 — Trendyol ürün yayınlama orkestrasyonu eksik

**Bulgu:** `IProductPort.UpsertAsync` adapterda mevcut; fakat production application service/job/API/UI çağrısı bulunmuyor.  
**Etki:** Panelden Trendyol’a ürün yayınlama hedefi gerçekleştirilemiyor.  
**Çözüm:**

1. `ProductPublication` aggregate ve validation snapshot oluştur.
2. `TRENDYOL_PRODUCT_PUBLISH` durable job ekle.
3. Create/update komutlarını ayır.
4. Batch request ID’yi sakla ve poll job üret.
5. Satır bazlı hata/başarıyı ürün varyantına bağla.
6. İdempotency key’i tenant+connection+publication revision üzerinden üret.

### P1 — “Upsert” create endpoint’ine sabit

**Bulgu:** `UpsertAsync` her zaman product create endpoint’ine POST ediyor.  
**Etki:** Var olan ürün güncellemesi duplicate veya validation hatası üretebilir.  
**Çözüm:** Portu `CreateProductsAsync`, `UpdateProductsAsync`, `ArchiveProductsAsync` olarak ayır; capability ve payload validation ayrı olsun.

### P1 — Stok ve fiyat modeli uzak sözleşmeyle uyuşmuyor

**Bulgu:** Application portu `PushStockAsync` ve `PushPricesAsync` olarak ayrılmış; adapter ikisini de desteklenmiyor olarak kapatmış.  
**Etki:** Stok/fiyat eşitleme hiç çalışmıyor; ayrı retry yapılırsa uzak sistemde tutarsızlık olabilir.  
**Çözüm:** Tek `PriceInventoryBatchCommand` içinde barcode, quantity, salePrice, listPrice tut; aynı batch ve aynı idempotency kaydıyla gönder; partial batch sonucunu satır bazında işle.

### P1 — Trendyol paket ve iade yazmaları eksik

**Bulgu:** package action ve return action adapterları fail-closed unsupported.  
**Etki:** Panelden kargo/paket ve iade işlemi yapılamaz.  
**Çözüm:** Her aksiyon için ayrı capability, allowed status transition, exact endpoint fixture ve Stage safe-write testi. Genel “shipment write” tek başına yeterli olmamalı.

### P1 — Invoice link teslimi erken terminal başarı yazıyor

**Bulgu:** POST 2xx sonrası adapter `DELIVERED` döndürüyor; query endpoint uygulanmamış.  
**Etki:** Uzak sistem işlemi sonradan reddederse yerel kayıt yanlış terminal durumda kalabilir.  
**Çözüm:** İlk sonuç `SUBMITTED`; remote request ID sakla; doğrulanmış query/reconciliation veya idempotent read-back ile `CONFIRMED` yap.

### P1 — E-Faturam akışı yarım

**Bulgu:** sign-in, submit ve permanent URL var; taxpayer query, remote status ve cancel yok.  
**Etki:** Doğru belge türü ve terminal durum güvenilir belirlenemez; iptal operasyonu panelden yürütülemez.  
**Çözüm:** Test firma scope’u doğrulandıktan sonra sırasıyla taxpayer, status polling, PDF download/checksum ve cancel geliştirilmeli.

### P1 — Mali politika onayı eksik

**Sorun:** e-Fatura/e-Arşiv seçimi, rounding, due, adjustment ve iptal kuralları tam iş otoritesiyle onaylı değil.  
**Etki:** Teknik olarak başarılı ancak mali olarak yanlış belge.  
**Çözüm:** Koddan bağımsız, versiyonlu fiscal policy kaydı; onaysız policy ile auto-submit kapalı.


### P1 — Deployment environment sözleşmesi çelişkiliydi

**Bulgu:** Production initializer, `compose.production.yaml` tarafından zorunlu tutulan `MARKETPLACEHUB_ALLOWED_HOSTS` değerini üretmiyordu. `.env.example` içindeki bazı anahtarlar da Compose substitution sözleşmesiyle eşleşmiyordu. Production TLS tarafı ayrıca incelendi; release edge image’ının `Caddyfile.production` kullandığı ve `tls internal` içermediği doğrulandı.  
**Etki:** Initializer’ın ürettiği `production.env` ile deployment ön-kontrolü zorunlu değişken hatası verebilirdi; local `.env` ayarlarının bir bölümü etkisiz kalabilirdi.  
**Çözüm:** AllowedHosts production origin hostundan türetildi, site address Compose değişkenine bağlandı ve örnek environment dosyası çalışan sözleşmeye göre yenilendi.


### P1 — E-Faturam belge indirme URL’si güven sınırına alınmamış

**Bulgu:** Provider’ın döndürdüğü `permanentUrl` doğrudan HTTP client ile indiriliyor; URL için HTTPS/host allow-list ve indirilen byte’lar için `%PDF-` imza doğrulaması yapılmıyor.  
**Etki:** Capability yanlışlıkla açılır ve provider yanıtı bozulur/ele geçirilirse SSRF benzeri iç ağ erişimi veya PDF yerine hatalı içerik saklama riski oluşur.  
**Çözüm:** Stage kanıtından çıkarılmış exact host allow-list, HTTPS-only ve user-info/port kısıtları; özel ağ/IP reddi; redirect kapatma veya her redirect’i yeniden doğrulama; PDF magic-byte ve boyut kontrolü. Bu kapılar tamamlanana kadar `INVOICE_DOCUMENT_READ` `UNKNOWN` kalmalıdır.

### P1 — Fatura bağlantısının 8 yıllık erişim garantisi yok

**Bulgu:** Trendyol’a HTTPS fatura linki gönderiliyor; fakat URL’nin sekiz yıl erişilebilir kalacağını doğrulayan retention/availability kontrolü veya sahiplik sözleşmesi yok.  
**Etki:** Başlangıçta çalışan E-Faturam kalıcı URL’si daha sonra erişilemezse yasal/operasyonel teslim şartı ihlal edilebilir.  
**Çözüm:** Provider URL ömrünü yazılı doğrula; günlük/haftalık link probe ve alarm ekle; belgeyi private storage içinde checksum ile sakla; provider bağımlılığı kabul edilmiyorsa kontrollü, kalıcı ve yetkilendirilmiş belge gateway’i tasarla.

### P1 — Backup aynı host riskini çözmüyor

**Sorun:** Yerel backup staging aynı fiziksel hostta kalabilir.  
**Etki:** Disk/host kaybında hem veri hem yedek kaybolur.  
**Çözüm:** Şifreli off-host object storage/ikinci sunucu; periyodik temiz volume restore; RPO/RTO ölçümü.

### P2 — Gözlemlenebilirlik eksikleri

Önerilen minimum metrikler:

- queue depth ve oldest job age
- retry/dead-letter sayısı
- remote request duration/status/rate-limit
- capability probe sonucu
- order/reference sync watermark lag
- invoice terminal olmayan kayıt yaşı
- reconciliation mismatch sayısı

Secret, token, müşteri adresi ve ham mali payload metric/log etiketi olmamalıdır.

### P2 — Kod okunabilirliği

Bazı persistence ve adapter metotları çok uzun, çok sayıda ifadeyi aynı satırda taşıyor. Bu derlenebilir olsa bile hata ayıklama ve review maliyetini artırır. Job processor, order upsert ve connection service küçük use-case sınıflarına bölünmeli; formatter kuralı CI’da zorunlu kalmalı.

## 5. Önerilen geliştirme sırası

1. Temiz kapsam ve CI doğrulaması — tamamlandı.
2. Trendyol reference/product/order read gerçek Stage tekrar testi.
3. Product publish application job + create/update ayrımı.
4. Birleşik stok-fiyat yazma ve partial batch yönetimi.
5. Paket/iade capability’lerini tek tek tamamlama.
6. E-Faturam taxpayer + mali policy.
7. Invoice submit/status/PDF/cancel uçtan uca akışı.
8. Trendyol invoice-link submit/reconciliation.
9. Webhook + polling reconciliation.
10. Backup/restore, rate-limit, retry, rollback ve production smoke.

## 6. Test planı

### Trendyol

- geçerli/geçersiz credential
- seller scope uyuşmazlığı
- kategori/marka pagination
- yalnız leaf kategori seçimi
- zorunlu özellik/değer validation
- ürün create, update, duplicate barcode ve partial batch
- birleşik stok-fiyat, aynı payload replay ve timeout-after-success
- sipariş overlap, duplicate, out-of-order package event
- iade boş liste, 404, gerçek claim ve action validation
- invoice link duplicate ve erişilemeyen URL

### E-Faturam

- sign-in auth hata/rate-limit/timeout
- mükellef kayıtlı/kayıtsız
- e-Fatura/e-Arşiv seçim kuralı
- submit duplicate ve remote timeout sonrası reconciliation
- processing/success/rejected status
- PDF URL/download/checksum/private storage
- iptal izinli/izinsiz/terminal durum

### Operasyon

- Postgres kapalıyken readiness fail
- Worker lease kaybı ve fencing
- container restart sonrası volume kalıcılığı
- backup temiz volume restore
- production domain/AllowedHosts/TLS
- secret ve PII log taraması

## 7. Silinmemesi gereken dosyalar

- EF Core migration zinciri ve model snapshot
- package lock dosyaları
- resmi adapter contract fixture’ları
- deployment ve backup runbook’ları
- ADR-001–013 temel mimari kararları ve ADR-016

Migration dosyasının adı güncel kapsamla uyumsuz görünse bile uygulanmış veritabanında kimliktir; yeniden adlandırma/silme yapılmamalıdır.

## 8. Doğrulama sınırı

Bu inceleme ortamında .NET SDK ve Docker bulunmadığı için backend build/test ve container smoke burada çalıştırılamadı. Node runtime `22.16.0`, proje engine’i `24.18.1` olduğundan web doğrulaması uygun runtime ile yapılamadı. Ayrıca ortamın npm paket aynası `zod@4.4.3` tarball’ını `404` döndürdü; oluşan kısmi `node_modules` ve build cache temizlendi. JSON/XML/YAML parse, shell syntax, Python compile ve delivery-package cleanliness denetimleri geçti. Nihai kabul, repository’de pinlenmiş GitHub Actions üzerinde exact .NET/Node sürümleriyle restore, build, test, format ve immutable image adımlarının geçmesine bağlıdır.
