# Ravencia MarketplaceHub — F3 Product Create Uygulama ve Doğrulama Raporu

**İnceleme tarihi:** 2026-08-05
**İncelenen dal:** `production-hardening-v7`
**Başlangıç HEAD:** `801f19d`
**Kapsam:** Trendyol Product Create application orkestrasyonu, güvenli yayın kapıları, durable job, batch sonucu, satır durumu, yayın durum API'si ve ana işleyiş belgesi uyumu

## 1. Sonuç özeti

Önceki durumda `ValidatePublicationAsync` eşleme kontrollerini yaptıktan sonra her koşulda `CAPABILITY_UNKNOWN` döndürüyor; adapter tarafındaki genel `UpsertAsync` ise application job/worker zincirine bağlanmıyordu. Bu nedenle panelden doğrulanmış bir Product Create işi üretilemiyordu.

Bu çalışma ile Product Create ayrı bir sözleşme ve durable iş akışına dönüştürüldü:

1. API, ürün ve bağlantı için yayın işi ister.
2. Application katmanı `PRODUCT_WRITE=SUPPORTED`, global dış-yazma anahtarı ve bağlantı dış-yazma anahtarını birlikte denetler.
3. Composer güncel kategori, marka, özellik ve değer eşlemelerini; listing profile, varyant kimliklerini, teklif, stok ve kalıcı HTTPS görselleri doğrular.
4. Deterministik Product Create payload'ı ve hash'i üretilir; aynı bağlantı, ürün ve payload için tek job korunur.
5. Worker `SUBMIT -> POLL` durum makinesiyle create isteğini gönderir ve `batchRequestId` sonucunu sorgular.
6. Batch satırları barkoda göre eşlenir; kabul/red sonucu varyant ve marketplace listing state üzerinde tutulur.
7. Batch kabulü doğrudan canlı yayın sayılmaz; tam kabul `APPROVAL_PENDING` olur.
8. Belirsiz dış etki durumunda otomatik ikinci create yerine `MANUAL_REVIEW` uygulanır.

**Production kararı değişmemiştir: `BLOCKED`.** Kod ve belgeler güncellenmiştir; exact .NET/PostgreSQL, Docker ve gerçek Trendyol Stage doğrulaması üretilmeden faz kapanmış sayılmaz.

## 2. Uygulanan teknik kapsam

| Alan | Son durum |
| --- | --- |
| Product port | Genel `UpsertAsync` kaldırıldı; yalnız Product Create anlamına gelen `CreateAsync` eklendi. Update ve archive ayrı sözleşme olarak kalır. |
| API | `POST /api/v1/products/{id}/publication-jobs` ve `GET /api/v1/products/{id}/publication-status/{connectionId}` eklendi. |
| Kalıcı ürün görseli | `POST /api/v1/files/product-media-url` ile tenant/ürün/varyant doğrulamalı, HTTPS ve yerel/özel literal IP engelli URL kaydı eklendi. |
| Güvenli enqueue | ACTIVE Trendyol bağlantısı, `PRODUCT_WRITE=SUPPORTED`, iki write switch, doğrulanmış payload ve repeatable-read/dedup sınırı zorunlu. |
| Payload composer | En fazla 1000 varyant; barkod/SKU/model kodu, fiyat/KDV, MAIN stok, güvenli medya, kategori/marka/özellik/değer eşlemeleri ve zorunlu özellikler denetlenir. |
| Uzak yürütme | Product Create endpoint'i kullanılır; dönen batch kimliği job payload'ına yazılır. |
| External-effect fence | Uzak çağrıdan önce kalıcı etki kaydı oluşturulur. Ağ/5xx/contract belirsizliğinde aynı create otomatik tekrar edilmez. |
| Batch polling | `IN_PROGRESS` retry; `COMPLETED` satır değerlendirmesi; tanınmayan/eksik/yinelenen/bilinmeyen barkod sonucu manuel inceleme. |
| Satır durumu | `CREATE_ACCEPTED`, `CREATE_REJECTED`, `PARTIAL_FAILURE`, `APPROVAL_PENDING` ve red kodları yerel listing kayıtlarına yazılır. |
| Replay | Aynı payload için aktif veya terminal mevcut job döndürülür; ikinci dış etki oluşturulmaz. |
| Test kapsamı | PostgreSQL başarı/replay ve partial-batch senaryoları ile batch fixture barkod/hata sözleşmesi kodlandı. |

## 3. Güvenlik ve veri bütünlüğü kararları

