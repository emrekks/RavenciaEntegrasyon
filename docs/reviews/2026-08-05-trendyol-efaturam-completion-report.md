# Trendyol E-Faturam Tamamlama ve Doğrulama Raporu

**Tarih:** 2026-08-05
**Kapsam:** F4 — Trendyol E-Faturam mali yaşam döngüsü
**Karar:** `CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`

## 1. Sonuç

Trendyol E-Faturam, Trendyol Türkiye CORE adapterından ayrı mali sağlayıcı sınırı olarak tamamlandı. API_USER ve MARKETPLACE kimlik doğrulaması, mükellefiyet sorgusu, E-Fatura/E-Arşiv oluşturma, sayısal durum uzlaştırması, güvenli kalıcı PDF, E-Arşiv iptal uzlaştırması, Trendyol paketine fatura bağlantısı teslimi, capability evidence, operatör ekranları ve güvenli bağlantı ayarları aynı fail-closed iş modeline bağlandı.

Bu rapor production kabulü değildir. Exact runtime testleri, PostgreSQL/Docker doğrulaması ve tarihli Trendyol E-Faturam Stage/SIT kabulü tamamlanmadan global ve bağlantı dış-yazma anahtarları kapalı kalmalıdır.

## 2. Tamamlanan teknik kapsam

- `signIn` kullanan API_USER modeli.
- Partner `signIn` ve müşteri `customerSignIn` zincirini kullanan MARKETPLACE modeli.
- Şifreleri geri döndürmeyen bağlantı ayarı okuma sözleşmesi.
- ETag korumalı ayar güncelleme ve gönderilmeyen alanları koruma.
- Çoklu kargo sağlayıcısı → VKN/TCKN → yasal unvan eşlemesi.
- VKN/TCKN biçim ve yalnız ASCII rakam doğrulaması.
- Temel/Ticari E-Fatura senaryosu bağlantı ayarı.
- Partner ID ve VKN/TCKN tabanlı mükellefiyet sorgusu.
- E-Fatura/E-Arşiv seçim kararı ve deterministik mali payload.
- Satır, vergi ve toplam tutar denklemleri.
- Tamamen iptal edilmiş veya pozitif faturalanabilir miktarı kalmamış sipariş satırlarını taslaktan güvenli biçimde dışlama.
- E-Arşiv internet satışı için ödeme, teslimat, sipariş ve kargo tüzel kimlik alanları.
- Global, bağlantı, feature flag, capability ve açık operatör onayıyla çok katmanlı dış-yazma kapısı.
- İdempotent gönderim job'ı, deneme kaydı ve otomatik uzlaştırma.
- `10/20/29/30/40/50/100/105/200/205/305/405` sağlayıcı kodlarını kapalı katalogla sınıflandırma.
- Bilinmeyen kodu başarı saymayan `MANUAL_REVIEW` davranışı.
- Kalıcı PDF URL'si ve HTTPS/host/public-IP/redirect/size/MIME/PDF imza kontrolleri.
- E-Arşiv iptal gönderimi ve `305` görülene kadar uzlaştırma.
- Trendyol paketine fatura bağlantısı gönderimi ve duplicate etki koruması.
- Submit, deliver ve cancel capability'leri için Stage/SIT fixture SHA-256 zorunluluğu.
- Fatura listeleme, filtre, detay, ETTN/UUID, PDF, gönderim, uzlaştırma, link teslimi, iptal ve mükellefiyet paneli.

## 3. Bilinçli fail-closed sınırlar

### Giden E-Fatura durum sorgusu

Public sözleşmede UUID tabanlı genel giden E-Fatura durum yolunun exact relative endpoint'i bu çalışma sırasında güvenilir biçimde kesinleştirilemedi. Bu nedenle endpoint tahmin edilmedi. `TrendyolEFaturam__OutgoingInvoiceStatusPath` boşsa adapter `EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED`; geçersiz mutlak veya traversal içeren yol verilirse `EFATURAM_EINVOICE_STATUS_PATH_INVALID` döndürür. Yol ancak Stage/SIT kanıtıyla deploy ayarına eklenmelidir.

### E-Fatura iptali

Otomatik E-Fatura iptali açılmadı. Mevzuat ve sağlayıcı süreci açısından E-Fatura itiraz/iptal işlemi manuel inceleme sınırında tutuldu. Otomasyon yalnız E-Arşiv iptal yaşam döngüsünü kapsar.

