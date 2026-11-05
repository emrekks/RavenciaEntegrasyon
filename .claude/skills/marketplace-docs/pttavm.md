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

## Sale state (T2-D30, 2026-11-05)
- StokKontrolListesi row `Aktif` ("Ürünün aktiflik bilgisi", set by `Aktif Yap`) and a variant entry's own `Aktif`;
  `Mevcut` is stock, not sale. Source: offline doc `# Aktif Yap` and the product list field table. A row without
  `Aktif` stays unknown. Code: `PttAvmProductMapper.SaleState`.

## Statuses (SiparisDurumu, Turkish letters folded)
kargo_yapilmasi_bekleniyor → Created; havale_onayi_bekleniyor / onay_surecinde → Awaiting (OnHold); gonderilmis /
gonderildi → Shipped; gondericisine_teslim_edildi → UndeliveredAndReturned (package Returned, the same panel state as
Trendyol's UnDeliveredAndReturned: the parcel is back with the seller, no sale invoice; the line keeps its own name so
the return read, which takes `iade` lines only, does not see a customer return); tamamlandi → Delivered; iptal /
odeme_gecersiz → Cancelled; iade → Returned. The offline doc lists the value without explaining it
(`pttavm-api-son.md:1622,1699,1838`); "delivered to its sender" is read literally.
Package status: an active line of unknown status makes the package ManualReview (İnceleme Gerekli); otherwise the
least advanced known line decides. Code: `PttAvmOrderMapper.PackageStatus`.

## Quirks
- No return service: returns come from "iade" lines, already completed; return actions are refused. RETURN_SYNC reads
  the last 2 days, the whole 3 months every 6 h (PttAvmReturnReadPolicy). A read answered with an unexplained fault
  ("Bilinmeyen bir hata oluştu") is retried (PTTAVM_REMOTE_FAULT); a write keeps PTTAVM_REQUEST_REJECTED.
- `IsInvoice=true` only for invoices sent through the API; panel uploads leave it false, so false = unverified.
- `SaveInvoince` answers Success=false with "işlem gerçekleştirilmiştir" on success — the result is read back.
- An upsert without the variant list deletes all variants.
- Manual external writes are gated by `PttAvmManualWritePolicy`.
- `PTTAVM-ENTEGRASYON-PLANI.md` gives PTTAVM_ORDER_SYNC as 60 s in §4 and 2 min in §6.1; check the code.
- Order lines (SiparisKontrolListesiV2): `UrunKodu` is the stock code ("Ürün stok kod bilgisi"), `UrunId` the product
  id, `UrunBarkod` the barcode (a model's code for a variant product; `VariantBarkod` the variant's). There is no order
  total, only line amounts (`KdvDahilToplamTutar`) (`pttavm-api-son.md:1597-1632`). Since 5 Nov 2026 a line's code is
  the barcode, else `UrunKodu`, never `UrunId` or the line id; an order with an unread line says its amount may be
  short (ORDER_AMOUNT_MAY_BE_INCOMPLETE, order list mark) (`PttAvmOrderMapper.cs`).
- No product approval step. The publication check reads the listed product's `Aktif` (and a variant entry's own
  `Aktif`) as the sale state: on sale → APPROVED, passive → ARCHIVED (review), no `Aktif` → UNKNOWN (review); a missing
  flag is never "live" (`PttAvmProductPayloadRules.ListedStatus`, report item 13, 5 Nov 2026). Before, passive meant
  "still waiting" and a missing flag meant live.
- A price/stock tracking answer that leaves a product out settles that line UNKNOWN (not sent; it goes again with the
  next change), the job asks for a look (PRICE_INVENTORY_LINES_UNCONFIRMED) (report item 14).

## Job types
PTTAVM_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC, _ORDER_RECOVERY_SYNC, _ORDER_STATUS_SYNC.
