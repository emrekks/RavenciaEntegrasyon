# e-Fatura Mükellef Sorgusu (Trendyol E-Faturam)

Araştırma: 31 Ekim 2026. Amaç: fatura kesilmeden önce alıcının e-Fatura mükellefi olup olmadığını E-Faturam'dan
sorgulamak; mükellefse e-Fatura, değilse e-Arşiv kesmek.

## Doküman kaynağı

- Sayfa: [getTaxPayersByTaxId](https://developers.trendyolefaturam.com/OpenApi/M%C3%BCkellef%20Sorgulama/get-tax-payers-by-tax-id)
  (bölüm "Mükellef Sorgulama", tag "Tax Payer Gateway").
- Giden e-Fatura: [createOutgoingEInvoice](https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice).
- Enum'lar: [enums](https://developers.trendyolefaturam.com/OpenApi/Enum%20ve%20Kod%20A%C3%A7%C4%B1klamalar%C4%B1/enums).
- Site bir Docusaurus SPA'sı; sayfa HTML'i boş gelir. İçerik `main.*.js` içindeki chunk eşlemesinden bulunan
  `assets/js/143487be.*.js` (mükellef) ve `assets/js/c8bad86c.*.js` (giden e-Fatura) dosyalarındadır.

## Mükellef sorgusu

| | |
|---|---|
| İstek | `GET /api/invoice/taxpayers/{taxId}?showDeleted=false` |
| Sunucular | Stage `https://stage-apigateway.trendyolefaturam.com/`, Prod `https://apigateway.trendyolecozum.com/` |
| Kimlik | Diğer uçlarla aynı Bearer token (`api/auth/signin`); stage IP whitelist ister, prod istemez |
| `taxId` | VKN (10) veya TCKN (11) |
| `showDeleted` | `true` ise silinmiş etiketler de döner; varsayılan `false` |
| 200 | `TaxPayer[]` dizisi |
| 404 | `problem+json`, type `.../gateway/tax-payer-not-found`, gövdede `taxId`: kayıtlı e-Fatura kullanıcısı değil |
| default | `problem+json` (ProblemDetail) |

`TaxPayer` alanları: `taxId`, `alias` (posta kutusu etiketi, örn. `urn:mail:defaultpk@...`), `title` (unvan),
`gibUserType`, `postBoxType`, `aliasType` (`INVOICE` | `DESPATCH_ADVICE`), `sourceFile`, `createdAt`,
`aliasCreationTime`, `firstCreationTime`, `deletedAt`.

- Bir mükellefin birden fazla satırı olabilir: fatura (`INVOICE`) ve irsaliye (`DESPATCH_ADVICE`) etiketleri ayrı
  satırlardır. e-Fatura için yalnız `aliasType = INVOICE` ve `deletedAt` boş satırlar geçerlidir.
- `postBoxType` ve `gibUserType` değerleri dokümanda enum olarak verilmemiş (serbest metin). GİB listesinde PK
  (posta kutusu, alıcı) ve GB (gönderici birim) etiketleri vardır; e-Fatura alıcı etiketi PK'dır. Gerçek değerler ilk
  canlı yanıtta loglanıp doğrulanmalı.
- Dokümanda istek limiti, önbellek süresi veya "default PK" kuralı yazmıyor. SSS (`/faq`) yalnız IP whitelist ve
  başvuru süreçlerini anlatıyor.

## Giden e-Fatura ve etiket seçimi

- `POST /api/invoice/documents/outgoing-einvoice`, `EInvoiceRequest`; isteğe bağlı `targetAlias` (en fazla 255):
  "Birden fazla fatura etiketi bulunan alıcı firmalar için etiket seçimi buradan yapılabilir".
- Etiket verilmezse sağlayıcı tek etiketi kendisi seçer; birden fazla etiket varsa
  `422 .../gateway/multiple-aliases-exist` döner. Yanlış etiket `404 .../gateway/target-alias-not-found`,
  mükellef olmayan alıcı `404 .../gateway/tax-payer-not-found`, senaryo uyumsuzluğu `409 .../gateway/scenario-not-valid`.
- Yani sağlayıcı e-Fatura'yı mükellef olmayan alıcıya göndermez; ama e-Arşiv'i mükellef alıcıya kesmeyi engellemez.
  Bu yüzden ön sorgu bizim tarafta yapılmalı.

## Bugünkü davranış (kod)

- `InvoiceAmounts.InvoiceDocumentType` (Domain): Trendyol `commercial` + `eInvoiceAvailable` bayraklarından tür
  seçer; diğer pazaryerleri yalnız açık bir e-Fatura bayrağı (`isEInvoice`, `isEInvoiceObliged`) varsa `TEMELFATURA`.
- `InvoiceIssuePolicy.Refusal`: `TEMELFATURA` her zaman durdurulur (`EINVOICE_RECIPIENT_REQUIRES_EFATURA`, otomatik
  e-Fatura kapalı); Trendyol dışı kurumsal alıcı (VKN, firma unvanı, kurumsal tip) bayraksız ise
  `EINVOICE_RECIPIENT_REVIEW_REQUIRED` ile incelemeye düşer (önizlemedeki sarı uyarı).
- `TrendyolEFaturamHttpClient.SubmitAsync` türüne göre `earchive` veya `outgoing-einvoice` ucuna gönderir;
  e-Fatura yolu kodda var ama canlıda hiç kullanılmadı ve `targetAlias` göndermiyor.
- Canlı (31 Ekim): yalnız `PRODUCTION` E-Faturam bağlantısı aktif, `STAGE` bağlantısı silinmiş. Canlıdaki 12 faturanın
  tamamı `EARSIVFATURA`.

## Uygulanan karar (31 Ekim 2026, kullanıcı onayı)

- Otomatik gönderim yok ve açılmayacak: her fatura "Onayla ve Faturayı Kes" ile gider. Tür seçimi otomatiktir.
- **Kim sorgulanır:** kurumsal alıcı (`InvoiceAmounts.IsCorporateBuyer` veya fatura gövdesinin kurumsal okuması) ya da
  pazaryerinin e-Fatura diye işaretlediği alıcı; Trendyol dahil her pazaryerinde. Bireysel alıcı sorgulanmaz, e-Arşiv
  (11111111111) kalır.
- **Sonuç** (`EInvoiceRecipientPolicy.Decide`):
  - Mükellef: `TEMELFATURA`, `targetAlias` = tek INVOICE etiketi, yoksa tek `urn:mail:defaultpk` etiketi. Seçilemezse
    `EINVOICE_TARGET_ALIAS_UNRESOLVED` ile durur.
  - Mükellef değil (yalnız 404 `tax-payer-not-found`): `EARSIVFATURA`. Pazaryeri e-Fatura dediyse
    `EINVOICE_TAXPAYER_CONFLICT` ile durur.
  - Bilinmiyor (her hata, çıplak 404, boş veya başka VKN içeren 200, yalnız irsaliye etiketi):
    `EINVOICE_RECIPIENT_REVIEW_REQUIRED`, fatura kesilmez.
- **Ne zaman:** önizlemede ve taslakta (24 saatlik bellek önbelleği, yalnız kesin sonuçlar); worker'da E-Faturam isteğinden
  hemen önce önbelleksiz. Worker'daki sonuç kayıtlı türle uyuşmazsa `EINVOICE_TAXPAYER_STATUS_CHANGED`. Bu kodlar
  sağlayıcıya bir şey gitmeden durdurur, önizlemeden yeniden denenebilir; tür değiştiyse gönderilmemiş taslak yeniden kurulur.
- GB (gönderici) ve silinmiş etiketler atılır. Her sorgu `E-Faturam taxpayer lookup` log satırı bırakır (durum, etiket
  sayısı, postBoxType). İlk canlı yanıtta `postBoxType` değerleri bu logdan doğrulanmalı.
- Migration yok.
