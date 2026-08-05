# Güncel Faz ve Devralma Durumu

**Son güncelleme:** 2026-08-05
**Ana plan sürümü:** 6.2
**Makine durum kaydı:** `PROJECT-STATUS.yaml`
**Aktif ürün kapsamı:** `TRENDYOL` + `TRENDYOL_EFATURAM`
**Genel durum:** `F3_CLOSURE_ACTIVE / F4_IN_PROGRESS / PRODUCTION_BLOCKED`

## Faz özeti

| Faz | Durum | Açıklama |
| --- | --- | --- |
| F0 | `BASELINE_COMPLETE` | Mimari, bağımlılık, risk ve doğrulama temeli oluşturuldu. |
| F1 | `HARDENING_CODED_DYNAMIC_REVALIDATION_REQUIRED` | Job retry/takip, rol sınırı, MFA yeniden doğrulama, webhook/PDF güvenliği, scheduler, CI ve deployment sağlık kapıları kodlandı; tam .NET/npm/Docker doğrulaması bekliyor. |
| F2 | `READY_LOCAL` | Ürün, katalog, import, stok ve fiyat yerel çekirdeği hazır. |
| F3 | `ACTIVE_CLOSURE` | Trendyol read/reference omurgası ile Product Create durable job, batch polling ve satır sonucu kaydı kodlandı; exact dinamik doğrulama, create sonrası onay reconciliation, update/archive, birleşik stok-fiyat ve Stage safe-write eksik. |
| F4 | `IN_PROGRESS_BLOCKED_EXTERNAL` | E-Faturam sign-in/submit/PDF URL ve güvenli PDF indirme kodu var; taxpayer/status/cancel ve gerçek E2E eksik. Trendyol fatura linki `SUBMITTED` sonrası otomatik kesin teyit endpoint'i doğrulanmadığı için manuel incelemeye düşer. |
| F5 | `PLANNED_BLOCKED_BY_F3_F4_AND_REVALIDATION` | Production pilot; F3/F4 çıkış kapıları ve tam dinamik doğrulama geçilmeden başlamaz. |
| F6 | `PLANNED` | Stabilizasyon, operasyon kabulü, restore drill ve 30 günlük pilot gözlemi. |
| F7 | `PLANNED` | Platform adapter registry/resolver ve yeni platform ekleme standardının sertleştirilmesi. |
| F8+ | `PLANNED_NOT_ACTIVE` | Diğer platformlar ayrı ADR ve fazlarla tek tek eklenir; şu anda adapter geliştirilmez. |

## Production sertleştirme v7 kapsamı

Aşağıdaki sorunlar kaynak kodunda düzeltilmiştir; fakat exact toolchain ve gerçek servis testleri geçmeden `PASS` veya `PRODUCTION_READY` sayılmaz:

- Geçici job hataları backoff ile `RETRY_SCHEDULED`, kalıcı hatalar `BLOCKED`, belirsiz mali sonuçlar `MANUAL_REVIEW` olur.
- Job liste/ayrıntı/retry/cancel API'leri ve panelde İşlem Takibi ekranı vardır.
- Webhook gerçek byte sınırı, rate limit ve gizli route log redaction uygulanır.
- E-Faturam PDF indirme exact HTTPS host, public IP, sınırlı redirect, MIME, boyut ve `%PDF-` kontrolüyle sınırlandırılır.
- Fatura linki HTTP 2xx sonrası doğrudan tamamlanmaz; `SUBMITTED` ve teyit/manual-review modeli kullanılır.
- Bootstrap parolası yalnız one-shot bootstrap servisine verilir ve başarılı kurulumdan sonra kaldırılır.
- Worker heartbeat, frontend asset smoke ve API readiness birlikte deployment kapısıdır.
- CSRF token yenileme, idempotency retention, MFA reauthentication ve rol bazlı yazma yetkileri uygulanır.
- Periyodik sipariş/iade/reference job üreticisi eklenmiştir.
- Pull request ve ana dal pushlarında verify workflow'u tanımlanmıştır; workflow'un gerçek başarılı koşusu henüz kanıt değildir.
- F3 frontend regression kapsamı güncellendi: Vitest artık kategori kapsamı -> özellik -> özellik değeri zincirini ve iki mapping payload'ını; Playwright ise güncel operasyon/ayar menülerini, rol bazlı Faturalama görünürlüğünü ve gerçek route üzerinde kategori-kapsamlı özellik/değer mapping zincirini doğrular. Exact Node/npm kurulamadığı için sonuç `DYNAMIC_NOT_RUN / BLOCKED_ENVIRONMENT` olarak kalır.
- Trendyol Product Create akışı `PRODUCT_WRITE=SUPPORTED`, global ve bağlantı bazlı dış yazma anahtarları, güncel doğrulanmış eşlemeler, teklif/stok ve kalıcı HTTPS görsel URL kontrollerinden sonra durable job üretir; worker `SUBMIT -> POLL` durum makinesiyle batch sonucunu ve varyant satırlarını kaydeder. Başarılı batch sonucu doğrudan yayında sayılmaz, `APPROVAL_PENDING` olur.
- Ürün için Trendyol tarafından erişilebilir kalıcı HTTPS görsel URL kaydı `/api/v1/files/product-media-url` üzerinden yapılabilir; private upload dosyası doğrudan marketplace URL'si sayılmaz.

