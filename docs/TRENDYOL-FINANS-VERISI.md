# Trendyol finans verisi (Cari Hesap Ekstresi)

Amaç: raporlarda tahmin yerine Trendyol'un kendi muhasebe kayıtlarından okunan gerçek masrafları göstermek
(komisyon, kargo faturası, platform hizmet bedeli, stopaj, ceza, erken ödeme, reklam).

Araştırma tarihi: 23 Ekim 2026. Canlı veriyle doğrulanmadı (bu çalışmada sunucuya ve Trendyol'a istek atılmadı).

## Kaynaklar

- Yurt içi (TR) doküman, v2 — en ayrıntılı tür listesi burada:
  https://developers.trendyol.com/v2.0/docs/current-account-statement-integration.md
- Kargo faturası ayrıntısı: https://developers.trendyol.com/v2.0/docs/cargo-invoice-details.md ve
  https://developers.trendyol.com/v2.0/reference/getcargoinvoiceitems.md
- Uluslararası (v3) doküman (storeFrontCode başlığı zorunlu; tür listesi kısa):
  https://developers.trendyol.com/v3.0/docs/1current-account-statement-integration.md,
  https://developers.trendyol.com/v3.0/reference/getsettlements.md,
  https://developers.trendyol.com/v3.0/reference/getotherfinancials.md

## Uç noktalar

Taban adres: `https://apigw.trendyol.com/integration/` (stage: `https://stageapigw.trendyol.com/integration/`).
Kimlik doğrulama sipariş servisleriyle aynı (Basic + User-Agent); istemci `storeFrontCode: TR` başlığını zaten gönderiyor.

| Uç nokta | Ne verir |
| --- | --- |
| `GET finance/che/sellers/{sellerId}/settlements` | Satış, iade, indirim, kupon, provizyon ve hakediş/komisyon düzeltmeleri (sipariş satırı düzeyinde, `barcode` ile) |
| `GET finance/che/sellers/{sellerId}/otherfinancials` | Kesinti faturaları (kargo, platform hizmet bedeli, …), stopaj, ödemeler, transferler, düzeltmeler |
| `GET finance/che/sellers/{sellerId}/cargo-invoice/{invoiceSerialNumber}/items` | Bir kargo faturasının paket satırları (sipariş no, tutar, desi, gönderi/iade) |
| `GET finance/che/sellers/{sellerId}/payment-order` | Ödenmiş hakediş emirleri (en çok 10'ar); bu çalışmada kullanılmadı |

Parametreler (settlements ve otherfinancials):

- `startDate`, `endDate`: milisaniye cinsinden zaman damgası, zorunlu; aralık **en çok 15 gün**.
- `transactionType` (tek tür) veya `transactionTypes` (virgüllü liste, yalnız v2'de yazıyor). Senkronizasyon her türü
  ayrı istekle okur: bir türü Trendyol 400 ile reddederse yalnız o tür atlanır ve operasyonel sorun olarak bildirilir.
- `page` (0'dan), `size` 500 veya 1000.
- `transactionSubType=PlatformServiceFee`: yalnız `DeductionInvoices` ile; platform hizmet bedeli kayıtlarını ayırır.
- Kayıtlar sipariş **teslim edildikten sonra** oluşur; `paymentOrderId` ödeme yapılınca dolar (genelde çarşamba).

Kayıt alanları: `id`, `transactionDate`, `barcode`, `transactionType` (Türkçe görünen ad, ör. "Satış",
"Kargo Faturası"), `receiptId`, `description`, `debt`, `credit`, `paymentPeriod`, `commissionRate`,
`commissionAmount`, `commissionInvoiceSerialNumber`, `sellerRevenue`, `orderNumber`, `paymentOrderId`,
`paymentDate`, `sellerId`, `storeId`…, `country`, `orderDate`, `affiliate`, `shipmentPackageId`.

Kargo faturası: `DeductionInvoices` yanıtında türü "Kargo Faturası" ya da "Kargo Fatura" olan kaydın `id` değeri,
faturanın `invoiceSerialNumber` değeridir. Satır alanları: `shipmentPackageType` ("Gönderi Kargo Bedeli",
"İade Kargo Bedeli"), `parcelUniqueId`, `orderNumber`, `amount`, `desi`.

## Okunan türler

- settlements: Sale, Return, Discount, DiscountCancel, Coupon, CouponCancel, ProvisionPositive, ProvisionNegative,
  ManuelRefund, ManualRefundCancel, TYDiscount, TYDiscountCancel, TYCoupon, TYCouponCancel, SellerRevenuePositive,
  SellerRevenueNegative, CommissionPositive, CommissionNegative, SellerRevenuePositiveCancel,
  SellerRevenueNegativeCancel, CommissionPositiveCancel, CommissionNegativeCancel.
- otherfinancials: DeductionInvoices (bir kez de `PlatformServiceFee` alt türüyle), Stoppage, CommissionAgreementInvoice,
  FinancialItem, ReturnInvoice.
- Okunmayanlar: PaymentOrder, WireTransfer, IncomingTransfer, CashAdvance — bunlar para hareketidir, masraf değildir;
  toplam masrafa karışırsa yanlış gösterim olur.

## Kategori eşlemesi

Ham tür her kayıtta saklanır (`RequestedType` = sorgu türü, `TransactionType` = Trendyol'un Türkçe adı). Tanınmayan her
kayıt `OTHER` olur, hiçbiri atılmaz.

| Kaynak / tür | Kategori |
| --- | --- |
| Sale | SALE |
| Return, ManuelRefund, ManualRefundCancel | RETURN |
| Discount, DiscountCancel, TYDiscount, TYDiscountCancel | DISCOUNT |
| Coupon, CouponCancel, TYCoupon, TYCouponCancel | COUPON |
| CommissionPositive/Negative (+Cancel), CommissionAgreementInvoice | COMMISSION |
| ProvisionPositive/Negative, SellerRevenue* | OTHER |
| Stoppage | STOPPAGE |
| DeductionInvoices + `PlatformServiceFee` | SERVICE_FEE |
| DeductionInvoices, FinancialItem, ReturnInvoice | ad/açıklamadaki anahtar kelimeye göre (sıra önemli): "kargo" → CARGO, "erken ödeme" → EARLY_PAYMENT, "reklam" → ADVERTISING, "ceza"/"gecikme" → PENALTY, "stopaj" → STOPPAGE, "komisyon" → COMMISSION, "hizmet"/"platform" → SERVICE_FEE, diğer → OTHER |

Tutar yönü: `debt − credit` (pozitif = satıcıdan kesilen). Komisyon, satış satırlarının `commissionAmount` alanından
dokümandaki işaret tablosuyla hesaplanır (Sale, DiscountCancel, CouponCancel, ProvisionPositive, TYDiscountCancel,
TYCouponCancel, ManualRefundCancel: satıcı öder; Return, Discount, Coupon, ProvisionNegative, TYDiscount, TYCoupon,
ManuelRefund: satıcıya geri döner). Komisyon düzeltme satırları (Commission*) ve CommissionAgreementInvoice için
`debt − credit` kullanılır.

## Senkronizasyon (TRENDYOL_FINANCE_SYNC)

- Yalnız `PRODUCTION` ortamındaki Trendyol bağlantıları; "FINANCE" politikası, günlük (öğleden sonra penceresi).
- İlk çalışmada geriye 90 gün, sonra her gün son kapsanan tarihten 3 gün geri örtüşerek bugüne kadar okunur.
- Pencereler en çok 15 gün; her iş diliminde en çok 2 pencere, kalan varsa iş kısa süre sonra devam eder.
- Bir pencerenin tüm türleri ve kargo satırları okunmadan hiçbir şey yazılmaz; yazma tek kayıtla (tek işlem) yapılır ve
  kapsanan tarih ancak o zaman ilerler. Hata → operasyonel sorun `TRENDYOL_FINANCE_SYNC_FAILED`, kapsam ilerlemez.
- Kendi kilit grubu (`finance`): sipariş okumalarını beklemez, onları da bekletmez. Yalnız okur, Trendyol'a yazmaz.

## Rapora yansıma (`costs` bölümü)

- Siparişe bağlı kayıtlar (komisyon, indirim, kupon, kargo satırları) siparişin dönemine yazılır; siparişe bağlı
  olmayanlar (hizmet bedeli, stopaj, ceza, kalemleri okunamamış kargo faturası) işlem tarihine göre.
- Ürün kırılımı yalnız `barcode` taşıyan kayıtlarda (satış satırları) yapılır; kargo ve hizmet bedeli ürüne
  paylaştırılmaz (paylaştırma tahmin olur).
- Hesap kesimi olan siparişlerde gerçek komisyon, tahmini komisyonun yerine kullanılır; mevcut tahmin alanları
  anlamını korur.
- Veri yoksa bölüm boş gelir ve not düşülür; sıfır uydurulmaz.

## Canlı veriyle doğrulanmayanlar

- `commissionAmount` ve `debt/credit` değerlerinin mutlak mı işaretli mi geldiği (kod mutlak değer alıp işaret
  tablosunu uygular). Doküman örneğindeki sayılar kendi içinde tutarsız.
- Komisyon düzeltme türlerinin (CommissionPositive vb.) tutarının `debt/credit`'te mi `commissionAmount`'ta mı olduğu.
- Kesinti faturalarındaki ceza, reklam ve erken ödeme kayıtlarının Türkçe adları (dokümanda yok; anahtar kelimeyle
  eşleniyor, eşleşmeyen OTHER'da kalır ve toplam masrafa eklenmez).
- Kargo faturası satır tutarlarının KDV dahil olup olmadığı ve toplamının fatura tutarına eşitliği.
- ManuelRefund / ManualRefundCancel yazımının API'de kabul edilip edilmediği (reddedilen tür atlanır ve bildirilir).
- Erken ödeme ücretinin hangi kayıtla geldiği (CashAdvance avansın kendisidir, ücret değildir; okunmuyor).
- Finans servislerinin hız sınırı (dokümanda yazmıyor).
