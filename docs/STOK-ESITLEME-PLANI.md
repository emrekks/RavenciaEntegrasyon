# Stok Eşitleme Planı

**Tarih:** 21 Ekim 2026
**Amaç:** Bir pazaryerinde satılan ürünün stoğu diğer pazaryerlerinde de düşsün; fazla satış olmasın.

Güven etiketleri: **[Kesin]** kodda veya canlı ölçümde doğrulandı · **[Muhtemel]** güçlü çıkarım · **[Tahmin]** doğrulanmadı.

---

## 1. Bugünkü durum (21 Ekim canlı ölçüm)

| Konu | Durum |
|---|---|
| Stok gönderimi | **Hiçbir pazaryerinde açık değil.** Bütün bağlantılarda dış yazma açık, ama `STOCK_WRITE` ve `PRICE_WRITE` hepsinde kapalı [Kesin]. |
| Sonuç | Bir platformdaki satış diğerlerinin stoğunu değiştirmiyor. Her pazaryerinin stoğu kendi panelinden elle yönetiliyor [Kesin]. |
| Panel stoğu | Ürün ilk aktarıldığında pazaryerindeki stokla doluyor, sonra yalnız siparişlerle azalıyor. Pazaryeri panelinden yapılan stok girişleri panele gelmiyor (stok yetkisi `CENTRAL`, ürün aktarımı elle) [Kesin]. |
| Belirti | 6 Ekim'den beri 44 sipariş satırında panel stoğu satışı karşılayamadı (`STOCK_ORDER_CONSUMPTION_BLOCKED`), 41'i Trendyol [Kesin]. Panel stoğu gerçeğin gerisinde kalıyor [Muhtemel]. |
| Risk | Gönderim bugün olduğu gibi açılırsa panelin eksik stokları pazaryerlerine yazılır; ürünler gereksiz yere satışa kapanır [Muhtemel]. |

Kodda nasıl çalışıyor:
- Sipariş gelince satılan adet panel stoğundan düşer; iptal edilen adet geri döner; iade ancak panelde "Stoğa ekle" ile döner (`MarketplaceJobProcessor.ProjectOrderReservations`).
- Stok değişince, varyantı satan her bağlantı için fiyat+stok gönderimi aynı işlemde kuyruğa girer (`StockProjectionOutbox`). Gönderim yalnız o bağlantıda dış yazma, `STOCK_WRITE` ve `PRICE_WRITE` üçü de açıksa yapılır.
- Gönderilen değer: panelde satılabilir stok − teklifin güvenlik stoğu (`ProductPublicationComposer.cs:275`).
- Stok ve fiyat birlikte gider; **fiyat yazma açılmadan stok gönderilemez**, açıldığında panel fiyatı da pazaryerine yazılır.
- Yedek uzlaştırma (15 dk / 1 sa / günlük) yalnız Trendyol ve N11'de var.

**Pazaryeri kodları (22 Ekim 2026):** Satıcı her pazaryerinde panelle aynı barkodu ya da stok kodunu kullanmayabilir.
- Ürün aktarımı, eşleştirme hangi kodla yapılmış olursa olsun, her bağa pazaryerinin kendi barkodunu ve stok kodunu
  yazar (`marketplace_variant_links.ExternalBarcode` / `ExternalSku`) [Kesin].
- Fiyat/stok o pazaryerinin anahtarıyla gider:
  - Trendyol ve Shopify: pazaryeri barkodu.
  - Hepsiburada: pazaryerinin merchantSku'su + hbSku.
  - N11: n11 stok kodu.
  - Pazarama ve PTT AVM: kendi barkodları.

  Panelden yayınlanmamış, içe aktarılmış Trendyol/Hepsiburada/Shopify ürünleri de artık gönderilir. Önceden bunlar
  `LIVE_OFFER_REQUIRED` ile sessizce atlanıyordu [Kesin].
- Sipariş satırı önce bu bağlantının bağlarındaki pazaryeri kodlarıyla, bulunamazsa panelin barkod/stok koduyla
  panel varyantına bağlanır. Kodları panelden farklı ürünlerin satışları da stoğu düşürür [Kesin].
- Kodlar ancak bir ürün aktarımından sonra dolar. Bu sürüm canlıya çıktıktan sonra her bağlantıda bir kez ürün aktarımı
  çalıştırılmalıdır; "yalnız eşleştirme" modu yeterlidir. Aktarılmamış bağlarda Trendyol/Hepsiburada/Shopify gönderimi
  yapılmaz.

---

## 2. Karar gerektiren noktalar

