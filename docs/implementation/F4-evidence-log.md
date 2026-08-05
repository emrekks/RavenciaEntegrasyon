# F4 Trendyol E-Faturam Kanıt Günlüğü

**Güncelleme:** 2026-08-05

| Kanıt | Durum | Not |
| --- | --- | --- |
| Safe connection settings | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Secret içermeyen ayar GET sözleşmesi, mevcut alanları koruyan PATCH ve çoklu carrier satır editörü eklendi. |
| API_USER sign-in | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | `signIn`, `x-access-token`, exact API version ve scope fingerprint uygulanmıştır. |
| MARKETPLACE sign-in | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Partner `signIn` sonrası `customerSignIn`; returned companyId/userId ayarla uyuşmazsa bloklanır. |
| Taxpayer query | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Partner ID + VKN/TCKN sorgusu, application detail ve aktif E-Fatura ayrımı kodlanmıştır. |
| E-Fatura/E-Arşiv create | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Kuruş payload, deterministic hash, Temel/Ticari senaryo ve source=PARTNER uygulanmıştır. |
| E-Arşiv internet satışı | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Payment/delivery ve kargo VKN/yasal unvan eşlemesi zorunludur. |
| E-Arşiv status | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Resmî UUID status endpoint'i ve numeric code catalog uygulanmıştır. |
| Giden E-Fatura status | FAIL_CLOSED_CONFIGURATION_REQUIRED | Public sözleşmede exact endpoint kesinleştirilmedi; deploy ayarı boşken adapter `EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED` döndürür. |
| Permanent PDF | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Permanent URL, HTTPS host allow-list, public DNS/IP, redirect, size, MIME ve `%PDF-` guardları vardır. |
| E-Arşiv cancellation | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Submit sonucu terminal sayılmaz; 305 görülene kadar reconciliation gerekir. |
| E-Fatura cancellation | NOT_SUPPORTED_BY_AUTOMATION | Mevzuata uygun itiraz/iptal süreci manuel inceleme olarak korunur. |
| Trendyol invoice-link | CODED_SUBMITTED_CONFIRMATION_REQUIRED | Duplicate teslim engellenir; kesin terminal query kanıtı yoksa manuel inceleme. |
| Operator UI | CODED_STATIC_VERIFIED / VITEST_PLAYWRIGHT_NOT_RUN | Güvenli mali ayar read-back, ETag korumalı çoklu carrier PATCH regresyonu, submit/reconcile/deliver/cancel, PDF, filtre ve taxpayer paneli eklendi. |
| Contract fixtures | CODED_STATIC_VERIFIED / DOTNET_NOT_RUN | customerSignIn, taxpayer, 205/305/105 status, ASCII tax-id, payload ve unknown-code testleri vardır. |
| Capability evidence policy | CODED_STATIC_VERIFIED / DOTNET_NOT_RUN | `TRENDYOL_EFATURAM` yalnız `developers.trendyolefaturam.com` kaynağını kabul eder; submit/cancel/deliver için 64 haneli Stage fixture SHA-256 zorunludur. |
| Exact runtime | BLOCKED_ENVIRONMENT | Bu çalışma ortamında .NET SDK/Docker ve pinli frontend bağımlılıkları yoktur. |
| Stage mali E2E | BLOCKED_EXTERNAL | Credential, controlled tax ID/package/order ve safe-write onayı yoktur. |

## Production kararı

Kod kapanışı production kabulü değildir. Capability evidence, exact runtime suite ve Stage E2E olmadan global/connection write anahtarları kapalı kalır.
