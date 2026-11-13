# Katalog yazma yolları: hata denetimi (ürün açma/güncelleme, batch sonucu, onay, eşleme, varyant aile, fiyat/stok, komisyon/promosyon)

Kapsam: `d4cb6ed` (5 Kas 2026). Salt okunur denetim. Canlı sunucuya ve canlı SQL'e dokunulmadı. Ortamda `dotnet` olmadığı için testler
çalıştırılmadı. `adapters.md`, `domain_application.md` ve `frontend_part2.md` içindeki bulgular tekrar edilmedi. `ChannelOfferPricePolicy`
yuvarlaması ile `CommissionTariffBands` orada zaten temiz bulunmuştu.
Önem: Kritik/Yüksek/Orta/Düşük. Güven: [Kesin] kod yolu izlendi; [Muhtemel] kod kesin ama tetiklenmesi veriye ya da uzak sisteme bağlı; [Tahmin] doğrulanmadı.

## 1. Yanlış ürün yanlış listinge gidiyor mu (barkod/SKU/model kodu anahtarı, Türkçe ı/İ)?

### Takeaway
En ciddi bulgu, Trendyol ve Hepsiburada "yalnız eşleme" içe aktarmasındaki model kodu yedeği. Barkod ve SKU tutmazsa, aynı model
kodlu tek panel varyantını renk ve beden bakmadan ilk gelen uzak varyanta bağlıyor. Bu bağdan sonra stok, fiyat ve siparişler yanlış
listinge akıyor. Türkçe harf tarafında iki ayrı normalizasyon, ortak anahtarı (`CatalogKeyNormalizer.Key`) kullanmıyor; bu yüzden ı ve
aksanlı değerler eşleşmiyor.

### Cited Findings
- **[Yüksek][Kesin kod / Muhtemel tetiklenme] Model kodu yedeği, tek adaylı durumda renk ve bedene bakmadan bağlıyor.**
  `MapExistingCatalogProduct` her uzak varyant için önce barkod, sonra SKU, en son model kodu adaylarını topluyor
  (`matches = barcodeMatches.Count > 0 ? … : skuMatches.Count > 0 ? … : modelMatches`). `matches.Count == 1` ise varyant bağlanıyor.
  Renk ve beden daraltması (`SameColorAndSize`/`HasOptionValues`) yalnız `crossMatchCodes` açıkken ve aday birden çoksa çalışıyor.
  Tek çağrı yerinde `crossMatchCodes: flatCatalog` ve `preferBarcode: isShopify` geçiliyor. Yani Trendyol ve Hepsiburada'da
  `preferBarcode=false` ve `crossMatchCodes=false`.
  Senaryo: Panelde "M1" model kodlu ürünün tek varyantı var (Siyah S). Trendyol'daki "M1" içeriğinde Siyah S, Siyah M ve Siyah L var ve
  barkodları panelden farklı. Döngüde ilk gelen uzak varyant (ör. Siyah L) panelin Siyah S varyantına bağlanıyor. Diğerleri
  "zaten bağlı" diye eşleşmemiş kalıyor. Sonra panelin S stoğu L listingine gidiyor ve L siparişleri S stoğundan düşüyor.
  Model kodu karşılaştırması da `CatalogKeyNormalizer.Key` ile yapılıyor, yani "M-1", "m 1" ve "M_1" aynı anahtara düşüyor.
  — [MarketplaceJobProcessor.cs:7280-7298](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs), çağrı yeri
  [MarketplaceJobProcessor.cs:6208](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)
