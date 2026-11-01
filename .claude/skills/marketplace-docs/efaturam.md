# Trendyol E-Faturam (invoice provider)

Docs: https://developers.trendyolefaturam.com/ — "API Dokümantasyonu" (`/OpenApi/...`, direct integration) and
"Pazaryeri Entegratörü Dokümantasyonu" (`/marketplace-docs/...`, partner integration). The site is a Docusaurus SPA:
the HTML is empty and WebFetch sees nothing. Each page's OpenAPI object sits in a chunk named in `assets/js/main.*.js`
(search the page's `.api.mdx` path there, then fetch `assets/js/<id>.<hash>.js`).

Code: `Adapters/TrendyolEFaturam/` — `TrendyolEFaturamHttpClient.cs`, `TrendyolEFaturamOptions.cs` (endpoints),
`Contracts/TrendyolEFaturamCanonicalPayload.cs`, `Contracts/TrendyolEFaturamInvoicePayload.cs`.

## Connection
- Stage `https://stage-apigateway.trendyolefaturam.com/` (IP whitelist), production `https://apigateway.trendyolecozum.com/`.
- `POST api/auth/signin` returns the token in `x-access-token`; the JWT carries `companyId`, `userId` and `privs`.
  Our account is a direct API_USER (source `PORTAL`), not a marketplace partner: the token has no partner id.

## e-Fatura taxpayer lookup (live since 31 Oct 2026)
- `GET api/invoice/taxpayers/{taxId}?showDeleted=false` ("Mükellef Sorgulama / getTaxPayersByTaxId").
- 200: rows `{taxId, alias, title, gibUserType, postBoxType, aliasType: INVOICE | DESPATCH_ADVICE, deletedAt…}`.
- 404 with problem type `…/gateway/tax-payer-not-found`: not an e-Invoice taxpayer. Any other answer is unknown.
- Outgoing e-Invoice takes an optional `targetAlias`; several aliases without it give 422 `multiple-aliases-exist`.
- Rules and decisions: `docs/EFATURA-MUKELLEF-SORGUSU.md`.

## Remaining credit (kontör): not available to our account
Every documented credit endpoint is on the "Partner Gateway", for a marketplace integrator and its sub-customers:
- `GET /api/invoice/partners/remaining-credit` — the partner's own remaining credit
  ([getRemainingCreditInfo](https://developers.trendyolefaturam.com/marketplace-docs/OpenApi/Kont%C3%B6r/get-remaining-credit-info)).
- `GET /api/invoice/partners/{partnerId}/credits/remaining/customers/{partnerCustomerId}` — a sub-customer's credit,
  `partnerId` must be the signed-in user's partner id
  ([getRemainingCreditByCustomerId](https://developers.trendyolefaturam.com/OpenApi/Di%C4%9Fer/get-remaining-credit-by-customer-id)).
  Answer: `{taxId, remainingCredit: int64 (adet), remainingCreditStatus: SUFFICIENT | LOW | RAN_OUT}`.
- `credit-transfer` (partner to sub-customer) is partner-only too.

The direct API documentation has no endpoint for an account's own credit, and our token has no partner id to call the
partner ones with. Checked 1 Nov 2026; ask entegrasyon@trendyolefaturam.com before building a credit display. An
insufficient balance surfaces only as 409 `…/gateway/insufficient-credit` on invoice creation.

### Credit probe (since 1 Nov 2026)
- `TrendyolEFaturamHttpClient.GetRemainingCreditAsync` tries only `GET api/invoice/partners/remaining-credit` with the
  connection's own sign-in token; API `GET /api/v1/connections/{id}/efaturam-remaining-credit`.
- Answer `{available: true, remainingCredit}` on 200; otherwise `{available: false, reason}`: `user-is-not-partner`
  (404 with that problem type), `unauthorized`, `forbidden`, `rate-limited`, `error`, `unreadable`, `sign-in-failed`.
- `TrendyolEFaturamCreditCache`: a value 10 min, not-partner 24 h, other failures 15 min (or Retry-After).
- Logs: `E-Faturam remaining credit. Available=True RemainingCredit=…` or the Warning
  `E-Faturam remaining credit unavailable. Reason=…`, once per cache period.
- The Integrations card shows `1.240 Kontör` beside the E-Faturam name only when available (warning colour under 50).
- Live answer for our production account (connection `01a13546-…-69d543113b52`), 1 Nov 2026 12:56 UTC, deploy
  `6c105ca9`: 404 `problem:/etransformation/gateway/user-is-not-partner` (api log, Warning
  `Reason="user-is-not-partner" ErrorCode="EFATURAM_RESOURCE_NOT_FOUND"`; the panel endpoint returned
  `{"available":false,"remainingCredit":null,"reason":"user-is-not-partner"}`). Our account is not a partner, so the
  badge does not appear; a credit display needs a direct-account endpoint from E-Faturam.