### Trendyol fatura bağlantısı teslimi

Gönderim dış etkisi idempotent kaydedilir. Güvenilir terminal query kanıtı bulunmadığında teslim sonucu otomatik kesin başarıya yükseltilmez; operatör teyidi veya Stage kanıtı gerektirir.

## 4. Kod ve sözleşme kanıtları

- Uygulama portları ve DTO'lar: `F3Contracts.cs`, `F4Contracts.cs`, `CapabilityEvidencePolicy.cs`.
- Mali kararlar: `InvoiceAmounts.cs`.
- Kimlik doğrulama ve HTTP adapterı: `TrendyolEFaturamAuthenticationHandler.cs`, `TrendyolEFaturamHttpClient.cs`.
- Payload ve mapper: `TrendyolEFaturamCanonicalPayload.cs`, `TrendyolEFaturamInvoicePayload.cs`, `TrendyolEFaturamJsonMapper.cs`, `TrendyolEFaturamStatusCatalog.cs`.
- İş servisleri ve worker: `F3ConnectionService.cs`, `F4BillingService.cs`, `F4JobProcessor.cs`.
- API: `F3Endpoints.cs`, `F4Endpoints.cs`.
- Operatör paneli: `F3Pages.tsx`, `F4Pages.tsx`.
- Regresyonlar: domain, capability policy, contract/payload, API surface, PostgreSQL ve frontend testleri.
- Operasyon belgeleri: ADR-017, F4 plan/evidence, capability matrisi ve invoice runbook.

## 5. Statik doğrulama

Tamamlanan statik kapılar:

- 16 TypeScript/TSX dosyası sözdizimsel transpile taramasından geçti.
- 144 C# kaynak dosyası yapısal/sözdizimsel denge taramasından geçti.
- 38 JSON, 7 YAML, 15 XML/MSBuild, 2 Python ve 5 shell dosyası ayrıştırma kontrollerinden geçti.
- Dokümantasyon transaction kontrolü geçti.
- Repository cleanliness kontrolü geçti.
- Git whitespace kontrolü geçti.

Git whitespace ve nesne bütünlüğü commit sonrasında doğrulandı. Teslim ZIP'i bağımsız klasörde açılarak ayrıca doğrulanacaktır.

## 6. Dinamik doğrulama blokajları

- `dotnet --info` exit `127`: proje tarafından sabitlenen .NET SDK `10.0.302` ortamda bulunmuyor.
- `docker version` exit `127`: Docker CLI/daemon bulunmuyor; PostgreSQL Testcontainers ve Compose kapıları çalıştırılamıyor.
- Ortam Node `22.16.0` ve npm `10.9.2`; proje Node `24.18.1` ve npm `11.12.1` sabitliyor.
- `node_modules` olmadığı için exact typecheck bağımlılık tiplerinde durdu; Vitest bulunamadı ve yerel Playwright paketi yüklenemedi.
- Trendyol E-Faturam Stage credential, kontrollü mükellef/sipariş/paket verisi ve yazma onayı sağlanmadı.

Bu blokajlar kod başarısı olarak raporlanmaz. Production kararı `BLOCKED` olarak kalır.

## 7. Stage kabul kriterleri

1. API_USER ve MARKETPLACE bağlantı testleri ayrı ayrı başarılı olmalı.
2. `customerSignIn` company/user scope değerleri bağlantı ayarıyla eşleşmeli.
3. Kontrollü VKN/TCKN için mükellefiyet sonucu fixture ile sabitlenmeli.
4. E-Fatura ve E-Arşiv create payload'ları sağlayıcı tarafından kabul edilmeli.
5. E-Arşiv `205`, ret ve `305` iptal durumları uzlaştırılmalı.
6. Giden E-Fatura exact status path'i yalnız kanıtlandıktan sonra yapılandırılmalı.
7. Kalıcı PDF private storage'a güvenli biçimde alınmalı.
8. Trendyol invoice-link teslimi kontrollü paket üzerinde doğrulanmalı.
9. Kanıt fixture'larının SHA-256 değerleri capability evidence kaydına işlenmeli.
10. Global ve bağlantı write anahtarları ancak tüm kabul kapıları geçtikten sonra kontrollü pilot için açılmalı.
