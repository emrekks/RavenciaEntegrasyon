# F6A Hepsiburada Adapter Kanıt Kaydı

Doğrulama tarihi: `2026-08-02`. Ortam: Windows geliştirme makinesi. Hepsiburada partner/SIT hesabı, merchant credential, anonim partner payload’ı, public callback veya dış yazma kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F6A-EV-001` build | PASS | Solution `0 warning / 0 error`; strict TypeScript/Vite production build geçti |
| `F6A-EV-002` phase boundary | PASS | Yalnız Hepsiburada F6A adapterı; N11/Pazarama adapter, route veya menüsü yok |
| `F6A-EV-003` generic ports | PASS_LOCAL | Connection/reference/product/inventory-price/order/return portlarının tamamı Infrastructure adapterda uygulanıyor |
| `F6A-EV-004` auth gate | PASS_FAIL_CLOSED | Portal auth anlatımı çelişkisi nedeniyle credential kaydı ve connection test job’u `HEPSIBURADA_AUTH_MODEL_UNVERIFIED` ile kapanır |
| `F6A-EV-005` read gate | PASS_FAIL_CLOSED | Partner fixture/capability yokken bütün dış read yöntemleri no-HTTP `HEPSIBURADA_CAPABILITY_UNVERIFIED` döndürür |
| `F6A-EV-006` write gate | PASS_FAIL_CLOSED | Product/listing, archive, stock, price, package ve return action yöntemleri `EXTERNAL_WRITE_DISABLED` döndürür |
| `F6A-EV-007` error classes | PASS_LOCAL | 401/403 authentication, 409 business conflict, 429 rate limit, 5xx remote ve diğer 4xx validation olarak ayrı test edildi |
| `F6A-EV-008` connection model | PASS_LOCAL | Draft merchant scope, `v1.0` guide kaydı, User-Agent ve capability UNKNOWN mevcut generic tablolarda tutulur; migration yok |
| `F6A-EV-009` UI | PASS_LOCAL | Mevcut integrations formunda yalnız F6A draft seçimi; auth blocker görünür ve credential/test düğmeleri kapalı |
| `F6A-EV-010` contract/SIT | BLOCKED_EXTERNAL | Gerçek payload olmadığı için mapping/status enum/endpoint oluşturulmadı |
| `F6A-EV-011` previous-platform gate | BLOCKED_PHASE_GATE | F5 production reconciliation/rollback kanıtı yok |

## Test sonucu

- .NET build: başarılı, `0` uyarı / `0` hata.
- Docker gerektirmeyen testler: `57/57` başarılı (Domain 12, Application 18, Adapter 23, API 2, Repository guard 2).
- Web: strict TypeScript/Vite production build ve component testi.
- Yeni migration oluşturulmadı.

## Faz durumu

F6A no-HTTP/no-write yerel güvenlik çekirdeği `READY_LOCAL_FAIL_CLOSED`dır. Partner/SIT hesabı, auth modeli, merchant scope/permission, tarihli endpoint/version, anonim fixture ve safe-write onayı olmadan capability `SUPPORTED` yapılmaz. F5 production reconciliation/rollback tamamlanmadan F6A production çıkışı yapılamaz. F6B ve F6C açılmamıştır.
