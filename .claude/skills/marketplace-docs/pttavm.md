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
- No paging means the whole window is one answer, capped at 16 MB by `MarketplaceResponseLimits`; an answer over it
  fails as PTTAVM_NETWORK_ERROR on every retry and the window is not split (2 orders live, 6 Nov 2026: not reached).
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
- A product with variants is listed under its model code; GetProductsWithVariants names its variants only by
  colour/size value (live shape `Variant[Limit,PriceDifference,Quantity,VariantId,VariantName]`, no code, no Aktif), and
  BarkodKontrol on a panel variant stock code answered no product (300 lookups a night, all UNKNOWN). The nightly
  LISTING_VERIFY therefore saw 20 of 3,017 linked codes from 30 Oct to 6 Nov 2026 (only products without variants):
  StokKontrolListesi returns 204 products, paging was not the cause. Since 6 Nov 2026 a linked panel variant of a
  listed model counts as seen when the model lists its colour and size values (the rule `MapPttAvmProduct` links it
  by), and its sale state is the model's Aktif (`MarketplaceJobProcessor.MarkPttAvmModelLinksSeen`).

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
- Variant prices (owner decision 18 Nov 2026; offline doc `pttavm-api-son.md` "Ürün Ekleme Güncelleme" and "Fiyat Stok
  Güncelle"): a product keeps one price and each variant a VAT-inclusive `Price` difference added to it (product price
  + difference must not be below 0). The panel computes it: product price = the family's lowest price, difference =
  variant price − that (100 and 150 → 50), on the price-stock send and, since 18 Nov, on the product upsert too
  (`VariantV3.Price`, left out at 0); a difference PTT AVM holds is carried, a variant priced on its own is not. Live 18
  Nov: 13 models had size-based prices on Trendyol (e.g. 799 normal, 899 big) but one price on PTT AVM (447 of the 468
  differing sizes), because the publication refused differing prices before.
- A model with variants is one product under its model code; the family goes whole and the adapter adds only the
  variants the product lacks (`PttAvmVariants`), so a new colour is a new variant of that product.
- No return service: returns come from "iade" lines, already completed; return actions are refused. RETURN_SYNC reads
  the last 2 days, the whole 3 months every 6 h (PttAvmReturnReadPolicy). A read answered with an unexplained fault
  ("Bilinmeyen bir hata oluştu") is retried (PTTAVM_REMOTE_FAULT); on a write (SaveInvoince, UpdateProductsStockPrice,
  UpdateProductsV3) the same fault is an uncertain result (Remote5xx PTTAVM_REMOTE_FAULT, "sonuç belirsiz"; an invoice is
  settled by reading IsInvoice back), never a definite refusal (scan B-3, 25 Nov 2026; source PttAvmSoap.FaultError).
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

## Invoices, buyer (stage 5, 5 Nov 2026)
- SaveInvoince answering Success=false with "işlem gerçekleştirilmiştir" is an uncertain delivery (BusinessConflict),
  settled by reading the order's per-line `IsInvoice` (true: confirmed; false: sent again on the automatic schedule).
- Invoice link (17 Nov 2026): the docs ask for "url: Faturanın .pdf, html ve htm formatında url bilgisi"
  (`pttavm-api-son.md:1924`). Whether a link without an extension (E-Faturam's `.../documents/download/<number>`) is
  accepted: unknown, never tried. PTT AVM therefore keeps the panel link (`.../invoice.pdf`) whatever
  `Invoicing:MarketplaceInvoiceLink` says (`MarketplaceInvoiceLinkPolicy`, efaturam.md).
- Buyer kind: `FaturaTip`/`faturaTip` ("Bireysel", "Kurumsal"; `pttavm-api-son.md:1644,1859`), kept as invoiceType.
  The sample sends it null: then the TCKN/VKN decides.
- E-Arşiv internet sale: purchaseUrl `https://www.pttavm.com`, paymentMeans MEDIATOR; the carrier (needed for the
  e-Arşiv delivery info) is known only once the tracking number comes.

## Report data gaps (7 Nov 2026)
- Discount: a line carries `IndirimPttavm` ("Pttavm indirim bilgisi") and `IndirimTedarikci` ("Tedarikçi(mağaza)
  indirim bilgisi") apart (`pttavm-api-son.md:1632-1634`, 1848-1850); the mapper reads only `IndirimToplam`
  (`PttAvmOrderMapper.cs:71`). Live, the 3 lines of 60 days have both at 0, so the split was never seen on a real
  discount: not read yet. No list price field, only the amounts with and without VAT.
- No shipment due date in the order API (the field table near `pttavm-api-son.md:1600-1660` has IslemTarihi only):
  ShipmentDueAt stays empty (0/2 orders), so the reports cannot measure late shipping on PTT AVM and say so.
- Returns page sort (11 Nov 2026): `ReturnClaim.RequestedAt` is the claim request date the İadeler page sorts by (default "En Yeni İade Talebi"); a claim without it lists last, never guessed. PTT AVM has no claim (a return is an order line in iade status), so RequestedAt stays empty and its returns list last.

## Model link (Model Kaydı Faz 2, 14 Nov 2026)
- Model-level id = the product id (`UrunId`), ExternalCode = the product barcode (`Barkod`; the model code for a product
  with variants, a variant barcode for some one-variant products: live 18 of 196). Live backfill: 68 models (only where no
  other colour card holds PTT AVM variant links; the rest wait for the next import's evidence).
- Family publication adds the model link's barcode to the parent-barcode check (can only stop a send); price/stock of a
  card without its own link goes under the model link's barcode before the model-code rule (`TrendyolWriteComposers`).
