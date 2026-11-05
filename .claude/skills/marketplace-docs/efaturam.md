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
- A 408 is a timeout (EFATURAM_REMOTE_TIMEOUT, TransientNetwork), not a refusal: on an invoice creation the outcome
  is unknown (`InvoiceSubmissionOutcomePolicy`) and the invoice is read back by its own UUID, never sent again blind
  (`TrendyolEFaturamErrorMapper.FromStatus`, report item 16, 5 Nov 2026). Before, a 408 read as "request refused".

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

## E-Arşiv internet sale info (stage 5, 5 Nov 2026)
- `paymentInfo`: purchaseUrl (required, 2-255), paymentMeans (required: CREDIT_CARD, EFT, ON_DELIVERY, MEDIATOR,
  OTHER); paymentAgentName, paymentType, paymentDate optional (`efaturam-api-son.md:505-522`). The option is off by
  default (UI and CreateInvoiceCommand); the preview turns it off with the reason when it cannot be filled.
