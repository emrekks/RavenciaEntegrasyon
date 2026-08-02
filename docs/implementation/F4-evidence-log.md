# F4 Fatura ve Mali Belge Kanıt Kaydı

Doğrulama tarihi: `2026-07-31`. Ortam: Windows 10 geliştirme makinesi, .NET SDK `10.0.302`, Node `24.15.0`, npm `11.12.1`, izole yerel PostgreSQL `18.4`. Gerçek E-Faturam/Trendyol credential, test firma, Stage write, Ubuntu sunucu veya production işlemi kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F4-EV-001` build/format | PASS_BUILD | Solution `0 warning / 0 error`; Web strict TypeScript/Vite build geçti |
| `F4-EV-002` migration | PASS | F1→F4 tek zincir; `billing` schema ve sekiz F4 tablosu PostgreSQL 18.4 fresh testinde doğrulandı; migration SHA-256 `3B3D136A9861C83739E9B2B4C09DBE11E59C982ED81F915F98D5C0CB22C576EE` |
| `F4-EV-003` model/state | PASS_LOCAL | Allow-list state machine, tenant FK/UQ/check/version ve mali/lojistik ayrımı test edildi |
| `F4-EV-004` policy/rounding | BLOCKED_DECISION | `UNAPPROVED` ve `FISCAL_CALCULATION_AUTHORITY_REQUIRED` fail-closed; mali değer uydurulmadı |
| `F4-EV-005` taxpayer/type | PARTIAL_LOCAL | Anonim taxpayer parser ve tax-id biçim testi geçti; gerçek test firma/type seçimi `BLOCKED_EXTERNAL` |
| `F4-EV-006` duplicate/unknown | PARTIAL_LOCAL | Submit attempt hash, job dedup ve `UNKNOWN_RESULT` geçişi uygulandı; 20 paralel gerçek provider testi `BLOCKED_EXTERNAL` |
| `F4-EV-007` document | PASS_LOCAL_CORE | Composite tenant FK, immutable guard, private storage, SHA-256 ve no-store streaming mevcut; gerçek XML/PDF restore `BLOCKED_EXTERNAL` |
| `F4-EV-008` snapshot | PASS_MODEL | Party snapshot protected+hashed, line snapshot append-only modelde; anonim mali fixture genişletmesi açık |
| `F4-EV-009` delivery | PARTIAL_LOCAL | Ayrı job/attempt/idempotency ve `MARKETPLACE_PENDING` akışı mevcut; Trendyol Stage delivery `BLOCKED_EXTERNAL` |
| `F4-EV-010` cancel/adjustment | BLOCKED_DECISION | `original_invoice_id`, immutable eski kayıt ve manuel fallback var; mali yöntem onayı bekleniyor |
| `F4-EV-011` secret/PII | PASS_LOCAL | Credential şifreli, tax-id/party protected, API maskeli; anonim fixture PII taraması geçti |
| `F4-EV-012` kill switch | PASS_LOCAL | Global + tenant auto-invoice + connection + capability dörtlü kapı; tümü varsayılan off/UNKNOWN |
| `F4-EV-013` reconciliation | PARTIAL_LOCAL | Local total dry-run ve remote raw-status fail-closed; gerçek ETTN/document comparison `BLOCKED_EXTERNAL` |
| `F4-EV-014` API/UI | PASS | Şartnamedeki F4 route aileleri, CSRF/re-auth/confirmation/ETag/idempotency/no-store guard; loading/empty/error/UNKNOWN UI |
| `F4-EV-015` backup/restore | BLOCKED_EXTERNAL | DB + private file modeli hazır; hedef backup/off-host/RTO ve belge checksum restore kanıtı yok |
| `F4-EV-016` provider E2E | BLOCKED_EXTERNAL | Test firma, credential ve entegrasyon modeli yok; sign-in dışı adapter çağrıları fail-closed |
| `F4-EV-017` production smoke | BLOCKED_EXTERNAL | Ubuntu sunucu/domain/production credential ve işlem bazlı etki onayı yok |

## Fixture checksum

`taxpayer-registered-anonymous.json`: SHA-256 `F6D22695EB5BFBC9286A23C18591FD2487EF30E27A255EDE923BF5E1FD410E5E`. Fixture e-posta, telefon, gerçek VKN/TCKN veya secret içermez.

## Test sonucu

- .NET: `50/50` başarılı (Domain 12, Application 18, Adapter 9, API 2, Repository guard 2, PostgreSQL 7).
- Web: `1/1` Vitest başarılı; production bundle başarılı.
- Yerel panel: CSRF `200`, login `200`, `/me` `200`, ilk durum `PASSWORD_CHANGE_REQUIRED`; readiness ve Vite proxy `200`.

## Faz durumu

F4 yerel çekirdek `READY_LOCAL_CORE` durumundadır. Mali kararlar, gerçek test firma/capability, Stage invoice delivery, public HTTPS, backup/restore ve production smoke nedeniyle F4 faz çıkışı `BLOCKED_EXTERNAL` kalır.
