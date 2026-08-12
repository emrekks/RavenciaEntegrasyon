# F4 Stage doğrudan hesap gate–route–service–job envanteri

Tarih: 2026-08-12

| Dosya / yüzey | Route / service / job | Mevcut görev | Stage gerekliliği | Production gerekliliği | Karar / planlanan değişiklik |
|---|---|---|---|---|---|
| `F3Endpoints.cs` + `F3ConnectionService.RotateCredentialAsync` | `PUT /api/v1/connections/{id}/credential` | Şifreli E-Faturam credential rotasyonu | E-posta + parola | E-posta + parola | Aktif kapsamın `API_USER` sözleşmesine dön; partner, müşteri hesabı ve müşteri VKN/TCKN zorunluluğunu kaldır. Secret saklama biçimi ve rotasyon audit davranışı korunur. |
| `TrendyolEFaturamAuthenticationHandler` | Tüm E-Faturam adapter çağrıları | Connection, credential ve environment boundary yükleme | Aktif bağlantı, çözülebilir Stage endpoint, e-posta/parola | Aktif bağlantı, çözülebilir Production endpoint, e-posta/parola | `Customer*` alanlarını runtime gate olmaktan çıkar. `IntegrationRuntimePolicy.TryResolveBaseAddress` fail-closed kalır. |
| `TrendyolEFaturamHttpClient.AcquireAccess` | Connection test/read/write ortak auth | Partner `signIn` ardından `customerSignIn` | Yanlış ve kapsam dışı blocker | Yanlış ve kapsam dışı blocker | Tek `signIn`; `companyId` ve `userId` imzalı access token claimlerinden okunur. Claim kapsamı belirsiz/çoklu ise fail-closed. |
| `F4JobProcessor` | `EFATURAM_CONNECTION_TEST` | Dayanıklı bağlantı testi | Doğrudan hesap auth sonucu | Doğrudan hesap auth sonucu | Job/dedup/retry yapısı değişmez; yalnız ortak adapter auth modeli düzeltilir. |
| `F4JobProcessor` | `INVOICE_SUBMIT`, `INVOICE_RECONCILE`, `INVOICE_DOCUMENT_FETCH`, `INVOICE_CANCEL` | Mali write/read zinciri | Manuel Stage: boundary + auth + input + idempotency + response/reconciliation | Authorization + master switch + connection switch + auth + input + idempotency + reconciliation/audit | Mali güvenlik kapıları korunur; partner/müşteri credential gate'i kaldırılır. |
| `F3Pages.tsx` | `/integrations/{id}` | Credential yönetimi | E-posta + parola | E-posta + parola | Teknik partner/müşteri formunu kaldır; yalnız bireysel hesap alanlarını göster. Secret tekrar gösterilmez. |
| Capability/evidence/fixture/SHA/fiscal approval/AUTO/re-auth | F4 manuel Stage route/job zinciri | Diagnostics/release kanıtı | Runtime blocker olmamalı | Runtime transaction gate'i değil; gerçek adapter desteği fail-closed | Mevcut Stage manuel politika korunur; bu refactor yeni kanıt veya otomasyon gate'i eklemez. |

Read yolları write/fiscal/automation/evidence gate'lerine bağlanmaz. Production endpoint/credential boundary, master dış-yazma anahtarı, bağlantı dış-yazma anahtarı, yetkilendirme, input doğrulama, idempotency, reconciliation ve audit kaldırılmaz.
