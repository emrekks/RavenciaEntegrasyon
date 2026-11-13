# Pazaryeri Adaptörleri ve E-Faturam Entegrasyonu: Hata Denetimi

Kapsam: `src/MarketplaceHub.Infrastructure/Adapters/**` (Trendyol, Hepsiburada, N11, Pazarama, PttAVM, Shopify,
TrendyolEFaturam), `MarketplacePortRouter.cs` ve `MarketplaceWebhookVerifier.cs`. Yalnız okuma yapıldı; canlı sunucuya
dokunulmadı. Kaynaklar depo içi dosya yollarıdır (satır numaraları denetim anındaki çalışma ağacına göredir). Web
araştırması yapılmadı: pazaryeri gerçekleri `.claude/skills/marketplace-docs/*.md` dosyasından alındı.

Etiketler: önem **Kritik / Yüksek / Orta / Düşük**; güven **[Kesin]** (kod yolu izlendi ve hata deterministik),
**[Muhtemel]** (kod yolu kesin, gerçekleşmesi uzak sistemin bir davranışına bağlı), **[Tahmin]** (doğrulanmamış varsayım).

## 1. Kod, marketplace-docs'ta kayıtlı gerçeklerle çelişiyor mu?

### Takeaway
Uç noktaların büyük bölümü, pencereler ve sayfa boyutları belgelerle uyumlu. Gerçek çelişkiler iki tane: Hepsiburada
soru listesi sorgu zamanlarını İstanbul yerel saati yerine ISO/UTC biçiminde gönderiyor. Hepsiburada katalog kotası ise
belgede IP başına tanımlıyken kodda hesap (Authorization başlığı) başına tutuluyor.