- **[Orta][Kesin kod / Muhtemel veri] Kod eşleştirmesi ayraçları ve büyük-küçük harfi siliyor; farklı kodlar çakışabiliyor.**
  Aynı fonksiyonda barkod ve SKU, `CatalogKeyNormalizer.SameKey` ile karşılaştırılıyor. Bu anahtar aksanları atıyor, büyük harfe
  çeviriyor, boşluk/`_`/`/`/`.` dizilerini tek `-` yapıyor ve 160 karakterde kesiyor
  ([CatalogKeyNormalizer.cs:36-52](src/MarketplaceHub.Application/CatalogKeyNormalizer.cs)).
  Sonuç: "ELB/01" ile "ELB-01", "abc.01" ile "ABC_01" ve "ÇANTA1" ile "CANTA1" aynı kod sayılıyor. Uzaktaki bir varyant, panelde gerçekte
  farklı olan ama aynı anahtara düşen başka bir varyantla eşleşip ona bağlanabiliyor. Barkod eşleşmesi SKU'dan önce geldiği için, kesin
  SKU eşleşmesi olsa bile katlanmış bir barkod eşleşmesi onu geçiyor
  ([MarketplaceJobProcessor.cs:7271-7298](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).
- **[Orta][Kesin] Trendyol yayınında seçenek değeri `Trim().ToUpperInvariant()` ile eşleniyor; ortak anahtar kullanılmıyor.**
  `NormalizeLabel` yalnız `Trim().ToUpperInvariant()` yapıyor. Seçenek etiketi "Kırmızı", özellik değeri "KIRMIZI" ise
  ("KıRMıZı" ≠ "KIRMIZI") ya da "Açık Mavi"/"ACIK MAVI" gibi farklar varsa `localValue` null kalıyor. Bunun iki etkisi var:
  (a) Beden gibi renk dışı bir seçenek, eşlenmiş `attributeValueId` yerine serbest metinle gidiyor (`customAttributeValue`), serbest
  metin kabul edilmiyorsa yayın `OPTION_VALUE_MAPPING_REQUIRED` ile duruyor. (b) Web Color türetmesi `localValue is not null` şartına
  bağlı olduğu için o varyanta Web Color hiç yazılmıyor. Web Color zorunluysa yayın `REQUIRED_ATTRIBUTE_MISSING` alıyor.
  — [ProductPublicationComposer.cs:253, 260, 263-283, 460](src/MarketplaceHub.Infrastructure/Persistence/ProductPublicationComposer.cs)
- **[Düşük-Orta][Kesin] `CatalogColorMappingPolicy.Normalize` noktasız ı'yı katlamıyor; "Fıstık" ve "Yavruağzı" yedekleri hiç çalışmıyor.**
  `Normalize`, `char.ToUpperInvariant` kullanıyor. `CatalogKeyNormalizer`'ın kendi yorumuna göre invariant büyük harf 'ı' harfini
  olduğu gibi bırakıyor ([CatalogKeyNormalizer.cs:44-46](src/MarketplaceHub.Application/CatalogKeyNormalizer.cs)).
  Bu yüzden "Fıstık" "FıSTıK" ve "Yavruağzı" "YAVRUAGZı" oluyor; tablo anahtarları ise "FISTIK" ve "YAVRUAGZI". Doğru Türkçe yazılmış
  bu iki renk için Web Color yedek eşlemesi (FISTIK→YESIL, YAVRUAGZI→PEMBE) hiç devreye girmiyor. Bu hem yayında (Web Color değeri
  bulunamıyor) hem içe aktarmada geçerli.
  — [CatalogColorMappingPolicy.cs:23-24, 33-43](src/MarketplaceHub.Application/CatalogColorMappingPolicy.cs); kullanımlar
  [MarketplaceJobProcessor.cs:7018, 7161](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)
- **[Düşük-Orta][Muhtemel] PTT AVM fiyat-stok kapsamı model kodunu marka bakmadan ve ham metinle grupluyor.**
  `BuildPttAvmAsync`, bir ürünün gönderiminde aynı ham `ModelCode` değerini taşıyan bütün ürünleri kapsama alıyor (satır 577). Ürün
  linki olmayan ürünü de `variant.ModelCode == productLink.ExternalCode` eşitliğiyle başka bir ürünün PTT barkoduna yöneltiyor
  (satır 608, 616-617). `ProductFamilyKeyPolicy` ise aileyi marka + ortak anahtar olarak tanımlıyor
  ([ProductFamilyKeyPolicy.cs:3-15](src/MarketplaceHub.Application/ProductFamilyKeyPolicy.cs)).
  Senaryo: İki markada aynı model kodu "100" var. B markasının PTT'ye bağlı varyantları, A markasının PTT ürününün (barkod "100")
  varyantı olarak, A'nın baz fiyatına göre fark fiyatıyla gönderiliyor. Tersine "abc-01" ve "ABC-01" ise aynı aile sayılmıyor.
  — [TrendyolWriteComposers.cs:572-619](src/MarketplaceHub.Infrastructure/Persistence/TrendyolWriteComposers.cs)
- **[Düşük][Tahmin] Barkod çakışması kontrolü yayın içinde büyük-küçük harfe duyarsız, bağlantı genelinde duyarlı.**
  Kart içinde `Distinct(OrdinalIgnoreCase)` uygulanıyor. Bağlantı genelindeki `ConnectionDuplicateAsync` ise PostgreSQL'in
  büyük-küçük harfe duyarlı `IN` sorgusunu kullanıyor ve Trendyol için yalnız listing barkodlarına bakıyor (`linkKeys = []`). Bu
  yüzden başka bir ürünün içe aktarılmış bağındaki (`MarketplaceVariantLinks.ExternalBarcode`) aynı barkod kontrol edilmiyor.
  — [ProductPublicationComposer.cs:127-129, 384-404](src/MarketplaceHub.Infrastructure/Persistence/ProductPublicationComposer.cs)

### Inferences
- Model kodu yedeği, tek varyantlı panel ürünlerinde sessiz yanlış bağ üretiyor. "PRODUCT_MAPPING_UNMATCHED" kaydı da yalnız `flatCatalog`
  pazaryerlerinde yazılıyor (`reportUnmatched: flatCatalog`), bu yüzden Trendyol/HB'de bu durum görünmüyor bile. Düzeltme önerisi: model
  yedeğinde renk/beden daraltmasını her pazaryeri için zorunlu tutmak ya da tek adayı yalnız uzak ürünün de tek varyantı varsa kabul etmek.
- Panelde en az üç ayrı "anahtar" var: `CatalogKeyNormalizer.Key`, `NormalizeLabel` ve `CatalogColorMappingPolicy.Normalize`. ı/İ ve
  aksan farkları bunların arasındaki boşluktan doğuyor. Bunları tek `Key` çağrısına indirmek iki bulguyu birlikte kapatır.

### Gaps
- Canlıda tek varyantlı panel ürünü ile çok varyantlı uzak içerik eşleşmesinin kaç kez olduğu ölçülmedi (canlı SQL kapsam dışı).
- Trendyol'un barkodu büyük-küçük harfe duyarlı tutup tutmadığı `marketplace-docs/trendyol.md` içinde yazmıyor.

## 2. Batch sonuçları doğru listinge mi yazılıyor; onay durumu yanlış mı çıkarılıyor?

### Takeaway
Ürün açma batch'inin okunmasında yanlış atama bulunmadı: anahtarlar tek tek eşleniyor, tekrar eden ya da bilinmeyen anahtar manuel
incelemeye gidiyor, listede olmayan kalem "bilinmiyor" kalıyor. Onay tarafında Trendyol için "hepsi NOT_FOUND ise yayını sıfırla"
kuralı, oluşturma gecikmesi varsa riskli olabilir; bu bir tahmin.

### Cited Findings
- [Doğru davranış] Poll aşaması yalnız bu payload hash'ini taşıyan listingleri okuyor. Sonuçta tekrar eden anahtar ya da gönderilmemiş
  anahtar `PRODUCT_BATCH_CONTRACT_INVALID` ile manuel incelemeye gidiyor. Eksik kalem 30 dakika bekletiliyor, sonra
  `CREATE_RESULT_UNKNOWN` olarak kalıyor ve "oluşturuldu" ya da "reddedildi" diye varsayılmıyor. Trendyol'da SUCCESS/FAILED dışındaki
  satırlar da bekleyen sayılıyor.
  — [MarketplaceJobProcessor.cs:651-745](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs),
  [ProductBatchResultPolicy.cs:8-27](src/MarketplaceHub.Application/ProductBatchResultPolicy.cs),
  [TrendyolJsonMapper.cs:697-714](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)
- [Doğru davranış] Onay eşleştirmesinde tanınmayan Trendyol durumu (`UNKNOWN`) "LIVE" ya da "REJECTED" sayılmıyor, `MANUAL_REVIEW`
  oluyor. Onaylı yanıtta contentId veya variantId eksikse, aynı variantId iki barkoda dönmüşse ya da kimlik başka bir yerel varyantta
  duruyorsa iş manuel incelemeye gidiyor.
  — [MarketplaceJobProcessor.cs:904-985](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs),
  [TrendyolJsonMapper.cs:662-695](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)
- **[Orta][Tahmin] Trendyol'da bütün barkodlar ilk kontrolde NOT_FOUND dönerse yayın sıfırlanıyor.**
  `ShouldResetMissingPublication` yalnız n11, Pazarama ve PTT AVM için kapalı. Trendyol'da, batch kalemi SUCCESS olsa ya da sonucu
  bilinmese (`CREATE_RESULT_UNKNOWN`) bile, onaylı ve onaysız listeler barkodu henüz göstermiyorsa `ResetMissingPublication` çalışıyor.
  Kullanıcı ürünü yeniden gönderirse çift içerik riski doğuyor. Policy yorumu yalnız Pazarama, n11 ve PTT AVM'nin yeni ürünü "bulunamadı"
  diye cevapladığını söylüyor; Trendyol listelerinin indeksleme gecikmesi belgelenmemiş.
  — [MarketplaceJobProcessor.cs:905-908](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs),
  [ProductApprovalReconciliationPolicy.cs:7-18](src/MarketplaceHub.Application/ProductApprovalReconciliationPolicy.cs)
- **[Düşük][Kesin] Onaylı listede `archived` önceliği "onSale=false" farkını görmüyor.** `ApprovedPublicationStatus` yalnız
  blacklisted/locked/archived bayraklarına bakıyor, kalanı `APPROVED` → `LIVE` sayıyor. Satışa kapalı ama arşivlenmemiş bir ürün de
  LIVE görünüyor. Bu bir durum eksikliği; yanlış listinge yazma değil.
  — [TrendyolJsonMapper.cs:671-675](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)
- [Kontrol edildi] Hepsiburada onayında, yeniden gönderimde canlı varyantların sonuç dışında kalması sorunu doğmuyor. Yalnız n11
  derleyicisi canlı varyantları gönderimden çıkarıyor
  ([N11ProductPublicationComposer.cs:61](src/MarketplaceHub.Infrastructure/Persistence/N11ProductPublicationComposer.cs)); HB
  derleyicisi bütün varyantları gönderdiği için `lines.Count != listings.Count` kontrolü yanlış alarm vermiyor.

### Inferences
- Batch ve onay akışı "bilinmeyen bilinmeyen kalır" kuralına genel olarak uyuyor. Kalan risk, Trendyol'daki sıfırlama kuralının
  zamanlaması.

### Gaps
- İlk onay kontrolünün gönderimden kaç dakika sonra koştuğu ve Trendyol listelerinin yeni barkodu ne kadar sürede gösterdiği canlı
  loglardan doğrulanmadı.

## 3. Fiyat ve stok: komisyon/kanal markup, yuvarlama, liste < satış, negatif ya da rezerve dahil stok

### Takeaway
Stok her yolda `Available − SafetyStock` (Available = OnHand − Reserved) ile hesaplanıp aşağı yuvarlanıyor ve 0'ın altına inmiyor.
Liste fiyatı her yolda `max(liste, satış)` ile korunuyor. Kodda komisyon ya da kanal markup'ı uygulayan bir fiyat kuralı yok: kanal
teklifinin fiyatı olduğu gibi gönderiliyor, bu yüzden "markup'sız gönderim" diye bir hata yolu da yok. Asıl sorun PTT AVM'nin varyant
fark fiyatlarında.

### Cited Findings
- **[Orta][Muhtemel] PTT AVM: gönderilmeyen varyantlar yeni baz fiyata göre kayıyor.**
  Bir ürünün fiyatı değişince "her varyant" gönderiliyor. Ama bu kümeden şu varyantlar sessizce düşüyor: MAIN stok kaydı olmayanlar
  (`rows.Where(row => row.Inventory is not null)`), sağlık durumu yüzünden atlananlar (`SkipsPush`), ACTIVE olmayan teklifler ve
  renk/beden çıkarılamayanlar (`attributes.Count == 0 → continue`). Baz fiyat `priced.Min(...)` yalnız kalan varyantlardan hesaplanıyor.
  Kodun kendi yorumuna göre PTT AVM gönderilmeyen varyantın eski farkını koruyor. Sonuç: en ucuz varyant bu kümeden düştüyse baz fiyat
  yükseliyor ve gönderilmeyen varyantın PTT'deki fiyatı (yeni baz + eski fark) panel fiyatından farklı oluyor.
  — [TrendyolWriteComposers.cs:556-562, 619-690](src/MarketplaceHub.Infrastructure/Persistence/TrendyolWriteComposers.cs)
- **[Orta][Muhtemel] PTT AVM varyant fiyatı hakkında iki varsayım birbiriyle çelişiyor.**
  Ürün açma derleyicisi model ailesindeki farklı fiyatları reddediyor ("PTT AVM'de bir ürünün varyantları aynı fiyatla satılır",
  `PTTAVM_VARIANT_PRICE_DIFFERS`). Fiyat-stok gönderimi ise her varyanta `SalePrice − basePrice` farkı yazıyor. Açıldıktan sonra
  fiyatları ayrışan bir ailede, ya fark alanı PTT'de yok sayılıyor (varyantlar baz fiyattan satılır) ya da açma kuralı gereksiz.
  `marketplace-docs/pttavm.md` iki davranıştan hiçbirini belgelemiyor.
  — [PttAvmProductPublicationComposer.cs:253-254](src/MarketplaceHub.Infrastructure/Persistence/PttAvmProductPublicationComposer.cs),
  [TrendyolWriteComposers.cs:674-680](src/MarketplaceHub.Infrastructure/Persistence/TrendyolWriteComposers.cs),
  [PttAvmPriceInventory.cs:78-88](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmPriceInventory.cs)
- **[Düşük][Kesin] PTT AVM: toplam stok 9.999'u aşan ya da 100'den çok varyantlı ürün hiç gönderilmiyor ve uyarı vermiyor.**
  `variants.Sum(x => x.Quantity) > MaxQuantity` olduğunda ürün sessizce atlanıyor. Teklif `settle` edilmediği için her otomatik turda
  yeniden deneniyor ve kullanıcıya bir neden gösterilmiyor (`Refuse` çağrılmıyor). Tek varyantlı ürün ise 9.999'a kırpılarak
  gönderiliyor; varyantlı üründe kırpma yok.
  — [TrendyolWriteComposers.cs:683-688](src/MarketplaceHub.Infrastructure/Persistence/TrendyolWriteComposers.cs),
  [PttAvmPriceInventory.cs:73-98](src/MarketplaceHub.Infrastructure/Adapters/PttAvm/PttAvmPriceInventory.cs)
- [Doğru davranış] Stok her yolda `Math.Max(0, Available − SafetyStock)` ile hesaplanıp aşağı yuvarlanıyor ve platform sınırıyla
  kırpılıyor (Trendyol 20.000, n11 999.999, PTT AVM 9.999).
  — [TrendyolWriteComposers.cs:281-283, 441, 490, 543, 643](src/MarketplaceHub.Infrastructure/Persistence/TrendyolWriteComposers.cs),
  [TrendyolStockRules.cs:9](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolStockRules.cs)
- [Doğru davranış] Giden liste fiyatı `max(Outbound(liste), Outbound(satış))`. n11 ve Pazarama 2 haneye yuvarladıktan sonra yeniden
  `max` alıyor. Veritabanında `ck_channel_offer_prices` (ListPrice ≥ SalePrice) kısıtı var. Hepsiburada ürün açmada fiyat tr-TR
  biçimiyle ("0,00") yazılıyor; bu kasıtlı ve adapters.md'de belirtilmiş.
  — [TrendyolWriteComposers.cs:718-722, 734-750](src/MarketplaceHub.Infrastructure/Persistence/TrendyolWriteComposers.cs),
  [CatalogModelConfiguration.cs:266](src/MarketplaceHub.Infrastructure/Persistence/CatalogModelConfiguration.cs)
- [Kontrol edildi] Kodda "markup", "price rule" ya da "commission" ile giden fiyatı değiştiren bir kural yok (grep). Komisyon tarifeleri
  (`CommissionTariffService`) ve promosyon dosyaları (`PromotionOfferService`) yalnız okuma ve görüntüleme için kullanılıyor; teklif
  fiyatına yazmıyor. Bu yüzden "promosyon iki kez uygulandı" senaryosunun bir yazma yolu yok.
  — [PromotionOfferService.cs:92-137, 253-300](src/MarketplaceHub.Infrastructure/Persistence/PromotionOfferService.cs)
- **[Düşük][Kesin] Promosyon satırlarındaki maliyet ve renk eşlemesi bağlantıya bağlı değil ve SKU'ya düşüyor.** `VariantFactsAsync`
  kiracının bütün varyantlarında barkod **veya** SKU arıyor. `Of(code)` önce barkodda, bulamazsa SKU'da arıyor ve her ikisinde de
  `TryAdd` ile ilk geleni alıyor. Pazaryeri barkodu başka bir ürünün SKU'suyla aynıysa, gösterilen maliyet ve kâr hesabı yanlış
  varyanttan geliyor. Bu bir gösterim hatası; pazaryerine yazmıyor.
  — [PromotionOfferService.cs:253-289](src/MarketplaceHub.Infrastructure/Persistence/PromotionOfferService.cs)

### Inferences
- Fiyat ve stok hattında negatif stok, rezervi de sayan stok ya da liste < satış ihlali bulunmadı. Risk PTT AVM'nin varyant modelinde
  toplanıyor: "baz fiyat + fark" varsayımı, ürün açmadaki "tek fiyat" kuralıyla çelişiyor ve kısmi gönderimde fiyatları kaydırabiliyor.

### Gaps
- PTT AVM `UpdateProductsStockPrice` çağrısının varyant `Price` alanını fark olarak mı uyguladığı, yoksa yok mu saydığı resmi belgeyle
  doğrulanamadı. Çevrimdışı belge klasörü (`C:/Users/emrek/...`) bu ortamda yok.
- Canlıda fiyatları ayrışmış PTT AVM ailesi olup olmadığı SQL ile sayılmadı.
