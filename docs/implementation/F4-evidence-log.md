# F4 E-Faturam Kanıt Günlüğü

| Kanıt | Durum | Not |
| --- | --- | --- |
| Sign-in contract | PASS_LOCAL | x-access-token akışı adapter sınırında uygulanmıştır |
| Canonical invoice payload | PASS_LOCAL | Payload ve tutar testleri vardır |
| Permanent PDF URL contract | PASS_LOCAL | Provider çağrısı uygulanmıştır |
| Taxpayer query | MISSING | Fail-closed unsupported |
| Invoice status polling | MISSING | Fail-closed unsupported |
| Invoice cancellation | MISSING | Fail-closed unsupported |
| Test firm E2E | BLOCKED_EXTERNAL | Credential, company/user scope ve mali karar gerekir |
| Trendyol invoice-link E2E | BLOCKED_EXTERNAL | Gerçek package ve public HTTPS link gerekir |
## 2026-08-05 production sertleştirme v7

| Kanıt | Durum | Not |
| --- | --- | --- |
| Güvenli PDF indirme sınırı | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Exact HTTPS host allow-list, public DNS/IP, en fazla 3 redirect, 20 MiB streaming sınırı, MIME ve `%PDF-` doğrulaması uygulanır. |
| Fatura reconciliation semantiği | CODED_STATIC_VERIFIED / PROVIDER_STATUS_NOT_RUN | Eşlenmemiş uzak durum başarı sayılmaz; `MANUAL_REVIEW`, geçici durum `RETRY_SCHEDULED` olur. |
| Trendyol fatura linki terminal durumu | CODED_STATIC_VERIFIED / CONFIRMATION_BLOCKED_EXTERNAL | HTTP 2xx yalnız `SUBMITTED` üretir. Resmî otomatik teyit endpoint'i doğrulanmadığı için kayıt manuel incelemeye düşer; tekrar job aynı linki yeniden göndermez. |
