# Trendyol E-Faturam Provider-Managed Akış Son Kontrol Raporu

**Tarih:** 2026-08-05
**Durum:** `CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`

## Talep ve uygulanan karar

Panelde tekrar tutulan gönderen mali hesap alanları kaldırıldı. Aktif akış yalnız doğrudan E-Faturam hesap e-postası/parolası, Trendyol sipariş müşteri/fatura adresi snapshotı ve fatura satırlarını kullanır.

Belge türü kullanıcı ayarı değildir:

- `commercial=true` ve `invoiceAddress.eInvoiceAvailable=true` → `TEMELFATURA`
- diğer bütün siparişler → `EARSIVFATURA`

Ayrı mükellef sorgusu, Temel/Ticari senaryo seçimi, companyId, userId, prefix/seri, ödeme yöntemi ve manuel taşıyıcı tüzel kimlik ayarı kaldırıldı.

## Provider-managed mali kapsam

- `companyId` ve `userId`, başarılı `signIn` tokenından okunur.
- Prefix gönderilmez; E-Faturam hesabındaki varsayılan seri kullanılır.
- Eski connection settings verilerinde kalmış mali alanlar `20260805183000_SanitizeEfaturamProviderManagedSettings` migrasyonuyla silinir.
- Migration yalnız `ExternalWritesEnabled` değerini korur; silinen mali alanları geri kuran bir `Down` işlemi yoktur.
- Connection update, credential rotation ve active/disabled geçişi de ayar JSON'unu yeniden sadeleştirir.

## Ödeme ve taşıyıcı alanları

Bu alanlar kullanıcı ayarı ve panel formu olmaktan çıkarıldı. İnternet satışı E-Arşiv provider payloadında gereken teknik alanlar sistem tarafından otomatik üretilir:

- sipariş tarihi ve Trendyol satış bağlamından ödeme bilgisi;
- shipment package provider kodundan resmî Trendyol kargo kataloğu VKN/unvanı;
- eşleşmeyen taşıyıcıda tahmin yerine `EFATURAM_CARRIER_CATALOG_MISS`.

E-Fatura payloadında bu iki internet satışı alanı gönderilmez.

## Kaldırılan yüzeyler

- `/connections/{id}/efaturam-settings`
- `/billing/legal-entity-profile`
- `/billing/taxpayers/{taxId}`
- partner/customerSignIn credential alanları
- company/user/prefix/senaryo/carrier/payment/delivery command ve view sözleşmeleri
- ilgili frontend formları, fixturelar ve capability satırı

## Test ve doğrulama kapsamı

Kodlanan regresyonlar:

- yalnız e-posta/parola credential payloadı;
- panelde mali hesap alanlarının görünmemesi;
- otomatik E-Fatura/E-Arşiv yönlendirmesinin dört kombinasyonu;
- token içinden companyId/userId scope okuma;
- bilinmeyen token ve taşıyıcıda fail-closed davranış;
- E-Fatura payloadında payment/delivery alanlarının bulunmaması;
- E-Arşiv payloadında otomatik payment/delivery üretimi;
- eski SettingsJson mali alanlarının temizlenmesi ve dış-yazma anahtarının korunması;
- kaldırılmış API route'larının yüzey testleri.

Çalışan kapılar:

- frontend TypeScript `tsc -p ... --noEmit`;
- TypeScript/TSX transpile sözdizimi taraması;
- C#, JSON, YAML, XML/MSBuild, Python ve shell statik ayrıştırma;
- dokümantasyon transaction kontrolü;
- repository cleanliness ve Git whitespace kontrolü.

Çalıştırılamayan kapılar:

- .NET SDK bulunmadığı için backend restore/build/test/migration execution;
- Docker bulunmadığı için PostgreSQL Testcontainers ve Compose;
- `node_modules` bulunmadığı için Vitest/Playwright;
- E-Faturam Stage credential ve kontrollü mali sipariş verisi bulunmadığı için gerçek submit/status/PDF/cancel E2E.

## Production kararı

Kod ve kullanıcı yüzeyi sadeleştirildi; ancak migration, backend testleri ve gerçek E-Faturam Stage kabulü exact runtime ortamında çalıştırılmadan production dış-yazmaları açılmamalıdır.
