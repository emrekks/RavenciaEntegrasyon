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
- A 403 whose body is an HTML page is the gateway's temporary block (EFATURAM_GATEWAY_BLOCKED, RateLimit, 15 min), as on
  every adapter (`MarketplaceErrorRules.GatewayBlocked`): the request never reached E-Faturam, so it is a pre-provider
  refusal and a resend is safe (same ETTN). A 403 JSON problem stays EFATURAM_OPERATION_FORBIDDEN (scan D11, 15 Nov 2026).

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

## Permanent document URL and the marketplace invoice link (investigated 17 Nov 2026)
- `POST api/invoice/documents/download/permanent-url` ("getPermanentDocumentDownloadUrl", `efaturam-api-son.md:3280-3351`)
  takes `{companyId, documentUuid, documentType: EARCHIVE | EINVOICE, fileExtension: pdf}` and answers `text/plain`, a
  bare URL string (404 = document not found). The docs say nothing about the URL's host, shape, validity or whether it
  can change; `getTemporaryDocumentDownloadUrl` (`:3422`) is the short-lived variant and is not used.
- Shape seen on live (Hepsiburada package of invoice DAP2026000000448, 1 Nov 2026, kept in
  `InvoiceStuckDeliveryFixTests.K5Link`): `https://apigateway.trendyolecozum.com/api/invoice/documents/download/<number>`,
  the production API gateway host, no file extension, no query.
- It opens without authentication: `SafeRemoteDocumentDownloader.DownloadPdfAsync` fetches it with a bare GET (only
  `Accept: application/pdf`; the `TrendyolEFaturamDocument` client has no auth handler), https/443 only, host on the
  allow-list (the two gateway hosts + `TrendyolEFaturam:DocumentAllowedHosts`, env `MARKETPLACEHUB_EFATURAM_DOCUMENT_HOST`),
  public IPs only, up to 3 redirects, 20 MB, PDF signature `%PDF-` (a non-PDF answer = "not ready yet", retried in 2 min).
- Stored in `billing.invoice_documents.PermanentUrl` (varchar 4096, migration `F4InvoicePermanentDocumentUrl`) on the
  `PDF` row written by INVOICE_DOCUMENT_FETCH (`InvoicingJobProcessor.FetchDocument`) only after the download and the PDF
  checks passed. Until 17 Nov 2026 the row kept the URL after redirects (`FinalUrl`); since then it keeps the URL
  E-Faturam returned (identical when there is no redirect, as on live). A `MARKETPLACE_DELIVERY` row of the same table
  holds the panel's own public link (`InvoiceDocumentLink.Create`, `/api/v1/public/invoice-documents/{token}/content`,
  served by `InvoicingEndpoints` only for a row whose PermanentUrl is that exact path).
- What each marketplace takes as the link (sources in each marketplace file):
  - Trendyol `sendInvoiceLink`: string, no host or extension rule; must stay reachable for 10 years (legal note in
    `trendyol-api-son.md:7830`); 409 = package already has an invoice OR the link was used on another package.
  - Hepsiburada `PUT .../invoice`: string, no documented rule; the panel requires https and reads back until INVOICED.
  - n11 SaveLinkSellerInvoice: https, max 2048, path must end .pdf/.png/.jpeg (documented) - the provider URL has no
    extension, so n11 keeps the panel link (served as `.../invoice.pdf`).
  - Pazarama `order/invoice-link`: only a sample `https://faturaUrl.pdf` (`pazarama-api-son.md:3540-3590`); the adapter
    requires https, max 2048 and a document extension since phase 6, so Pazarama keeps the panel link too.
  - PTT AVM SaveInvoince: "url: Faturanın .pdf, html ve htm formatında url bilgisi" (`pttavm-api-son.md:1924`); the
    adapter requires https only. Acceptance is read back from the order lines' IsInvoice.
- Before 17 Nov 2026 Trendyol, Hepsiburada and Shopify already received the provider URL (the latest document with a
  PermanentUrl is the PDF row); only n11, Pazarama and PTT AVM received the panel link.
- Since 17 Nov 2026 (`MarketplaceInvoiceLinkPolicy`, setting `Invoicing:MarketplaceInvoiceLink` = `Provider` default |
  `Panel`): Trendyol, Hepsiburada, PTT AVM and Shopify get the provider URL of the verified PDF row; n11 and Pazarama
  always the panel link. Each delivery stores the link it carried (`marketplace_delivery_states.InvoiceLink`); retries,
  the Hepsiburada exchange re-send, confirmation and link removal use that link, and a delivery recorded before the
  column keeps its old link (the panel row when one exists, else the latest document). A missing or unusable provider
  URL stops the delivery (INVOICE_PROVIDER_LINK_MISSING / _INVALID, MarketplaceFailed, Tekrar Gönder) without falling
  back to the panel link. Reconciliation accepts any link the panel stored for the invoice as its own.

## E-Arşiv internet sale info (stage 5, 5 Nov 2026)
- `paymentInfo`: purchaseUrl (required, 2-255), paymentMeans (required: CREDIT_CARD, EFT, ON_DELIVERY, MEDIATOR,
  OTHER); paymentAgentName, paymentType, paymentDate optional (`efaturam-api-son.md:505-522`). The option is off by
  default (UI and CreateInvoiceCommand); the preview turns it off with the reason when it cannot be filled.
