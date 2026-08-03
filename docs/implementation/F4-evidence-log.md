# F4 Fatura ve Mali Belge Kanıt Kaydı

İlk doğrulama tarihi: `2026-07-31`; üretim paneli salt-okunur Stage bağlantı doğrulaması ve resmî sözleşme güncellemesi: `2026-08-03`. Yerel ortam Windows geliştirme makinesi, .NET SDK `10.0.302`, Node `24.15.0`, npm `11.12.1`; gerçek bağlantı testi AWS Ubuntu üretim dağıtımından çalıştırıldı. Stage/production write veya mali belge işlemi kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F4-EV-001` build/format | PASS_BUILD | Solution `0 warning / 0 error`; Web strict TypeScript/Vite build geçti |
| `F4-EV-002` migration | PASS | F1→F4 tek zincir; `billing` schema ve sekiz F4 tablosu PostgreSQL 18.4 fresh testinde doğrulandı; migration SHA-256 `3B3D136A9861C83739E9B2B4C09DBE11E59C982ED81F915F98D5C0CB22C576EE` |
| `F4-EV-003` model/state | PASS_LOCAL | Allow-list state machine, tenant FK/UQ/check/version ve mali/lojistik ayrımı test edildi |
| `F4-EV-004` policy/rounding | PASS_APPROVED_MANUAL | Kullanıcı onaylı manuel policy; satır bazında half-away-from-zero ve bir kuruş üzeri farkta ret; auto-submit kapalı |
| `F4-EV-005` taxpayer/type | PASS_LOCAL_RULE / BLOCKED_EXTERNAL | Trendyol `commercial` + `invoiceAddress.eInvoiceAvailable` ile E-Fatura, diğer siparişlerde E-Arşiv; eksik VKN/TCKN gönderim öncesi ret |
| `F4-EV-006` duplicate/unknown | PARTIAL_LOCAL | Submit attempt hash, job dedup ve `UNKNOWN_RESULT` geçişi uygulandı; 20 paralel gerçek provider testi `BLOCKED_EXTERNAL` |
| `F4-EV-007` document | PASS_LOCAL_PIPELINE | E-Faturam kalıcı HTTPS URL isteği, 20 MB üst sınır, private storage, SHA-256 ve no-store streaming mevcut; gerçek test firma PDF'i `BLOCKED_EXTERNAL` |
| `F4-EV-008` snapshot | PASS_MODEL | Party snapshot protected+hashed, line snapshot append-only modelde; anonim mali fixture genişletmesi açık |
| `F4-EV-009` delivery | PASS_LOCAL_CONTRACT / BLOCKED_EXTERNAL | Kalıcı HTTPS link + package + invoiceDateTime + invoiceNumber, ayrı retry attempt ve yazma kapıları uygulandı; Trendyol Stage delivery bekliyor |
| `F4-EV-010` cancel/adjustment | BLOCKED_DECISION | `original_invoice_id`, immutable eski kayıt ve manuel fallback var; mali yöntem onayı bekleniyor |
| `F4-EV-011` secret/PII | PASS_LOCAL | Credential şifreli, tax-id/party protected, API maskeli; anonim fixture PII taraması geçti |
| `F4-EV-012` kill switch | PASS_LOCAL | Global + tenant auto-invoice + connection + capability dörtlü kapı; tümü varsayılan off/UNKNOWN |
| `F4-EV-013` reconciliation | PARTIAL_LOCAL | Local total dry-run ve remote raw-status fail-closed; gerçek ETTN/document comparison `BLOCKED_EXTERNAL` |
| `F4-EV-014` API/UI | PASS | Şartnamedeki F4 route aileleri, CSRF/re-auth/confirmation/ETag/idempotency/no-store guard; loading/empty/error/UNKNOWN UI |
| `F4-EV-015` backup/restore | BLOCKED_EXTERNAL | DB + private file modeli hazır; hedef backup/off-host/RTO ve belge checksum restore kanıtı yok |
| `F4-EV-016` provider E2E | PARTIAL_PASS_CONNECTION | 2026-08-03 E-Faturam Stage connection `VERIFIED`; submit + permanent document + Trendyol link HTTP sözleşmeleri yerel kodda hazır, gerçek safe-write capability'leri `UNKNOWN`, bütün mali yazmalar off |
| `F4-EV-017` production smoke | BLOCKED_EXTERNAL | Ubuntu sunucu/domain/production credential ve işlem bazlı etki onayı yok |

## Fixture checksum

`taxpayer-registered-anonymous.json`: SHA-256 `F6D22695EB5BFBC9286A23C18591FD2487EF30E27A255EDE923BF5E1FD410E5E`. Fixture e-posta, telefon, gerçek VKN/TCKN veya secret içermez.

## Test sonucu

- Güncel hızlı .NET seti: `103/103` başarılı (Domain 31, Application 19, Adapter 51, API 2); solution build `0 warning / 0 error`.
- PostgreSQL/Testcontainers ve tarayıcı uçtan uca seti Docker Engine kapalı olduğu için bu güncellemede çalışmadı; önceki hedef kanıt korunur, yeni migration için idempotent SQL üretimi geçti.
- Web: `1/1` Vitest başarılı; production bundle başarılı.
- Yerel panel: CSRF `200`, login `200`, `/me` `200`, ilk durum `PASSWORD_CHANGE_REQUIRED`; readiness ve Vite proxy `200`.

## Faz durumu

F4 yerel fatura oluşturma ve teslim zinciri `READY_LOCAL_WRITE_PIPELINE` durumundadır. Gerçek test firma safe-write kanıtı, capability onayı ve işlem bazlı kullanıcı izni olmadan dış yazmalar kapalıdır; F4 production mali etki çıkışı `BLOCKED_EXTERNAL` kalır.