### Cited Findings
- **[Yüksek][Muhtemel] HB katalog (mpop) kotası IP yerine hesap başına tutuluyor.** Belge: "catalog 180/min per IP …
  excess blocks the IP and unblocking needs a ticket with a static IP"
  ([hepsiburada.md](.claude/skills/marketplace-docs/hepsiburada.md) "Limits"). Kodda kova anahtarı
  `hepsiburada:{AccountKey(request)}:{bucket}`. `AccountKey`, Authorization başlığının SHA-256 özetinden üretiliyor
  ([HepsiburadaRateLimitHandler.cs:24,45,55](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaRateLimitHandler.cs)).
  Senaryo: aynı sunucuda (tek çıkış IP'si) iki HB bağlantısı var ve her ikisi de katalog/ürün senkronu yapıyor. Her biri
  180/dk kullanabiliyor, IP'den toplam 360/dk gidiyor ve HB IP'yi blokluyor. Kimlik doğrulamada "username → merchantId"
  geri dönüşü Authorization başlığını değiştirdiğinden aynı hesap bile iki ayrı kova açabiliyor.
- **[Orta][Muhtemel] HB soru listesi tarih filtresi İstanbul yerel saati değil.** Belgeye ve CLAUDE.md'ye göre HB sorgu
  zamanları `yyyy-MM-dd HH:mm` Türkiye saatiyle gönderilmeli (`HepsiburadaJsonMapper.QueryTime`). Kod ise
  `minModifiedAt` / `maxModifiedAt` değerlerini `start.ToString("O")` ile, yani `2026-11-05T09:00:00.0000000+00:00`
  biçiminde gönderiyor ([HepsiburadaQuestions.cs:80-81](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaQuestions.cs)).
  Senaryo: HB offset'i yok sayıp değeri İstanbul yerel saati olarak okursa pencere 3 saat geriye kayar. Pencerenin son
  3 saatinde değişen sorular o turda okunmaz; ISO biçimini hiç kabul etmezse filtre tümden düşer.
- [Uyumlu] Trendyol uç noktaları belgedeki tabloyla birebir aynı (ürün oluşturma `v2/products`, sipariş `v2/orders`,
  stream, claims, seller-invoice-links): [TrendyolOptions.cs:36-74](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolOptions.cs).
- [Uyumlu] HB sayfa üst sınırları (sipariş 100, paket 10, durum listeleri 50, eksik fatura/iptal 50) `Page(...)`
  içinde uygulanıyor ([HepsiburadaHttpClient.cs:1468-1472](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
  24 saatlik pencere sınırı da var ([HepsiburadaHttpClient.cs:530-531, 589-590](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
- [Uyumlu] Pazarama'da bitiş günü hariç tutuluyor. Pencereler "yarına kadar" kuruluyor
  ([SynchronizationPolicies.cs PazaramaOrderHistoryPolicy.Windows](src/MarketplaceHub.Application/SynchronizationPolicies.cs)),
  adaptör de Türkiye günüyle gönderiyor ([PazaramaOrders.cs:16-25](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaOrders.cs)).
- [Uyumlu] Trendyol finans penceresi en çok 15 gün ve `supplierId` gönderiliyor
  ([TrendyolFinance.cs:52-53, 80](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolFinance.cs)).
- [Uyumlu] N11 `sender=ALL`, `orderByField=true`, sayfa ≤ 100 ve pencere ≤ 15 gün
  ([N11Orders.cs:13-31](src/MarketplaceHub.Infrastructure/Adapters/N11/N11Orders.cs)).

### Inferences
- Belgedeki "per IP" ifadesi yalnız katalog için geçiyor. OMS için hesap başına tutulan kova muhtemelen doğru; asıl risk
  ürün/katalog işlerinin aynı anda koştuğu çok bağlantılı kiracılarda.

### Gaps
- HB ask-to-seller servisinin `minModifiedAt` için hangi biçimi beklediği belgede yazmıyor; HB sayfaları WebFetch'e 403
  veriyor, bu yüzden doğrulanmadı.
- HB katalog IP blok eşiğinin sayısı belgede de "unverified".

## 2. Hepsiburada sorgu zamanları İstanbul yerel saatiyle mi gidiyor; timestamptz'ye UTC mi yazılıyor?

### Takeaway
Sipariş, paket, iptal, talep ve finans sorguları `QueryTime` (İstanbul, dakika hassasiyeti) ya da İstanbul günü
kullanıyor; tek istisna soru listesi (bkz. bölüm 1). Ayrıştırma tarafında HB, Pazarama, N11 SOAP ve PttAVM değerleri
`.ToUniversalTime()` ile normalize ediliyor. Trendyol'un `FlexibleInstant` yardımcısı ise offset'li bir metni offset'iyle
bırakıyor.

### Cited Findings
- `QueryTime` = `value.ToOffset(+3).ToString("yyyy-MM-dd HH:mm")`
  ([HepsiburadaJsonMapper.cs:1759-1760](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaJsonMapper.cs)).
  Siparişler, paketler, iptaller ve talepler bunu kullanıyor
  ([HepsiburadaHttpClient.cs:534-535, 721-722, 793-794, 1136-1142](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
  Finans sorgusu İstanbul günüyle gidiyor
  ([HepsiburadaFinance.cs:27-36](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaFinance.cs)).
- `TurkeyTime.Parse`, offset'siz metni +03:00 sayıp `.ToUniversalTime()` yapıyor
  ([TurkeyTime.cs:13-22](src/MarketplaceHub.Infrastructure/Adapters/TurkeyTime.cs)).
- **[Düşük][Muhtemel] Trendyol `FlexibleInstant` offset'li metni UTC'ye çevirmiyor.**
  `DateTimeOffset.TryParse(..., AssumeUniversal)` sonucu `AdjustToUniversal` ya da `.ToUniversalTime()` olmadan
  döndürülüyor ([TrendyolJsonMapper.cs:1200-1211](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)).
  Kullanıldığı yerler: `invoiceUpdatedAt`, `agreedDeliveryDate`/`extendedAgreedDeliveryDate` (`DueAt`), claim
  `claimDate` ve `autoApproveDate` (satır 89, 95, 745, 746). Senaryo: Trendyol bu alanlardan birini
  `"2026-11-05T10:00:00+03:00"` metniyle gönderirse değer +03:00 offset'iyle kalır. Npgsql timestamptz yalnız offset 0
  kabul ettiği için kayıt `SaveChanges` sırasında patlar (CLAUDE.md "Npgsql timestamptz accepts offset 0 only").
  Infrastructure'da genel bir DateTimeOffset→UTC dönüştürücüsü bulunamadı. Aynı dosyadaki `TrendyolQuestions.Date`
  `.ToUniversalTime()` yapıyor ([TrendyolQuestions.cs:116](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolQuestions.cs)),
  yani bu bir tutarsızlık.
- **[Düşük][Tahmin] HB dakika hassasiyeti pencere kenarlarını aşağı yuvarlıyor.** `enddate=QueryTime(before)` saniyeleri
  atıyor: 12:34:50'deki bitiş 12:34 oluyor. Sonraki pencere aynı `before` değerinden başlıyorsa kayıp olmaz;
  `enddate`'in kapsayıcı mı dışlayıcı mı olduğu belgede yok.
- **[Düşük][Tahmin] E-Faturam yük ayrıştırması `DateTimeOffset.Parse(text, InvariantCulture)` ile `DateTimeStyles.None`
  kullanıyor.** Offset'siz bir metin sunucunun yerel saat dilimiyle okunur
  ([TrendyolEFaturamCanonicalPayload.cs:140,150,161](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamCanonicalPayload.cs)).
  Yük panel içinde "O" biçimiyle üretildiği için bugün sorun çıkarmıyor.

### Inferences
- Trendyol tarihleri genellikle epoch sayı olarak geliyor, bu yüzden `FlexibleInstant` sorunu bugün tetiklenmiyor
  olabilir. Ancak kod metin dalını açıkça destekliyor ve o dalda UTC normalizasyonu yok.

### Gaps
- Trendyol'un claim ve paket tarihlerini hiç metin olarak gönderip göndermediği canlı veriyle doğrulanmadı.

## 3. Trendyol v2 servislerini kullanıyor mu; sınır ötesi vitrin siparişleri kendi ülkesinde mi sayılıyor?

### Takeaway
Evet: sipariş okumaları v2 (`order/sellers/{id}/v2/orders`) ve stream servislerini kullanıyor. Siparişlere okundukları
vitrinin kodu yazılıyor, claim'lerde vitrin bilinmiyorsa TR varsayılmıyor (null bırakılıyor). Sorun, birden çok vitrinde
yanıtların birleştirildiği claim akışında ve sipariş aramadaki hata/eksik okuma ayrımında.

### Cited Findings
- Stream okuması `window.StoreFrontCode` başlığıyla gidiyor ve her siparişe o vitrin kodu yazılıyor; başlık yoksa
  `HomeStoreFront` (TR) kullanılıyor ([TrendyolHttpClient.cs:249-256](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
- Claim'in vitrini yalnız `storeFrontCode`/`orderCountryCode` alanından ve bilinen kod listesinden alınıyor; yoksa
  `null` ("never TR") ([TrendyolJsonMapper.cs:859-866](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)).
  Webhook paketleri için `orderCountryCode`, iş işleyicisinde kullanılıyor
  ([MarketplaceJobProcessor.cs:3908, 9356](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).
- **[Orta][Kesin] Stream'de sipariş taraması 400/404'te "bulunan kadarını" başarı sayıyor.** `ScanOrderStreamAsync`
  bir sayfa 400 ya da 404 döndüğünde `break` ediyor ve o ana kadar toplanan paketleri `MergeOrderPackages` ile birleştirip
  `Success` döndürüyor ([TrendyolHttpClient.cs:359-385](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Senaryo: bölünmüş bir siparişin 1. paketi 1. sayfada, 2. paketi 3. sayfada. 3. sayfa 400 dönüyor (cursor geçersiz
  ya da filtre değişti). Sipariş tek paketle başarıyla döner, tutarlar ve durum eksik kalır. Sayfa sınırı ve tekrar
  eden cursor durumunda ise kod doğru şekilde `OrderScanIncomplete` dönüyor; 400/404'te bu tutarlılık yok.
- **[Orta][Muhtemel] V2 aramasında 400 "bu vitrinde yok" sayılıyor ve sonuç `REMOTE_ORDER_NOT_FOUND` (404) oluyor.**
  Tırnaklı tekrar denemeden sonra da 400 gelirse döngü `continue` ediyor
  ([TrendyolHttpClient.cs:289-294, 316-318](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Senaryo: pencere 14 günü aşıyor ya da parametre geçersiz; her vitrin 400 dönüyor ve sipariş "platformda yok" diye
  raporlanıyor. Bu, bir kullanıcı veya parametre hatasını uzak tarafta var olmama durumuna çeviriyor.
- **[Düşük][Muhtemel] Claim birleştirmede satır durumu en yeni gözlemden değil, kova sırasından seçiliyor.** Bir claim
  birden çok `claimItemStatus` kovasında dönerse `primary` en yeni `LastModifiedAt`'e göre seçiliyor. Satırlar ise
  `lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.RawStatus))` ile, `ClaimStatuses` dizisi sırasına göre
  alınıyor ([TrendyolHttpClient.cs:1151-1166](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Kovalar `Task.WhenAll` ile eşzamanlı okunuyor. Senaryo: okumalar arasında bir satır Created'dan Accepted'a geçiyor ve
  satır, Created kovasındaki eski kopyadan "Created" olarak kalıyor.
- **[Düşük][Kesin] Claim kovası 404 döndürürse sessizce atlanıyor** ([TrendyolHttpClient.cs:1141](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Kovanın yolu değişirse o durumdaki iadeler hiç gelmez ve hata görünmez.

### Inferences
- Belgedeki "vitrin ülkesinde say" kuralı, adaptör sınırında `StoreFrontCode` ile korunuyor. Raporlamadaki sayımın
  doğruluğu bu denetimin kapsamı dışında (Persistence ve raporlar).

### Gaps
- V2 penceresinin 14 gün/1 ay sınırları belgede "unverified"; 400 yanıtlarının gerçek nedeni bilinmiyor.

## 4. Bir pazaryeri durumu eksik bir alandan nerede çıkarılıyor?

### Takeaway
Fatura tarafı genelde dikkatli (PttAVM `IsInvoice=false` → UNVERIFIED; Trendyol fatura durumu yoksa sözleşme hatası).
Buna karşın birkaç yerde eksik alan bir duruma ya da bir varsayılan tarihe çevriliyor.

### Cited Findings
- **[Orta][Kesin] HB soru durumu yoksa "WaitingForAnswer" varsayılıyor.**
  `var rawStatus = Text(item, "status") ?? "WaitingForAnswer";`
  ([HepsiburadaQuestions.cs:167](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaQuestions.cs)).
  Senaryo: detay ya da liste yanıtında `status` gelmiyor (başka alan adı kullanılmış ya da null). Cevaplanmış veya
  süresi dolmuş soru panelde "cevap bekliyor" görünür ve operatör ikinci kez cevap gönderir. Bu, CLAUDE.md'deki
  "unknown stays unknown" kuralının doğrudan ihlali.
- **[Düşük][Kesin] Tarih yoksa `UnixEpoch` (1970) kullanılıyor.** HB soru `created`
  ([HepsiburadaQuestions.cs:171](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaQuestions.cs)), Pazarama
  sipariş tarihi `OrderDate ?? lastItemChange ?? UnixEpoch`
  ([PazaramaOrderMapper.cs:124](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaOrderMapper.cs)), N11 paket
  `lastModifiedDate ?? UnixEpoch` ([N11OrderMapper.cs:82](src/MarketplaceHub.Infrastructure/Adapters/N11/N11OrderMapper.cs)).
  Senaryo: N11'de `lastModifiedDate` eksik olan bir paket gözlemi "1970" tarihli sayılır. Olay sıralaması daha eski
  gözlemi atladığı için gerçek durum değişikliği yok sayılabilir; Pazarama'da ise sipariş 1970 tarihli rapora düşer.
- **[Düşük-Orta][Kesin] HB sipariş düzeyi fatura durumu çıkarımı null satırları atıyor.** `UniformInvoiceStatus`, durumu
  null olan satır/paketleri `Where(status => status is not null)` ile eliyor
  ([HepsiburadaJsonMapper.cs:603-613](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaJsonMapper.cs)).
  Paket düzeyindeki eşi `UniformItemInvoiceStatus` ise null'u ayrı bir değer sayıp belirsiz bırakıyor (satır 597-601).
  `QueryDeliveryAsync`, pakete özgü gözlem yoksa bu sipariş düzeyi sonuca düşüyor
  ([HepsiburadaHttpClient.cs:1092-1093](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
  Senaryo: çok paketli bir siparişte tek satır `hasInvoice:false` taşıyor, diğerlerinde alan yok. Sonuç "NOT_INVOICED"
  olur ve `DeliverAsync`'in "boş gözlendiyse gönder" ön koşulu (satır 1043-1046) yanlışlıkla sağlanır.
- [Doğru davranış] PttAVM `IsInvoice=false` UNVERIFIED sayılıyor
  ([PttAvmInvoices.cs:39-49](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmInvoices.cs)).
  Trendyol fatura durumu yoksa `TRENDYOL_INVOICE_STATUS_UNAVAILABLE` dönüyor
  ([TrendyolHttpClient.cs:917-920](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  E-Faturam'da yalnız belgelenmiş `tax-payer-not-found` problemi "mükellef değil" sayılıyor.

### Inferences
- `UnixEpoch` yedekleri gerçek bir "bilinmiyor" değerinin yerini tutuyor. `null` taşıyıp üst katmanda karar vermek
  CLAUDE.md kuralına daha uygun olur.

### Gaps
- Persistence katmanının `ORDER_PACKAGE_LINE_INVALID` sorunlu siparişleri içeri alıp almadığına bakılmadı (bkz. bölüm 6).

## 5. Ondalık ve tarih ayrıştırma kültürden bağımsız mı (tr-TR virgüllü ondalık)?

### Takeaway
Tüm ayrıştırmalar `CultureInfo.InvariantCulture` kullanıyor; kültüre bağlı (CurrentCulture) ayrıştırma bulunmadı.
Risk tersten geliyor: `NumberStyles.Number` ve `NumberStyles.Any` binlik ayırıcıya izin verdiği için, Türkçe biçimli
"12,50" metni Invariant kültürde **1250** olarak okunuyor. HB fiyatı ise bilinçli olarak tr-TR biçiminde yazılıp okunuyor.

### Cited Findings
- **[Orta][Kesin davranış / Tahmin tetiklenme] Virgüllü metin sayıları sessizce 100 kat büyüyor.**
  - Trendyol `TryDecimal` / `DecimalNullable`: `decimal.TryParse(item.ToString(), NumberStyles.Any, InvariantCulture)`
    ([TrendyolJsonMapper.cs:1147, 1162](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)).
  - `NumberStyles.Number` + Invariant: PttAVM sipariş/ürün/varyant
    ([PttAvmOrderMapper.cs:235](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmOrderMapper.cs),
    [PttAvmProductMapper.cs:113](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmProductMapper.cs),
    [PttAvmVariants.cs:163](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmVariants.cs)); N11
    ([N11Settlements.cs:90](src/MarketplaceHub.Infrastructure/Adapters/N11/N11Settlements.cs),
    [N11OrderMapper.cs:259](src/MarketplaceHub.Infrastructure/Adapters/N11/N11OrderMapper.cs)); Pazarama
    ([PazaramaOrderMapper.cs:283](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaOrderMapper.cs)); HB
    ([HepsiburadaJsonMapper.cs:1729](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaJsonMapper.cs)).
  - Senaryo: bir SOAP veya REST alanı tutarı "1234,50" metniyle döndürürse 123450 okunur; ciro, komisyon ve hakediş
    rakamları büyür. Hiçbir yardımcı "virgül var ama nokta yok" durumunu reddetmiyor. .NET, Invariant kültürde grup
    ayırıcının yerini denetlemiyor; bu çıkarım .NET'in `AllowThousands` davranışına dayanıyor.
- [Kasıtlı] HB ürün içe aktarmada fiyat tr-TR ile yazılıyor ve doğrulanıyor
  ([HepsiburadaProductPublicationComposer.cs:153](src/MarketplaceHub.Infrastructure/Persistence/HepsiburadaProductPublicationComposer.cs),
  [HepsiburadaHttpClient.cs:1508](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
  Envanter XML'i de tr-TR "0.00" biçiminde virgülle yazılıyor ([HepsiburadaHttpClient.cs:1285](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
  Yazma ve doğrulama tutarlı. HB listing XML'inin virgüllü fiyatı beklediği belgede yazmıyor.
- Tarihler: Pazarama iadeleri Türkçe ay adları için önce tr-TR, sonra Invariant deniyor
  ([PazaramaReturns.cs:189-200](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaReturns.cs)). PttAVM gün
  ekini `ddMMyy` Invariant ile okuyor ([PttAvmOrderMapper.cs:156-161](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmOrderMapper.cs)).
  Tarih tarafında kültür hatası bulunmadı.
- `PttAvmOrderMapper.Fold`, `ToLowerInvariant` ve ardından Türkçe harf katlaması yapıyor; "İPTAL" ve "İade"
  değerleri doğru katlanıyor ([PttAvmOrderMapper.cs:238-244](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmOrderMapper.cs)).

### Inferences
- Pazaryerleri JSON'da sayıları çoğunlukla number olarak gönderdiği için risk büyük ölçüde metin yedek yollarında. Yine
  de metin dalı var ve sessiz büyütme, CLAUDE.md'nin "yanlış sayı göstermek eksik sayıdan kötüdür" ilkesine ters düşüyor.

### Gaps
- Hiçbir pazaryerinin tutarları virgüllü metin olarak döndürdüğüne dair canlı kanıt bulunmadı (canlı SQL çalıştırılmadı).

## 6. Hata yönetimi, sayfalama, webhook, HttpClient ve iptal belirteci kusurları

### Takeaway
En önemli sorunlar: `JsonElement.TryGetXxx` çağrılarının ValueKind denetimi olmadan yapılması (null alan →
yakalanmayan `InvalidOperationException`), E-Faturam'da `x-request-id` başlığı varken problem tipinin hiç okunmaması
("mükellef değil" ve "fatura bulunamadı" kararlarını bozuyor), ofset/sayfa tabanlı sayfalamanın kayan listelerde kayıt
atlaması ve Shopify GraphQL hatalarının yeniden denenmez sınıfa düşmesi.

### Cited Findings

**E-Faturam (fatura sağlayıcı)**
- **[Yüksek][Kesin kod / Muhtemel tetiklenme] Problem tipi yalnız `x-request-id` başlığı yoksa okunuyor.**
  `remoteId ??= await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(...)`
  ([TrendyolEFaturamHttpClient.cs:303](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/TrendyolEFaturamHttpClient.cs)).
  Mükellef ve fatura kararları ise aynı alanın problem referansı olmasına dayanıyor:
  `RemoteRequestId: TaxPayerNotFoundReference` (satır 184) ve `IsInvoiceNotFound(notFound.RemoteRequestId)` (satır 115).
  Senaryo: ağ geçidi 404 yanıtına `x-request-id` ekliyor. `tax-payer-not-found` sonucu `NotRegistered` yerine `Unknown`
  olur ve e-Arşiv/e-Fatura kararı verilemez. Belirsiz sonuçlu bir gönderimin UUID okumasında `earchive-not-found` ise
  `NotFoundAtProviderCode`'a çevrilemez, fatura "bilinmiyor" durumunda takılır.
- **[Orta][Kesin kod / Muhtemel tetiklenme] `gibStatusCode` null ya da metin gelirse durum okuması çöküyor.**
  `NullableInteger` → `value.TryGetInt32` ValueKind denetimi yapmıyor; Null veya String için
  `InvalidOperationException` fırlatılıyor
  ([TrendyolEFaturamJsonMapper.cs:97](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Mapping/TrendyolEFaturamJsonMapper.cs)).
  `QueryStatusAsync` yalnız `JsonException` yakalıyor
  ([TrendyolEFaturamHttpClient.cs:121-122](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/TrendyolEFaturamHttpClient.cs)).
  Senaryo: GİB'e henüz iletilmemiş bir e-Arşiv'de `"gibStatusCode": null` geliyor. İstisna adaptörden kaçar ve
  durum/iptal işi `AdapterResult` yerine istisnayla düşer. Testlerde `gibStatusCode` hiç geçmiyor.
- **[Düşük][Kesin] Kanonik yük `Decimal()` için de aynı desen geçerli** (`value.TryGetDecimal`, ValueKind denetimi yok):
  [TrendyolEFaturamCanonicalPayload.cs:221](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamCanonicalPayload.cs).
  `SubmitAsync` yalnız JsonException, ArgumentException ve FormatException yakalıyor (satır 77). Örneğin
  `"DiscountAmount": null` gelirse yakalanmayan `InvalidOperationException` oluşur. Bu, ağ isteğinden önce olduğu için
  mükerrer fatura riski yaratmıyor.
- **[Düşük][Kesin] Durum okuması `content` dizisinden ilk öğeyi UUID eşleşmesine bakmadan alıyor**
  ([TrendyolEFaturamJsonMapper.cs:72-74](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Mapping/TrendyolEFaturamJsonMapper.cs)).
- [Doğru davranış] Gönderim zaman aşımı, ağ hatası, 5xx ve 409 "sonuç bilinmiyor" sayılıyor ve kör yeniden gönderim
  yapılmıyor ([InvoiceSubmissionOutcomePolicy.cs:17-21](src/MarketplaceHub.Application/InvoiceSubmissionOutcomePolicy.cs)).
  İptal yalnız `EARSIVFATURA` için çağrılıyor
  ([InvoicingJobProcessor.cs:2213](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)).

**Trendyol**
- **[Orta][Kesin kod / Muhtemel tetiklenme] Fatura linki gövdesi `invoiceDateTime: null` gelirse çöküyor.**
  Teslim yükü `["invoiceDateTime"] = invoice.IssuedAt?.ToUnixTimeMilliseconds()` ile üretiliyor ve `IssuedAt` null ise
  JSON null oluyor ([InvoicingJobProcessor.cs:1260](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)).
  `InvoiceLinkBody` bu alanda `issuedAt.TryGetInt64` çağırıyor; mikro ihracat dalında da `invoiceDateTime.TryGetInt64`
  var ([TrendyolHttpClient.cs:866, 892](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Null bir JsonElement'te bu çağrı `InvalidOperationException` fırlatır; teslim işi hata sonucu yerine istisnayla
  düşer. Mikro ihracat için beklenen "invoiceDateTime zorunludur" doğrulama mesajı da hiç üretilmez.
- **[Düşük][Kesin] Claim denetim kaydında `date` null ya da metin gelirse istisna fırlıyor.** Yorumda "tarihsiz kayıt
  atlanır" yazıyor, oysa `date.TryGetInt64` null değerde fırlatıyor
  ([TrendyolJsonMapper.cs:792](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)).
  Çağıran yalnız JsonException yakalıyor ([TrendyolHttpClient.cs:150](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Tanınmayan bir şekil ise sessizce `[]` dönüyor (satır 786).
- **[Düşük][Muhtemel] Sipariş taraması döngüsü iptalde kısmi sonuç döndürebiliyor.** Döngü
  `while (nextCursor is not null && !cancellationToken.IsCancellationRequested)` koşuluyla çıkıyor ve ardından toplanan
  paketleri başarı olarak birleştiriyor ([TrendyolHttpClient.cs:383-385](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  Yanıt ile döngü denetimi arasında iptal gelirse eksik sipariş döner. Çoğu durumda `SendAsync` zaten
  `OperationCanceledException` fırlattığı için pencere dar.

**Hepsiburada**
- **[Orta][Muhtemel] Ofset sayfalaması küçülen listelerde kayıt atlıyor.** Ödenmiş sipariş listesi yalnız OPEN/UNPACKED
  siparişleri içeriyor (belge). `nextOffset = offset + Items.Count`
  ([HepsiburadaHttpClient.cs:545](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaHttpClient.cs)).
  Senaryo: 1. sayfa okunurken 1. sayfadaki bir sipariş paketleniyor ve listeden düşüyor. 2. sayfanın ilk kaydı 1. sayfaya
  kayar ve bu turda hiç okunmaz. Aynı desen açık paket listesinde (satır 670) ve talep listelerinde (satır 959) de var.
  Kurtarma senkronu kısmen telafi ediyor.
- **[Düşük][Muhtemel] Paylaşılan kota sınırlayıcısı hata verirse istek kotasız gönderiliyor.** Sınırlayıcıdaki herhangi
  bir istisna yutuluyor ve istek kota beklemeden çıkıyor
  ([HepsiburadaRateLimitHandler.cs:28-32](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaRateLimitHandler.cs)).
  PostgreSQL kesintisinde tüm API ve worker süreçleri sınırsız istek atar; katalogda bu IP blokuna (bkz. bölüm 1) yol
  açabilir. Aynı fail-open davranışı diğer sınırlayıcılarda da var [Tahmin, ayrıca okunmadı].
- **[Düşük][Kesin] HB finans sayfası `count` null ya da metin gelirse çöküyor.** `total.TryGetInt32` ValueKind denetimi
  yapmıyor ([HepsiburadaFinance.cs:59](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaFinance.cs)).

**N11**
- **[Orta][Muhtemel] Son değişiklik tarihine göre artan sıralı sayfa numarası, sayfalama sırasında değişen kayıtları
  kaydırıyor.** `orderByField=true&orderByDirection=ASC` ve `page=N` kullanılıyor
  ([N11Orders.cs:20, 31](src/MarketplaceHub.Infrastructure/Adapters/N11/N11Orders.cs)). Senaryo: 0. sayfadaki bir paket
  okuma sırasında güncelleniyor ve listenin sonuna gidiyor. 1. sayfanın ilk kaydı 0. sayfaya kayar ve atlanır. Filigran
  en yeni `lastModifiedDate`'e ilerlerse, atlanan kaydın eski zamanı sonraki turun 600 sn'lik örtüşmesinin dışında
  kalabilir; tam taramaya kadar kayıp olur.
- **[Düşük][Kesin] SOAP'ta 500 dönen her fault `Remote5xx` (yeniden denenir) sayılıyor.** SOAP 1.1 istemci hataları da
  HTTP 500 ile gelir. Gövde `{operation}Response` içermiyorsa kimlik ya da şema hatası da `N11_REMOTE_ERROR` olarak
  sonsuz yeniden denenir ([N11Soap.cs:41-42](src/MarketplaceHub.Infrastructure/Adapters/N11/N11Soap.cs)). Bunun tersine,
  HTML bir 401/403 `N11_SOAP_INVALID` (ContractViolation) olarak sınıflanır ve kimlik hatası olarak görünmez.

**Pazarama ve PttAVM**
- **[Orta][Muhtemel] Geçersiz satır siparişten atılıyor ama sipariş alınıyor.** Kalem kimliği, adet ya da fiyatı eksik
  olan satır `ORDER_PACKAGE_LINE_INVALID` sorunuyla atlanıyor; sipariş kalan satırlarla ve eksik toplamla içeri alınıyor
  ([PazaramaOrderMapper.cs:74-78](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaOrderMapper.cs),
  [PttAvmOrderMapper.cs:58-62](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmOrderMapper.cs)). Senaryo: iki
  kalemli siparişin bir kaleminde `salePrice` null. Sipariş tek kalemle ve düşük ciroyla görünür; fatura tutarı da yanlış
  olabilir. Sorun kodu raporlanıyor, ama siparişin bu durumda karantinaya alınıp alınmadığı Persistence'ta doğrulanmadı.
- [Doğru davranış] Pazarama 401'de token'ı unutup bir kez yeniden deniyor; `success:false` zarfı hata sayılıyor
  ([PazaramaHttpClient.cs:167-190](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaHttpClient.cs)).
  PttAVM `SaveInvoince` belirsiz yanıtta yeniden okumaya gidiyor
  ([PttAvmInvoices.cs:28-34](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmInvoices.cs)).

**Shopify**
- **[Orta][Muhtemel] GraphQL `errors` içindeki geçici hatalar yeniden denenmeyen sınıfa düşüyor.** THROTTLED, ACCESS_DENIED
  ve MAX_COST dışındaki her kod (ör. HTTP 200 ile gelen `INTERNAL_SERVER_ERROR`) `AdapterErrorClass.Validation` /
  `SHOPIFY_GRAPHQL_ERROR` oluyor ([ShopifyHttpClient.cs:1241-1269](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyHttpClient.cs)).
  Senaryo: Shopify tarafındaki anlık bir iç hata, sipariş senkronunu ya da stok yazımını kalıcı doğrulama hatası olarak
  bitirir. Ayrıca yalnız `errors[0]` inceleniyor.
- **[Düşük][Muhtemel] Shopify webhook tazelik penceresi yeniden teslimleri kalıcı olarak düşürüyor.**
  `X-Shopify-Triggered-At` ±5 dk dışındaysa 200 `SHOPIFY_WEBHOOK_STALE` dönülüyor ve iş kuyruğa alınmıyor
  ([ShopifyWebhookVerifier.cs:57-59](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyWebhookVerifier.cs)).
  Shopify yeniden denemelerde ilk tetiklenme zamanını korur [Tahmin]. Senaryo: ilk teslim sunucu yavaşlığından düşüyor,
  5 dk sonraki yeniden deneme "eski" sayılıyor ve 200 alan Shopify bir daha denemiyor. Telafiyi zamanlanmış sipariş
  kontrolü yapıyor. Başlık imzalı olmadığından saldırgana karşı da koruma sağlamıyor; tekrarlara karşı asıl koruma
  gövde özeti.
- **[Düşük][Tahmin] ROUTE_TOKEN aboneliğinde imza yok.** Gizli rotayı bilen herkes gövde gönderebiliyor
  ([ShopifyWebhookVerifier.cs:43-49](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyWebhookVerifier.cs)).
  Yorum, gövdenin yalnız siparişi adlandırdığını ve siparişin Shopify'dan yeniden okunduğunu söylüyor; bunun işleyicide
  gerçekten böyle olduğu doğrulanmadı.

**Yönlendirici ve webhook doğrulayıcı**
- **[Düşük][Kesin kod / Tahmin veri] Platform kodu normalizasyonu tutarsız.** `MarketplaceWebhookVerifier` platform
  kodunu birebir (`"SHOPIFY"`, `"HEPSIBURADA"`, `"TRENDYOL"`) karşılaştırıyor ve kiracı filtresi uygulamıyor
  ([MarketplaceWebhookVerifier.cs:18-28](src/MarketplaceHub.Infrastructure/MarketplaceWebhookVerifier.cs)). Router ise
  `Trim().ToUpperInvariant()` uyguluyor ([MarketplacePortRouter.cs:40](src/MarketplaceHub.Infrastructure/MarketplacePortRouter.cs)).
  Kod "Trendyol" ya da boşluklu kayıtlıysa router çalışır ama webhook `WEBHOOK_PLATFORM_UNSUPPORTED` döner.
- **[Düşük][Kesin] Bilinen ama portu olmayan bir platform istisna fırlatıyor.** `Route`, platformda istenen port yoksa
  `InvalidOperationException` fırlatıyor (satır 35-37). PttAVM'e yönlenen ve henüz desteklenmeyen bir port çağrısı (ör.
  soru portu) istisnayla düşer; bu bilinçli bir tasarım ("wiring bug"). `ReportCapability` PttAVM'i hiç geçirmediği için
  orada güvenli şekilde "Unsupported" dönüyor (satır 109).
- [Doğru davranış] HB ve Trendyol Basic/API key doğrulaması sabit zamanlı karşılaştırma kullanıyor
  ([HepsiburadaWebhookVerifier.cs:58-63](src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaWebhookVerifier.cs)).
  Shopify HMAC, ham gövde üzerinde `FixedTimeEquals` ile doğrulanıyor. `X-Ravencia-Event-Path` başlığı istemciden
  gelirse API'de siliniyor ([MarketplaceEndpoints.cs:370-371](src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs)).

### Inferences
- `TryGetInt32/Int64/Decimal` ValueKind denetimi olmadan çağrılıyor ve uzak yanıt ayrıştırmalarında
  `InvalidOperationException` adaptör sınırını aşıyor. Bu desen birden çok adaptörde tekrarlanıyor (E-Faturam,
  Trendyol, HB finans; Shopify cost alanları ve PttAVM iç yük okumaları da aynı biçimde yazılmış).
- Yazma işlemlerinde zaman aşımı genelde `TransientNetwork` sınıfında yeniden deneniyor. Fatura gönderimi için "sonuç
  bilinmiyor" politikası var; diğer yazmalarda (HB paket oluşturma, Trendyol paket durum PUT'u) yeniden denemenin
  idempotent olup olmadığı adaptör düzeyinde garanti edilmiyor [Tahmin, işleyici tarafı okunmadı].

### Gaps
- Persistence ve job processor tarafı (yeniden deneme politikası, sorunlu siparişlerin karantinası, webhook gövdesinin
  nasıl kullanıldığı) kapsam dışında kaldı ve doğrulanmadı.
- `MarketplaceCircuitBreakerHandler`, `SharedRateLimiter`, Pazarama/PttAVM/N11 hız sınırı işleyicileri ile ürün ve
  stok yazma dosyaları (`*ProductWrites.cs`, `*PriceInventory.cs`) zaman kısıtı nedeniyle ayrıntılı okunmadı.
- E-Faturam ağ geçidinin hata yanıtlarına `x-request-id` ekleyip eklemediği canlı loglardan doğrulanmadı;
  `/prod-logs` çalıştırılmadı.