## Codex'in devam edeceği sıra

1. v7 ve Product Create değişikliklerini exact .NET `10.0.302`, Node `24.18.1`, npm `11.12.1` ve Docker Compose ortamında restore/build/test/typecheck/smoke ile doğrula.
2. Product Create için kodlanan başarı, replay ve partial-batch testlerini PostgreSQL üzerinde çalıştır; API/Worker status akışını Playwright ile görünür hale getir.
3. Create batch kabulünden sonra approved-products read-back ile `APPROVAL_PENDING -> LIVE/REJECTED` reconciliation ekle.
4. `ProductUpdate` ve uzak archive komutlarını create sözleşmesinden ayrı uygula.
5. Trendyol stok ve fiyatı tek `price-and-inventory` komutu olarak uygula.
6. Sipariş/paket/iade read akışlarını gerçek Stage fixture ve idempotency testleriyle kapat.
7. F4 taxpayer query, invoice status polling ve cancellation akışlarını ekle.
8. E-Faturam güvenli PDF indiricisini gerçek izinli host ve Stage PDF ile doğrula.
9. Trendyol fatura linki için resmî teyit/read-back imkânı doğrulanırsa `SUBMITTED -> CONFIRMED` reconciliation ekle; aksi halde manuel operasyon prosedürünü kanıtla.
10. Stage E2E, backup/restore ve production read-only smoke kanıtlarını kaydet.
11. F3 ve F4 kapandıktan sonra F5 production pilotunu, F6 stabilizasyonu ve F7 adapter registry hazırlığını yürüt.
12. Yeni platformu ancak F7 çıkış kapısından sonra ayrı ADR ile aç.

## Test görünürlüğü ve başarı kuralı

Test kaynakları silinmemiştir. Solution içinde Domain, Application, Persistence, API, Adapter Contract ve End-to-End test projeleri bulunur. Web tarafında Vitest/Testing Library testleri `src/MarketplaceHub.Web/src/*.test.tsx` altındadır.

- Hedefli test, geliştirme döngüsünde değiştirilmiş alanı hızlı doğrular.
- Faz/release kapısında bütün backend ve web testleri zorunludur.
- Çalıştırılmayan test `NOT_RUN`, araç/registry engeli `BLOCKED_ENVIRONMENT` olarak yazılır; başarılı sayılmaz.
- Başarılı testin özet ve komutu evidence loguna girer; gereksiz ayrıntılı çıktı konuşma bağlamına taşınmaz.
- PR verify workflow'u tanımlanmıştır; bu commit için gerçek GitHub Actions sonucu görülmeden CI kanıtı `PASS` değildir.

## Production blockerları

- v7 değişikliklerinin exact toolchain ile başarılı restore/build/test/typecheck/Vitest/Playwright sonucu
- Docker Compose config, bootstrap, Worker heartbeat ve frontend/API smoke doğrulaması
- Product Create için exact .NET/PostgreSQL test sonucu, gerçek Trendyol Stage credential ve güvenli write kanıtı
- Create sonrası approved-products reconciliation; ProductUpdate/archive ve birleşik stok-fiyat akışları
- E-Faturam test firma/company/user scope, taxpayer/status/cancel ve mali iş kuralı onayı
- Güvenli PDF indirme için gerçek Stage host/PDF kanıtı
- Trendyol fatura linki kesin teyit veya onaylı manuel inceleme prosedürü
- Off-host backup ve temiz hedef restore/smoke
- Başarılı GitHub Actions verify/release koşuları ve immutable image digest'leri

## Test ve belge çalışma kuralı

- Durum değiştiren kod commitleri `PROJECT-STATUS.yaml`, bu dosya, ilgili evidence log ve `docs/CHANGELOG.md` dosyalarını birlikte günceller.
- `verify-documentation-transaction.py --base <merge-base>` kod/deploy/test değişikliklerinde bu işlemi zorunlu tutar.
- Ana geliştirme repository'sinde Git geçmişi korunur; `.git` yalnız temiz release paketinden çıkarılır.
- Evidence logları geçmişteki doğrulamaları korur; yeni kod değişikliği eski PASS kaydını otomatik olarak güncel kod için PASS yapmaz.

## Kaynak önceliği

Çelişki halinde aşağıdaki sıra kullanılır:

1. `docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md`
2. `docs/implementation/PROJECT-STATUS.yaml`
3. Bu dosya
4. `docs/specification/current-scope.md`
5. `docs/adr/ADR-016-trendyol-efaturam-only-until-complete.md`
6. capability matrix
7. ilgili faz planı ve evidence log
8. `docs/CHANGELOG.md`
9. tarihsel F0-F2 belgeleri
