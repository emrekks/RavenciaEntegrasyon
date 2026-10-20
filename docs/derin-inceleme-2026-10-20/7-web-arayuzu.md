# 7 — Web arayüzü (W)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`.
> Ölçüm: `npm ci` 0 zafiyet; typecheck 0 hata; lint 0; vitest 110 dosya / 495 test geçti; build uyarısız. En büyük parçalar: index CSS 373,8 kB, vendor-react 224,5 kB, MappingPages JS 171 kB, ProductEditorPages JS 170,6 kB.

## 19 Ekim düzeltmelerinin durumu
W1 düzeldi (açık: Ayarlar güvenlik çağrıları `api()` ile, 401'de giriş ekranı yerine mesaj, `SystemSettingsPage.tsx:182-264`). W2 düzeldi. W9 kısmi: pano aralıkları ve eksen (`DashboardPage.tsx:46-67,139`), soru saati (`QuestionsPage.tsx:54-55,88-95`), İşlem Takibi "Bugün" (`operations/pages.tsx:200`), tarih seçici hazır aralıkları (`DateRangePicker.tsx:6-26`) tarayıcı saatiyle. W3 düzeldi (artıklar: `IntegrationPages.tsx:243` ölü anahtar; sipariş detay anahtarları yenilenmiyor). W5, W6, W7, W14 düzeldi.

## Bulgular
1. **High — W1.** Background polls keep idle sessions alive (`shared/api/client.ts:28`; only `App.tsx:59-60` send the header; hidden-tab polls in `DashboardPage.tsx:32`, `ProductsPage.tsx:415-416`, `ProductEditorPages.tsx:750,924`; visible polls in `data-freshness.tsx:27`, `invoicing/pages.tsx:361`, `ReturnPages.tsx:348`, `operations/pages.tsx:473,487`). Fix: header on every interval query (helper) or on GETs without recent user interaction.
2. **High — W2.** Quick stock edit writes back old stock (`ProductsPage.tsx:212-230`): field not resynced, sends `typed − latest onHand`, empty → 0, no error handling. Fix: dirty-only save, resync, real empty, show errors; better absolute set with expected current stock.
3. **High — W3.** Product editor save reverts stock changes made meanwhile (`ProductEditorPages.tsx:2205-2209`, reset 1134-1169). Fix: keep loaded stock per row, send only edited rows against loaded value.
4. **High — W4.** Bulk selection impossible on phones on Faturalar (`invoicing/pages.tsx:494,530,543`; `invoices-responsive.css:241,397`). Fix: show checkboxes or long-press selection mode; RTL test at 375 px.
5. **Medium-High — W5.** Deleting a saved image wipes unsaved edits (`ProductEditorPages.tsx:1469` vs 1137: seed key mismatch). Fix: shared `editorSeedKey()` + test.
6. **Medium-High — W6.** No unsaved-changes guard; realtime reseeds editor (`ProductEditorPages.tsx:1169`; `main.tsx:38` BrowserRouter so no `useBlocker`; no `beforeunload`). Fix: dirty flag + banner; `beforeunload`; `createBrowserRouter`.
7. **Medium — W7.** Notification history, tab counts, label data leak across users/tenants (`notifications.ts:35`, `tab-counts.ts:12`, `shipping-label.ts:132,229`, `shipping-settings.ts:19,35-46`, `QuestionsPage.tsx:84`). Fix: `${tenantId}:${userId}` keys, clear on logout/login.
8. **Medium — W8.** Bulk "Kargo Firması Değiştir" ignores row rules, stops at first error, no refresh, false message (`OrdersPage.tsx:856, 486, 294-308`).
9. **Medium — W9.** Settings errors as green success (`SettingsNotifications.tsx:9`, `notifications.ts:40`, `SystemSettingsPage.tsx:215,247,269`).
10. **Medium — W10.** Saving credentials leaves connection card stale → 412 (`IntegrationPages.tsx:496-514,669`; server `MarketplaceConnectionService.cs:441`).
11. **Medium — W11.** Returned stock cannot be put back for some approvals (`ReturnPages.tsx:463`; server `MarketplaceSalesService.cs:1993,2305`); disposition key replays.
12. **Medium — W12.** Return approval messages claim stock added without checking results (`ReturnApproveDialog.tsx:97-104`, `ReturnBulkDecisionDialog.tsx:89,129-136`).
13. **Medium — W13.** Dashboard custom range throws on half-picked range (`DashboardPage.tsx:52-53,69,192-196`; `DateRangePicker.tsx:76,88`).
14. **Medium — W14.** Dashboard periods use browser time zone (`DashboardPage.tsx:46-67,139,175,242`).
15. **Medium — W15.** Invoice detail modal works on stale snapshot; "Platforma ilet" stays enabled (`invoicing/pages.tsx:325,574-581`).
16. **Medium — W16.** List state lost on Back/refresh (`OrdersPage.tsx:758,867,955`, `ReturnPages.tsx:325`, `ShipmentPages.tsx`, `ProductsPage.tsx:400-405`). Fix: `useSearchParams`.
17. **Medium — W17.** Price/stock sorting on Ürünler sorts only current page (`ProductsPage.tsx:432`, `product-catalog-sorting.ts:19-23`).
18. **Medium — W18.** Bulk quick edit stops halfway, stale baseline (`ProductsPage.tsx:158-188`).
19. **Medium — W19.** Server field errors never shown (`client.ts:70-84`, `ProductEditorPages.tsx:2048`).
20. **Medium — W20.** Mapping conflicts stale; one-click delete with old version (`MappingConflictsPanel.tsx:15-33`; saves at `MappingPages.tsx:1022,1264,1391,1496,1501`, `MissingValueMappingDialog.tsx:62-66`).
21. **Medium — W21.** Value-mapping save stuck after partial failure (`MappingPages.tsx:1371-1393`).
22. **Medium — W22.** İşlem Takibi refetches every loaded page every 5 s (`operations/pages.tsx:479-488`).
23. **Medium — W23.** HB package form can't be resubmitted after correction (`OrdersPage.tsx:581,599`).
24. **Medium — W24.** "Yenile" on Sorular does nothing for N11/Pazarama (`QuestionsPage.tsx:189,220-224`; `QuestionEndpoints.cs:31-38`).
25. **Medium — W25.** Single label print never reaches server (`OrdersPage.tsx:376` vs 388).
26. **Low-Medium — W26.** Order-detail modal data never refreshed (`OrdersPage.tsx:118,131,166,227,330,383`).
27. **Low-Medium — W27.** Searches not debounced (`QuestionsPage.tsx:167,179,302`; `invoicing/pages.tsx:358-362`).
28. **Low-Medium — W28.** Selected question silently switches (`QuestionsPage.tsx:238`).
29. **Low-Medium — W29.** Answer templates deleted with one click (`QuestionsPage.tsx:198,341`).
30. **Low-Medium — W30.** Password/MFA screens hide real error; no confirm-password field (`App.tsx:374-376,409-410`).
31. **Low-Medium — W31.** Authenticator setup: recovery-code step closes on Escape; manual key shows full otpauth URI (`SystemSettingsPage.tsx:362,367`).
32. **Low — W32.** Copy/format leftovers: unformatted tax/discount (`invoicing/pages.tsx:744-745`), hard-coded "Trendyol" (`:181,746`), raw codes (`InvoiceWorkspacePreviewModal.tsx:118`, `IntegrationPages.tsx:678`, CSV export `OrdersPage.tsx:830`), "Talep Tarihi" mislabel (`ReturnPages.tsx:79`), JPEG/PNG text but only PDF accepted (`OrdersPage.tsx:125`), case mix (`ShipmentPages.tsx`, `CatalogListPages.tsx`, `ImportPages.tsx`), raw JSON field (`ShipmentPages.tsx:106`), import page polls forever (`ImportPages.tsx:19`), hard-coded production values (`operations/pages.tsx:390`, `IntegrationPages.tsx:639,674`), late warning hidden on phones (`orders-sade.css:177`), `JSON.stringify(pageItems)` per render + per-row ResizeObserver (`OrdersPage.tsx:941,494-503`), unvirtualized value-mapping lists (unverified), dashboard axis ₺ for every currency.

## Özet
W1–W14 fixes hold except W9 (partial). Worst new gaps are stock correctness (quick edit, editor, bulk edit send changes against stale numbers). Session/tenant hygiene (polls defeat idle timeout; localStorage leaks across users/tenants). Data-loss UX (no unsaved guard, realtime/image delete reseed, field errors hidden). Phones can't bulk-select invoices; list filters not in URL; several mutations stop halfway and still claim success.