- Read capability, write yetkisi sayılmaz. Product Create yalnız açıkça `SUPPORTED` Product Write capability ile açılır.
- Global ve bağlantı bazlı dış-yazma anahtarlarından biri kapalıysa job oluşmaz.
- Private upload dosyasının container içindeki yolu marketplace görsel URL'si kabul edilmez.
- Görsel URL'sinde yalnız HTTPS, kullanıcı bilgisi içermeyen ve literal olarak loopback/özel ağ olmayan adres kabul edilir.
- Create çağrısının uzak tarafta uygulanıp uygulanmadığı belirsizse tekrar gönderim yerine operatör incelemesi gerekir.
- Batch içindeki tüm barkodlar beklenen yerel satırlarla bire bir eşleşmeden başarı kaydedilmez.
- Batch `SUCCESS`, ürünün Trendyol'da canlı olduğunu kanıtlamaz; onay reconciliation ayrı adımdır.

## 4. Kodlanan doğrulama senaryoları

| Senaryo | Beklenen sonuç |
| --- | --- |
| Geçerli tek varyant | Deterministik payload, quantity = available - safety stock, nested delivery option, tek job ve tek dış etki |
| Aynı payload replay | İlk job kimliği döner; yeni job ve yeni dış çağrı oluşmaz |
| Terminal job sonrası replay | Aynı payload hash'i için mevcut terminal job döner |
| Tam batch kabulü | Varyant `CREATE_ACCEPTED`, profile `APPROVAL_PENDING` |
| Kısmi batch | Kabul edilen ve reddedilen varyantlar ayrı kaydedilir; profile `PARTIAL_FAILURE` ve job blocked olur |
| Eksik/yinelenen/bilinmeyen barkod | `MANUAL_REVIEW` |
| Dört saat içinde tamamlanmayan batch | `PRODUCT_BATCH_RESULT_EXPIRED` ve `MANUAL_REVIEW` |
| Güncel olmayan/pasif özellik değeri | Payload oluşturulmaz |

## 5. Doğrulama durumu

| Kontrol | Sonuç | Sınır |
| --- | --- | --- |
| Tüm C# kaynaklarda lexical denge | `PASS_STATIC` | 140 kaynak dosya tarandı; compiler/typecheck yerine geçmez. |
| JSON parse | `PASS_STATIC` | 33 kaynak JSON dosyası. |
| YAML parse | `PASS_STATIC` | 7 YAML/YML dosyası. |
| XML/MSBuild parse | `PASS_STATIC` | 15 proje/props/targets/XML dosyası. |
| Shell syntax | `PASS_STATIC` | 5 shell dosyası `bash -n`. |
| Python syntax | `PASS_STATIC` | 2 script; üretilen cache temizlendi. |
| Repository cleanliness | `PASS` | Yasak build/cache/secret çıktısı bulunmadı. |
| Dokümantasyon transaction | `PASS` | Plan, current phase, status, evidence ve changelog birlikte güncellendi. |
| Git whitespace | `PASS` | `git diff --check`. |
| `.NET restore/build/test/format` | `BLOCKED_ENVIRONMENT` | Projenin sabitlediği .NET SDK `10.0.302` ortamda kurulu değildir. |
| PostgreSQL/Testcontainers | `BLOCKED_ENVIRONMENT` | Docker CLI/daemon yoktur. |
| Frontend exact typecheck/Vitest/Playwright | `BLOCKED_ENVIRONMENT` | Ortam Node `22.16.0`/npm `10.9.2`; proje Node `24.18.1`/npm `11.12.1` ister. `npm ci --offline` engine uyumsuzlukları ve cache içinde `zod@4.4.3` bulunmaması (`ENOTCACHED`) nedeniyle tamamlanmadı; `node_modules` temizlendi. |
| Trendyol Stage Product Create | `BLOCKED_EXTERNAL` | Stage credential, kontrollü test barkodu/SKU, açık operasyon onayı ve rollback gerekir. |

## 6. Açık kalan zorunlu işler

1. Exact .NET 10.0.302 ve PostgreSQL/Testcontainers ortamında tüm solution testlerini çalıştırmak.
2. Product Create API/Worker/publication-status akışını gerçek browser senaryosunda göstermek.
3. Batch kabulünden sonra approved-products read-back ile `APPROVAL_PENDING -> LIVE/REJECTED` reconciliation eklemek.
4. Product Update ve uzak archive komutlarını create sözleşmesinden ayrı uygulamak.
5. Fiyat ve stoku Trendyol'un birleşik uzak komutuyla yürütmek.
6. Gerçek Stage safe-write, satır hatası ve rollback kanıtını kaydetmek.
7. Production pilotundan önce Docker smoke, backup/restore ve read-only kabul kapılarını geçirmek.

## 7. Son karar

Product Create için önceki application boşluğu kaynak kodu ve dokümantasyon düzeyinde kapatılmıştır. Akış capability/write-switch kapıları, deterministik payload, durable job, external-effect fence, batch polling ve varyant bazlı sonuç kaydıyla fail-closed çalışacak şekilde tasarlanmıştır.

Bununla birlikte dinamik toolchain ve Stage kanıtı bulunmadığından sonuç **`CODED_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED`** olarak tutulur. Production açma kararı verilmemiştir.
