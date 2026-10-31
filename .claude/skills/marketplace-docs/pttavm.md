# PttAVM

Docs: https://developers.pttavm.com/en/ (SOAP pages `*_soap`; behind Cloudflare, WebFetch gets 403).
WSDL: `https://ws.pttavm.com:93/service.svc?singleWsdl`.

Code: `Adapters/PttAvm/` — `PttAvmHttpClient.cs`, `PttAvmSoap.cs`, `PttAvmOrders.cs`, `PttAvmOrderMapper.cs`,
`PttAvmInvoices.cs`, `PttAvmReturns.cs`, `PttAvmPriceInventory.cs`, `PttAvmProducts.cs`, `PttAvmProductWrites.cs`,
`PttAvmProductUpdate.cs`, `PttAvmVariants.cs`, `PttAvmReferences.cs`, `PttAvmCodeLookup.cs`,
`PttAvmAuthenticationHandler.cs`, `PttAvmRateLimitHandler.cs`.

## Connection
- SOAP 1.1 WCF `https://ws.pttavm.com:93/service.svc`, binding `WsSecured_IService`. No sandbox.
- The REST API `https://integration-api.pttavm.com/api/v1` is not used: the seller panel issues no Api-Key /
  Access-Token.
- WS-Security Timestamp (5 min) + UsernameToken (PasswordText) with the panel's API username and password; Mağaza ID
  = `ExternalStoreId`. `SOAPAction: http://tempuri.org/IService/<Operation>`.
- Nulls must be `i:nil="true"`. A malformed element returns the same security fault as a wrong password.

## Limits
None documented. Code: orders 30/min, reads 30/min, product writes 15 s apart. The same product or price/stock request
may not repeat within 5 min (`PTTAVM_REPEAT_REQUEST_WAIT`). Price/stock ≤ 1000 items per request.

## Windows and paging
- `SiparisKontrolListesiV2 (BaslangicTarihi, BitisTarihi, AktifSiparisler)`: no paging, one call returns the whole
  window. Documented max 40 days, code 35. First read 3 months; incremental re-reads 2 days.
- One order is found by reading the day in its number (`PTT-…-ddMMyy`) ±1 day.
- `GetProductsWithVariants(page, size)` pages from 1; `StokKontrolListesi` read until `RowCount`.

## Time
`IslemTarihi` has no offset; read as Istanbul time.

## Operations
`SiparisKontrolListesiV2` (orders), `SaveInvoince` (sic; invoice link), `UpdateProductsStockPrice`,
`GetProductsTrackingResult` (async result), `UpdateProductsV3` (upsert by barcode), `GetProductsWithVariants`,
`StokKontrolListesi`, `BarkodKontrol`, `GetCategoryTree`, `GetMainCategories`, `KullaniciTedarikciBilgisiGetir`,
`KargoBilgiListesi`. Shipping is a separate REST service (`shipment.pttavm.com`) and is blocked.

## Statuses (SiparisDurumu, Turkish letters folded)
kargo_yapilmasi_bekleniyor → Created; havale_onayi_bekleniyor / onay_surecinde → Awaiting (OnHold); gonderilmis /
gonderildi → Shipped; gondericisine_teslim_edildi → Undelivered; tamamlandi → Delivered; iptal / odeme_gecersiz →
Cancelled; iade → Returned.

## Quirks
- No return service: returns come from "iade" lines, already completed; return actions are refused.
- `IsInvoice=true` only for invoices sent through the API; panel uploads leave it false, so false = unverified.
- `SaveInvoince` answers Success=false with "işlem gerçekleştirilmiştir" on success — the result is read back.
- An upsert without the variant list deletes all variants.
- Manual external writes are gated by `PttAvmManualWritePolicy`.
- `PTTAVM-ENTEGRASYON-PLANI.md` gives PTTAVM_ORDER_SYNC as 60 s in §4 and 2 min in §6.1; check the code.

## Job types
PTTAVM_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC, _ORDER_RECOVERY_SYNC, _ORDER_STATUS_SYNC.
