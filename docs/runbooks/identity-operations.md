# F1 Kimlik Operasyonları

## Session durumları

- `PASSWORD_CHANGE_REQUIRED`: yalnız `GET csrf`, `GET me`, `POST change-password`, `POST logout`.
- `MfaChallenge`: yalnız `GET csrf`, `GET me`, `POST mfa/challenge`, `POST logout`.
- `Active`: tenant ve OWNER bağlamı server-side membership'ten eklenir.
- `Revoked`: hiçbir işlem yapamaz.

Cookie `__Host-MarketplaceHub`; Secure, HttpOnly, Path `/`, Domain yok, SameSite Lax. Idle süre 30 dakika, absolute süre 12 saat, hassas güvenlik işlemlerinde 10 dakikalık reauthentication penceresi uygulanır. Değiştirici auth istekleri `X-CSRF-TOKEN` ve same-origin kontrolünden geçer.

## TOTP ve recovery

TOTP default `OFF`; 6 hane, 30 saniye, ±1 step ve son kabul edilen timestep replay guard'ı vardır. Pending enrollment 10 dakika geçerlidir. Confirm/regenerate cevabındaki 10 recovery code yalnız o response'ta gösterilir; veritabanında keyed digest tutulur. Regenerate eski batch'i atomik olarak invalid eder.

## Break-glass MFA reset

Bu işlem HTTP endpoint değildir. Önce OS/operatör yetkisini ayrı kanalla doğrula; ticket/olay nedenini hazırla. Komut reason olmadan veya yetki environment'ı olmadan fail-closed olur:

```powershell
& $compose -f deploy/compose/compose.yaml run --rm -e MARKETPLACEHUB_BREAK_GLASS_AUTHORIZED=true api api/MarketplaceHub.Api.dll identity reset-mfa '<user-email>' '<approved-ticket-and-reason>'
```

İşlem TOTP/recovery materyalini temizler, tüm session'ları revoke eder, session version'ı artırır ve secret içermeyen append-only audit kaydı yazar. Komut çıktısını secret olarak kabul etme; audit sonucu DB ve merkezi log üzerinden doğrulanır.