1. **Stoğun asıl kaynağı hangisi?** Eşitlemeden sonra tek kaynak panel olur. O andan itibaren stok **yalnız panelden** girilmeli; pazaryeri panellerinden stok değiştirilirse panel bir sonraki gönderimde onu ezer. Bugünkü hacme göre başlangıç değeri için önerilen kaynak **Trendyol**.
2. **Güvenlik stoğu:** Siparişler 3 dk'ya kadar gecikmeyle geldiği için aynı anda iki kanalda son ürün satılabilir. Çok kanallı ürünlerde teklif başına **1 adet** güvenlik stoğu önerilir; tek kanallı ürünlerde 0.
3. **Fiyat:** Fiyat yazma açılınca panel fiyatları pazaryerlerine gider. Panelde fiyatların güncel olduğundan emin olunmalı (aşama 2'deki aktarım fiyatları da günceller).
4. **Hangi kanallar:** Önce Trendyol, sonra N11 ve Hepsiburada; Pazarama en son (platform stok güncellemesini 4 saate kadar geciktirebiliyor). Shopify ayrıca değerlendirilir.

---

## 3. Aşamalar

### Aşama 0 — Ölçüm (bugün, salt-okunur)
`deploy/sql/olcum.sql` bölüm 9–11:
- Aktif tekliflerde panel stoğu ile pazaryerinde son görülen stok farkı, panelde satılabilir stoğu olmayan teklif sayısı, ayrılmış stok.
- Stok ve fiyat gönderim ayarları.
- Stoğu karşılanamayan sipariş satırları.

**Çıkış koşulu:** Farkın büyüklüğü ve hangi ürünlerde olduğu biliniyor.

### Aşama 1 — Kararlar
Bölüm 2'deki dört soru yanıtlanır. Pazaryeri panellerinden stok girişinin bırakılacağı tarih ekiple paylaşılır.

### Aşama 2 — Panel stoğunu kaynakla eşitle
1. Ürünler → **Platformdan Ürün Aktar** → kaynak platform (Trendyol) → yalnız mevcut ürünler → **Fiyat ve Stokları Güncelle: Açık**.
   Bu seçenek panel stoğunu ve fiyatını pazaryerindeki değerle değiştirir (`forceStockUpdate`).
2. Kaynak platformda olmayan ürünlerin stoğu panelden elle girilir (Toplu Stok Düzenle).
3. Çok kanallı tekliflerde güvenlik stoğu ayarlanır.

**Not:** Stoğu karşılanamamış eski sipariş satırları "ayrılmış" stokta bekliyor olabilir. Bu siparişler kargolandıkça ayrılan adet kendiliğinden düşer; kargolanmamış olanlar varsa satılabilir stoğu geçici olarak eksik gösterir. Bölüm 9'daki `ayrilmis_var` sütunu bunu gösterir.

**Çıkış koşulu:** Bölüm 9'da `stok_farkli` 0'a yakın; yeni `STOCK_ORDER_CONSUMPTION_BLOCKED` oluşmuyor (1–2 gün izleme).

### Aşama 3 — İlk kanal: Trendyol
0. Bu sürümden sonra Trendyol ürün aktarımı en az bir kez çalışmış olmalı (bağlara pazaryeri kodları yazılır).
1. Trendyol bağlantısında `PRICE_WRITE` ve `STOCK_WRITE` açılır (Entegrasyonlar → bağlantı → akış ayarları).
2. İlk gün: İşlem Takibi'nde `PRICE_INVENTORY_SYNC` işleri ve hata kodları izlenir; birkaç ürünün Trendyol stoğu panelle karşılaştırılır.
3. Trendyol kaynak olduğu için gönderilen değerler zaten aynı olmalı; bu aşama gönderim hattının doğruluğunu düşük riskle sınar.

**Çıkış koşulu:** 2 gün boyunca gönderim hatası yok, stoklar tutuyor.

### Aşama 4 — Diğer kanallar (birer birer)
Sıra: N11 → Hepsiburada → Pazarama. Her biri için:
1. Ürünlerin panel varyantlarıyla eşleştiği kontrol edilir (eşleşmeyen ürüne gönderim gitmez: `LIVE_OFFER_REQUIRED`).
2. `PRICE_WRITE` ve `STOCK_WRITE` açılır; bir gün izlenir; sonra sıradaki kanal.
- Hepsiburada ayrıca fiyat/stok yazma yeteneği testinin geçmiş olmasını ister.
- Pazarama'da stok güncellemesi platform tarafında 4 saate kadar gecikebilir; bu kanalda güvenlik stoğu önerilir.

### Aşama 5 — Shopify (ayrı karar)
Shopify satışları 21 Ekim'den beri panel stoğunu düşürüyor. Shopify'a stok yazmak için yazma yeteneği doğrulaması gerekiyor; 4.10 raporundaki şema uyarısı nedeniyle önce bir ürünle denenmeli.

---

## 4. Geri alma
- Herhangi bir kanalda sorun görülürse o bağlantının `STOCK_WRITE` (veya `PRICE_WRITE`) ayarı kapatılır; yeni gönderim kuyruğa girmez [Kesin].
- Kuyrukta bekleyen işlerin de durması kesin istenirse bağlantının **dış yazma** anahtarı kapatılır: zamanlayıcı o bağlantının bekleyen fiyat-stok işlerini iptal eder ve yazma akışlarını kapatır (`ScheduledJobProducer.DisableExternalWriteAutomationAsync`) [Kesin].
- Pazaryerine yazılmış değerler kendiliğinden geri dönmez; gerekirse aşama 2'deki aktarım tersine (pazaryerinden panele) tekrarlanır ya da pazaryeri panelinden düzeltilir.

---

## 5. İzleme
- `deploy/sql/olcum.sql` bölüm 6 (başarısız işler), 9 (stok farkı), 10 (ayarlar), 11 (karşılanamayan satırlar) her aşamadan sonra çalıştırılır.
- İşlem Takibi'nde `PRICE_INVENTORY_SYNC` ve `STOCK_PROJECTION_DISPATCH` işleri.
- Yeni hata kodları: `LIVE_OFFER_REQUIRED`, `STOCK_WRITE_HELD_PRICE_POLICY_DISABLED`, `PRICE_INVENTORY_OUTCOME_UNKNOWN`.
