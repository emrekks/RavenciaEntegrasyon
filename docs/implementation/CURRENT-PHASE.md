# Güncel Faz ve Devralma Durumu

**Son güncelleme:** 2026-08-04  \n**Ana plan sürümü:** 5.0  \n**Makine durum kaydı:** `PROJECT-STATUS.yaml`  
**Aktif ürün kapsamı:** `TRENDYOL` + `TRENDYOL_EFATURAM`  
**Genel durum:** `F3_CLOSURE_ACTIVE / F4_IN_PROGRESS / PRODUCTION_BLOCKED`

## Faz özeti

| Faz | Durum | Açıklama |
| --- | --- | --- |
| F0 | `BASELINE_COMPLETE` | Mimari, bağımlılık, risk ve doğrulama temeli oluşturuldu. |
| F1 | `READY_LOCAL` | Kimlik, güvenlik, persistence, job altyapısı ve yerel deployment çekirdeği hazır. Production dış kanıtları eksik. |
| F2 | `READY_LOCAL` | Ürün, katalog, import, stok ve fiyat yerel çekirdeği hazır. |
| F3 | `ACTIVE_CLOSURE` | Trendyol read/reference omurgası var; ürün yayınlama orkestrasyonu, create/update ayrımı, birleşik stok-fiyat ve Stage safe-write eksik. |
| F4 | `IN_PROGRESS_BLOCKED_EXTERNAL` | E-Faturam sign-in/submit/PDF URL ve Trendyol link gönderimi kısmen var; taxpayer/status/cancel, güvenli PDF indirme ve gerçek E2E eksik. |
| F5 | `PLANNED_BLOCKED_BY_F3_F4` | Production pilot; F3 ve F4 çıkış kapıları geçilmeden başlamaz. |
| F6 | `PLANNED` | Stabilizasyon, operasyon kabulü, restore drill ve 30 günlük pilot gözlemi. |
| F7 | `PLANNED` | Platform adapter registry/resolver ve yeni platform ekleme standardının sertleştirilmesi. |
| F8+ | `PLANNED_NOT_ACTIVE` | Diğer platformlar ayrı ADR ve fazlarla tek tek eklenir; şu anda adapter geliştirilmez. |

## Codex'in devam edeceği sıra

1. F3 ürün yayınlama application akışını durable job, batch polling ve satır sonucu kaydıyla tamamla.
2. `UpsertAsync` yerine create/update/archive sözleşmelerini ayır.
3. Trendyol stok ve fiyatı tek `price-and-inventory` komutu olarak uygula.
4. Sipariş/paket/iade read akışlarını gerçek Stage fixture ve idempotency testleriyle kapat.
5. F4 taxpayer query, invoice status polling ve cancellation akışlarını ekle.
6. E-Faturam PDF URL'sine HTTPS + exact-host allow-list + redirect/IP kontrolü + `%PDF-` ve boyut doğrulaması uygula.
7. Trendyol fatura link durumunu `SUBMITTED -> CONFIRMED` reconciliation modeliyle tamamla.
8. Stage E2E, backup/restore ve production read-only smoke kanıtlarını kaydet.
9. F3 ve F4 kapandıktan sonra F5 production pilotunu, F6 stabilizasyonu ve F7 adapter registry hazırlığını yürüt.
10. Yeni platformu ancak F7 çıkış kapısından sonra ayrı ADR ile aç.

## Mevcut test görünürlüğü

Test kaynakları silinmemiştir. Solution içinde altı .NET test projesi vardır:

- Domain unit tests
- Application tests
- Persistence integration tests
- API integration tests
- Adapter contract tests
- End-to-end tests

Web tarafında Vitest/Testing Library testleri de `src/MarketplaceHub.Web/src/*.test.tsx` altında bulunur. CI, backend ve web testlerini image yayınından önce çalıştırır.

Silinenler yalnız üretilmiş test sonuçları, cache, `bin/obj`, `node_modules`, `dist` ve geçmiş platformlara ait artık testlerdir. Bunlar test kaynağı değildir.

## Production blockerları

- Gerçek Trendyol Stage credential ve güvenli write kanıtı
- E-Faturam test firma/company/user scope ve mali iş kuralı onayı
- Taxpayer/status/cancel ve güvenli PDF indirme
- Off-host backup ve temiz hedef restore/smoke
- GitHub Actions üzerinden başarılı tam build/test ve immutable image digest'leri

## Test ve belge çalışma kuralı

- Küçük geliştirme döngüsünde hedefli testler çalıştırılır; faz/release kapısında tam suite zorunludur.
- Test çıktısı evidence dosyasına kaydedilir; yalnız özet ve hata bağlamı Codex konuşmasına taşınır.
- Durum değiştiren kod commitleri `PROJECT-STATUS.yaml`, bu dosya, ilgili evidence log ve `docs/CHANGELOG.md` dosyalarını birlikte günceller.
- Ana geliştirme repository'sinde Git geçmişi korunur; `.git` yalnız temiz release paketinden çıkarılır.

## Kaynak önceliği

Çelişki halinde aşağıdaki sıra kullanılır:

1. `docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md` (ana plan, kullanıcı işleyişi, kapsam ve mimari)
2. `docs/implementation/PROJECT-STATUS.yaml` (makinece okunabilir durum)
3. Bu dosya (anlık faz ve blokaj)
4. `docs/specification/current-scope.md`
5. `docs/adr/ADR-016-trendyol-efaturam-only-until-complete.md`
6. capability matrix
7. ilgili faz planı ve evidence log
8. `docs/CHANGELOG.md`
9. tarihsel F0-F2 belgeleri

Tarihsel belgelerde geçen eski platform veya commit hash'i aktif kapsamı değiştirmez.
