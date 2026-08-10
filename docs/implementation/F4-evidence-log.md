# F4 Trendyol E-Faturam Kanıt Günlüğü

## 2026-08-09 — v10.15.1 CI biçimlendirme kaydı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Repository formatter | PASS_LOCAL | Yeni manuel fatura belgesi endpointindeki import sırası `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore` ile doğrulandı. Davranış veya dış etki değişmez. |
| Tam GitHub release doğrulaması | PENDING | Kaynak ve belge transaction'ı yeniden CI hattında doğrulanmalıdır. |

**Güncelleme:** 2026-08-05

## 2026-08-09 — v10.15 güvenli manuel fatura belgesi yükleme

| Kanıt | Durum | Not |
| --- | --- | --- |
| API ve dosya güvenliği | PASS_LOCAL | `POST /invoices/{id}/documents/manual`, aktif tenant ve idempotency anahtarı ister; PDF/JPEG/PNG dosya imzasını, 10 MiB sınırını ve tenant fatura sahipliğini doğrular. |
| Private arşiv ve audit | PASS_LOCAL | Belge `INVOICE_DOCUMENT_MANUAL` private asset olarak saklanır; SHA-256 ile yinelenen yükleme ikinci belge üretmez ve `INVOICE_DOCUMENT_MANUAL_UPLOAD` audit kaydı yazar. |
| Dış etki sınırı | CODED | Manuel belge yükleme E‑Faturam submit, iptal veya Trendyol fatura-link delivery job'u oluşturmaz; elle yüklenen belge kalıcı dış URL sayılmaz. |
| Yerel doğrulama | PASS_LOCAL | .NET solution build, API yüzey testi, TypeScript, 16/16 Vitest ve production web build geçti. |
| Stage mali E2E | REVALIDATION_REQUIRED | Bu private upload için dış yazma yoktur; mevcut F4 Stage mali kabul kapıları değişmez. |

## 2026-08-08 yerel eşitleme doğrulaması

- E-Faturam connection görünüm kontratına `hasCredential` alanı eklendi; frontend typecheck `PASS`.
- Fatura politikası yüklenirken kaydetme kilidi ve güncel bağlantı fixture sözleşmesi düzeltildi; frontend typecheck, production build ve 13 Vitest davranış testi `PASS`.
- Stage ve production durumu yükseltilmedi; Docker/PostgreSQL ve gerçek mali Stage kabulü bekliyor.

| Kanıt | Durum | Not |
| --- | --- | --- |
| Provider-managed connection | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Mali hesap/seri/senaryo/kargo/ödeme ayarları kaldırıldı; connection settings yalnız dış-yazma anahtarını taşır. Eski JSON alanları data migration ve runtime sanitization ile temizlenir. |
| API_USER sign-in | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Doğrudan `signIn`, `x-access-token`, exact API version ve şifreli e-posta/parola uygulanmıştır. |
| Token fiscal scope | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | `companyId/userId` sign-in JWT kapsamından okunur; scope yoksa `EFATURAM_TOKEN_SCOPE_MISSING` ile fail-closed durur. |
| Automatic invoice type | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | `commercial && eInvoiceAvailable` => `TEMELFATURA`; diğer siparişler => `EARSIVFATURA`. Ayrı taxpayer sorgusu veya senaryo ayarı yoktur. |
| E-Fatura/E-Arşiv create | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Kuruş payload, deterministic hash, provider varsayılan serisi ve `source=WEB` uygulanmıştır. |
| E-Arşiv internet satışı | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Payment/delivery kullanıcı ayarı değildir; Trendyol siparişi ve resmî carrier kataloğundan otomatik üretilir. Bilinmeyen sağlayıcı bloklanır. |
| E-Arşiv status | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Resmî UUID status endpoint'i ve numeric code catalog uygulanmıştır. |
| Giden E-Fatura status | FAIL_CLOSED_CONFIGURATION_REQUIRED | Public sözleşmede exact endpoint kesinleştirilmedi; deploy ayarı boşken adapter `EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED` döndürür. |
| Permanent PDF | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Permanent URL, HTTPS host allow-list, public DNS/IP, redirect, size, MIME ve `%PDF-` guardları vardır. |
| E-Arşiv cancellation | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Submit sonucu terminal sayılmaz; 305 görülene kadar reconciliation gerekir. |
| E-Fatura cancellation | NOT_SUPPORTED_BY_AUTOMATION | Mevzuata uygun itiraz/iptal süreci manuel inceleme olarak korunur. |
| Trendyol invoice-link | CODED_SUBMITTED_CONFIRMATION_REQUIRED | Duplicate teslim engellenir; kesin terminal query kanıtı yoksa manuel inceleme. |
| Operator UI | CODED_STATIC_VERIFIED / VITEST_PLAYWRIGHT_NOT_RUN | Yalnız E-Faturam credential, otomatik belge türü açıklaması, manuel paket policy, submit/reconcile/deliver/cancel, PDF ve filtre ekranları vardır. |
| Contract fixtures | CODED_STATIC_VERIFIED / DOTNET_NOT_RUN | JWT scope, resmî carrier alias, 205/305/105 status, ASCII tax-id, otomatik payload ve unknown-code testleri vardır. |
| Capability evidence policy | CODED_STATIC_VERIFIED / DOTNET_NOT_RUN | `TRENDYOL_EFATURAM` yalnız `developers.trendyolefaturam.com` kaynağını kabul eder; submit/cancel/deliver için 64 haneli Stage fixture SHA-256 zorunludur. |
| Exact runtime | BLOCKED_ENVIRONMENT | Bu çalışma ortamında .NET SDK/Docker ve pinli frontend bağımlılıkları yoktur. |
| Stage mali E2E | BLOCKED_EXTERNAL | Credential, kontrollü corporate/individual order/package ve safe-write onayı yoktur. |

## Production kararı

Kod kapanışı production kabulü değildir. Capability evidence, exact runtime suite ve Stage E2E olmadan global/connection write anahtarları kapalı kalır.
## 2026-08-10 — v10.32 faturalama ayar yüzeyi sadeleştirmesi

- Kullanılmayan genel faturalama ayarları kullanıcı menüsünden kaldırıldı; eski `/settings/billing` adresi sistem ayarlarına yönlenir.
- Fatura oluşturma, yükleme ve provider submit onay kapıları değiştirilmedi; dış yazma açılmadı.
- Web TypeScript ve Vitest PASS; provider/Stage mali akışı `NOT_RUN`.

## 2026-08-09 — v10.19 fatura taslak ön izlemesi

- Sipariş satırındaki “Fatura Oluştur”, API kaynaklı müşteri, fatura adresi, satır, KDV ve tutar özetini modalde gösterir.
- Devam adımı yalnız mevcut idempotent `/invoices` taslak endpoint'ini çağırır; gerçek E-Faturam submit parola + açık onay akışında kalır.
- Doğrulama: ilgili web davranış testi dahil Vitest 18/18 PASS, TypeScript ve production build PASS.
- Stage/provider gerçek fatura gönderimi `NOT_RUN`; canlı görsel testte mali dış yazma başlatılmayacaktır.
