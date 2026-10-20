# 3 — Güvenlik, kimlik, ayarlar (A)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`.

**Feature gap:** no password reset, invitation, user/membership management or tenant switching backend; the login page tells the user to contact the administrator.

## Önceki düzeltmelerin doğrulaması
| Item | Status | Gap |
|---|---|---|
| P2 webhook gate/limiter | Present (`MarketplaceEndpoints.cs:347`, `WebhookRateLimits.cs`) | Gate checks only `connectionPublicId`; route token checked after up to 10 MiB buffered; per-IP 6000/min very high (A8) |
| P3 MFA attempts | Correct | Setup/confirm path has no factor check (A1) |
| P4 auth audit | Present | Step-up password failures on invoice actions (`InvoicingEndpoints.cs:609`) and lockouts there not audited; MFA setup start not audited; authenticator replacement logged as first enrolment |
| P5 roles | In place | Webhook subscription creation open to OPERATIONS (A22) |
| P6 status mapping | Correct | — |
| P7 replay | Stored | Per tenant not per user; bodies can hold secrets (A5) |
| P10 session | One round trip | — |
| P11 health | Caddy 404 for `/health/*` except `/live` publicly | `Program.cs:99` comment outdated |
| P12 settings If-Match | Present | Optional; concurrent first saves → 500 (A20) |
| P14 Sec-Fetch-Site | Present | `/files/product-media/{id}/content` still outbound fetch per request |

