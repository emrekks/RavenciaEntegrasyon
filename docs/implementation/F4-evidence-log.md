# F4 Trendyol E-Faturam Kanıt Günlüğü

**Güncelleme:** 2026-08-05

## 2026-08-08 yerel eşitleme doğrulaması

- E-Faturam connection görünüm kontratına `hasCredential` alanı eklendi; frontend typecheck `PASS`.
- Frontend davranış paketi kalan test uyumsuzlukları nedeniyle `FAILED_VALIDATION`; Stage ve production durumu yükseltilmedi.

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