## Bulgular
1. **High — A1. Stolen session can enrol/replace authenticator without password or current factor.** `AuthEndpoints.cs:169-213, 393, 82`. `/mfa/setup` needs only `ReauthenticatedAt` < 10 min (login sets it); `/mfa/confirm` needs a code from the new secret; no session revocation or `SessionVersion` bump. Exploit: cookie stolen within 10 min of login → attacker holds authenticator and recovery codes; victim locked out. Fix: when TOTP enabled require password + current TOTP/recovery code (`ValidateSecondFactorAsync`), else password; on confirm bump SessionVersion, revoke others, audit `AUTH_MFA_REPLACED`.
2. **High — A2. Replacing authenticator leaves old recovery codes valid.** `AuthEndpoints.cs:211, 228, 371`; `MfaConfirmAsync` calls `ReplaceRecoveryCodes` without invalidating (unlike `RecoveryRegenerateAsync` :286); validation ignores `RecoveryBatchId`. Fix: same `ExecuteUpdate InvalidatedAt` in confirm transaction; check BatchId.
3. **High (unverified on host) — A3. MFA for OWNER/ADMINISTRATOR not enforced in production.** `appsettings.json:4` false; not set in `compose.production.yaml`. Fix: `Security__RequireMfaForElevatedRoles=true`.
4. **High — A4. Irreversible tenant-wide deletions have no step-up.** `MarketplaceEndpoints.cs:55-58`, `OperationalDataMaintenanceService.cs:12-36, 199-206`: `/settings/data-reset`, `/connections/{id}/deep-delete`, `data-reset` hard-delete orders, invoices, documents, deliveries with only a typed phrase. Fix: `PasswordCheck` + second factor (or reauth < 5 min) for these and `PUT /connections/{id}/credential`; soft-delete or refuse issued invoices.
5. **Medium — A5. Idempotency store keeps secrets and is shared across users.** `IdempotencyMiddleware.cs:62, 141-149`; `MarketplaceConnectionService.cs:693`: `POST /connections/{id}/webhooks` response (plaintext routeToken) stored 24 h; keyed by tenant+route+key. Fix: don't store bodies for secret routes; add UserId.
6. **Medium — A6. Passwords become an unsalted SHA-256 oracle.** `IdempotencyMiddleware.cs:56-59`, `InvoicingEndpoints.cs:628`: `ConfirmedAction(Password, …)` in hashed body. Fix: HMAC with server key, or strip secrets; better use `/auth/reauthenticate` timestamp.
7. **Medium — A7. No offboarding/recovery; lockout DoS.** No reset/user/membership endpoints; 5 wrong passwords per 15 min keeps the single owner locked forever. Fix: owner-only user admin (deactivate, role change via SessionVersion), CLI `identity reset-password`, IP-scoped/progressive lockout.
8. **Medium — A8. Webhook memory DoS when public id is known.** `MarketplaceEndpoints.cs:347-352`, `WebhookRateLimits.cs:11`. Fix: check route token hash in gate before reading body; concurrency limiter; per-IP ~600/min.
9. **Medium — A9. Current password brute-forceable via `/auth/change-password`.** `AuthEndpoints.cs:100-113`: failures don't call `AccessFailedAsync`, no rate-limit, no lockout check. Fix: `PasswordCheck.VerifyAsync` first, `.RequireRateLimiting("auth")`, audit.
10. **Medium — A10. Password change before second factor when `ForcePasswordChange`.** `AuthEndpoints.cs:63, 102-119`. Fix: MFA challenge first.
11. **Medium — A11. SignalR connections outlive logout/revocation/membership removal.** `Realtime/OperationsHub.cs:8-18`. Fix: store session_id per connection, periodic check/abort; Origin allowlist.
12. **Medium — A12. Data reset/deep delete leave private files on disk (KVKK).** `OperationalDataMaintenanceService.cs`. Fix: archive matching FileAssets; retention worker deletes files.
13. **Low-Medium — A13. Media proxy serves any `image/*`** (`CatalogEndpoints.cs:165-167`, `PublicNetwork.cs:77`). Fix: allowlist + magic bytes + CSP sandbox.
14. **Low — A14. SSRF filter gaps** (`PublicNetwork.cs:13-60`): NAT64 `64:ff9b::/96`, 6to4 `2002::/16`, IPv4-compatible, Teredo, `100::/64`; `UseProxy` default true (unverified). Fix: extract embedded IPv4, `UseProxy=false`.
15. **Low — A15.** `PrivateFileStorage.OpenReadAsync` checks tenant prefix before normalising (`PrivateFileStorage.cs:37, 44-47`). Fix: check after GetFullPath.
16. **Low — A16.** Data Protection purposes not bound to tenant/connection/user (`MarketplaceConnectionService.cs:68-69`, `AuthEndpoints.cs:174`).
17. **Low — A17.** Public invoice links never expire, cacheable, tokens in Caddy logs (`InvoiceDocumentLink.cs`, `InvoicingEndpoints.cs:410`). Fix: `private, no-store`, link version, `log_skip /api/v1/public/*`.
18. **Low (unverified) — A18.** Caddy JSON access log records `X-CSRF-TOKEN`, `Idempotency-Key`, query values.
19. **Low — A19.** Client `X-Correlation-ID` trusted (`Program.cs:111-117`).
20. **Low — A20.** Settings: missing If-Match last-write-wins; concurrent first saves 500; shipping-label JSON no schema (`SettingsEndpoints.cs:107-121, 319-328`).
21. **Low — A21.** Rate limiter partitions per full IP (IPv6 /128), one 10/min bucket for login/MFA/reauth, no global limiter (`Program.cs:79-81`).
22. **Low — A22.** Webhook subscription creation open to OPERATIONS (`RoleAuthorizationMiddleware.cs:92-94`).
23. **Low — A23.** `/api/v1/public/**` exemption covers every method (`RoleAuthorizationMiddleware.cs:22`).
24. **Low — A24.** Recovery code format ~50 bits, confusing (`AuthEndpoints.cs:385, 404`); use Crockford base32.
25. **Low — A25.** Login timing reveals unknown accounts (`AuthEndpoints.cs:46-51`).
26. **Low — A26.** Weak-password list has two entries (`AuthEndpoints.cs:409`).
27. **Info — A27.** Draft product images fetchable by asset GUID (`CatalogEndpoints.cs:137-152`).
28. **Info — A28.** Idempotency middleware buffers bodies before rate limiter (`IdempotencyMiddleware.cs:42-44`).

## Özet
Session/CSRF/cookie solid; tenant isolation consistent, no IDOR found. Weakest: MFA lifecycle (A1–A3). Destructive/fiscal operations lack step-up (A4); secrets leak to idempotency records (A5–A6). Missing user lifecycle turns lockout into single-owner DoS (A7). Fix A1–A9 first.
