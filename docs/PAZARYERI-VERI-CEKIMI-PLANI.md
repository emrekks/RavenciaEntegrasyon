# Pazaryeri veri çekimi ve anlık işlemler — iyileştirme planı (rev. 3)

**Revizyonlar:**
- **Rev. 2:** canlı ölçüm, asıl darboğazın Trendyol sipariş kotasını tüketen arka plan okumaları olduğunu gösterdi. Sıra
  "Faz 0 Trendyol okuma bütçesi → Trendyol webhook → anlık işlemler → gerisi" olarak değişti.
- **Rev. 3 (2026-10-18):**
  - Beş platformun resmi API dokümanları okundu.
  - Her platform ve veri türü için ilk çekim süresi, kontrol aralıkları, sayfa boyutları ve kota bütçesi belirlendi
    (bölüm 3–4).
  - En sona kısa özet eklendi (bölüm 12).
- **Rev. 4 (2026-10-18):**
  - İlk çekim kuralı değişti: her veri türü kendi sınırı kadar, **en çok 3 ay** (Trendyol'un azamisi). Sınırı belgelenmemiş
    veya 3 aydan uzun olan türler 3 ay okunur.
  - Pazarama resmi dokümanı (isortagim.pazarama.com/auth/integration) tarayıcıda okundu: sipariş ve iade sorgularında saat
    ve dakika verilebiliyor, sipariş servisi statü filtresi ve statü tarihçesi veriyor, dokümanda webhook bölümü yok.

## 1. Context
Hedef: platformları yormadan panel her zaman güncel kalsın; ilk bağlantıda platformun izin verdiği geçmiş çekilsin; işleme
alma, fatura kesme, iade kararı gibi kullanıcı işlemleri kuyruk beklemeden anında sonuç versin.

### 1.1 Bugünkü durum (canlı, salt-okunur, 2026-10-18)
- **Gecikme sorunu yalnız Trendyol'da.** HB / N11 / Pazarama / Shopify sipariş okumaları ~2.000'er iş/48 saat, bekleme 0 sn,
  çalışma 0–1 sn.
- `TRENDYOL_ORDER_SYNC`:
  - 603 iş / 48 saat.
  - Bekleme ort. 45 / p95 390 sn; çalışma ort. 123 sn.
  - Canlıda kontrol aralığı 180 sn (kod 60 sn'yi 180'e sabitliyor).
- 30 sn'den uzun bekleyen 225 Trendyol sipariş okumasının o anında aynı bağlantıda çalışan işler:

  | İş | Etkilenen okuma |
  |---|---|
  | Fatura uzlaştırma | 99 |
  | Sipariş uzlaştırma | 65 |
  | Açık sipariş durum taraması | 65 |
  | Geçmiş kurtarma | 31 |

- Trendyol fatura uzlaştırması: 93 denemenin 88'i 600–800 sn sürüyor → her 15 dakikanın ~10'unda çalışıyor.
- Trendyol paketleri (iptal hariç):
  - faturasız 67 (59'u ≤14 gün, 27'si "Yeni"),
  - durumu bilinmeyen 38 (hepsi >90 gün),
  - faturalı ~1.650.
- Trendyol siparişlerinin %88'i (1.598 / 1.813) 14 günden eski.
- Hacim (son 30 gün, günlük):

  | Platform | Sipariş/gün | Soru |
  |---|---|---|
  | Trendyol | 15,7 | 43 / ay |
  | Hepsiburada | 1,1 | 0–2 / ay |
  | N11 | 0,1 | 0–2 / ay |
  | Pazarama | ~0 | 0–2 / ay |
  | Shopify | ~0 | — |

  Soru kontrolü platform başına 48 saatte ~1.700 kez çalışıyor.
- Fatura gönderimi / teslimi (öncelik -1): son 14 günde en çok 3 sn bekleme.
- Açık `ExternalEffectRecord`: 3 adet `TRENDYOL_RETURN_ACTION`, 2026-09-15'ten beri.
- `webhook_subscriptions` = 0.
- Sunucu her gece ~00:30–09:00 kapalı.

### 1.2 Kök nedenler (kodda doğrulandı)
1. **Tek kota, çok tüketici.**
   - Satıcının tüm `/v2/orders` ve `/orders/stream` istekleri aynı 30/dk kovasını paylaşıyor.
   - Stream istekleri ayrıca aynı 5 sn aralık kovasını paylaşıyor (TrendyolResilienceHandler.cs:147–152).
   - Arka plan okumaları yeni sipariş okumasıyla aynı kovadan yiyor.
2. **Hedefli okumalar vitrini bilmiyor.**
   - Tekil sipariş okuması (TrendyolHttpClient.cs:287) ıskada 14 vitrin × 2 sorgu = 28 istek atıyor.
   - Paket okuması (:341) 14 istek atıyor; ardından vitrin kodu vermeden (yalnız TR) 12 günlük stream taraması yapıyor
     (:402–463).
3. **Kod V2'yi son 14 günle sınırlıyor** (TrendyolHttpClient.cs:281, :336). API 1 ay veriyor.
   - 14 günden eski siparişler hep ıskalanıyor.
   - Günlük sipariş uzlaştırması 90 günden `UpdatedAt`'e göre en eski 50 siparişi seçiyor; hepsi ıskalanıyor, NotFound sessizce
     geçiliyor ve ertesi gün aynı 50 sipariş yeniden seçiliyor (MarketplaceJobProcessor.cs:3095–3112).
4. **Fatura uzlaştırması gereğinden geniş.**
   - Faturasız her paketi 15 dk'da bir kontrol ediyor; "Yeni" paketler de dahil (MarketplaceContracts.cs:462).
   - V2 paketi bulsa bile fatura alanı boşsa stream taramasına düşüyor (TrendyolHttpClient.cs:378–463).
   - Seçimde yaş sınırı yok (MarketplaceJobProcessor.cs:3352); bütçe 600 sn.
5. **Açık sipariş taraması verimsiz.** Trendyol'da 3 dakikada yalnız 1 sipariş okunuyor (batch 1) ve sipariş başına ≤28 istek
   harcanıyor.
6. **Kilit grubu.** Yeni sipariş okuması, durum taraması, uzlaştırma ve kurtarma aynı "orders" kilit grubunda. Biri çalışırken
   yeni sipariş okuması kiralanmıyor (JobLeaseService.cs:36).
7. **Sipariş akışı her turda 14 vitrini 5 sn arayla geziyor** → turda ~70 sn.
   - Resmi vitrin listesi TR + 9 ülke.
   - QA, OM, BH, AZ resmi listede yok.
8. **Anlık işleme alma:**
   - Yalnız Trendyol/N11'de çalışıyor; Pazarama GUID'leri `long.TryParse`'ta düşüyor (MarketplaceSalesService.cs:1239).
   - Her tıklama yeni bir idempotency anahtarı üretiyor (pages.tsx:1641).
   - Belirsiz effect'ler sonsuza dek açık kalıyor.
   - İstek kota beklerken 10 dk'ya kadar asılı kalabiliyor.
9. **Kullanıcının başlattığı tam senkron** zamanlanmış bir okumayla çakışırsa "başarılı" sayılıp düşürülüyor
   (MarketplaceSyncExecutionLock.cs:123).
10. **Sürekli tam yeniden okumalar:**
    - N11 ve Pazarama iadeleri her 3 dk'da son 45 gün.
    - Shopify kurtarma her 45 dk'da son 3 ay.
    - Sorular her 60 sn.
11. **Hepsiburada:**
    - Sayfa boyutu her uçta 10'a sabit; resmi azami 50–100.
    - İptal ve bozulan paket listeleri hiç okunmuyor.
    - Hız sınırlayıcı yok.
    - Webhook alıcısı yalnız iade yükünü tanıyor.
12. **`TriggerType` ve politika ezilmesi.**
    - Yeni işte `ResourceType` "jobs" ise `TriggerType` iş türünden yeniden türetiliyor (AppDbContext.cs:163–171).
    - Kullanıcının seçtiği aralık eski bir varsayılana eşitse `IsKnownDefault` onu her 5 sn'de yeni varsayılana çeviriyor
      (ScheduledJobProducer.cs:337–368).

## 2. Kullanıcı kararları
- **Sıra (2026-10-18):** Faz 0 Trendyol okuma bütçesi → Trendyol webhook → anlık işlemler → gerisi.
- **İlk çekim (rev. 4):** her veri türü platformun izin verdiği kadar, **en çok 3 ay** (Trendyol sipariş akışının azamisi).
  Sınırı belgelenmemiş veya 3 aydan uzun olan türler 3 ay okunur. Platform 3 aydan kısa izin veriyorsa (HB siparişleri 1 ay,
  Shopify `read_orders` 60 gün) o sınır geçerli. **Hepsiburada istisnası:** iade ve sorular da 1 ay (siparişle aynı). Önceki
  "türlerin en kısası ortak pencere" kuralı kaldırıldı.
- **Webhook:** alıcı ve kayıt kodu yazılır; her platformda dış kaydı kullanıcı onaylar. Yoklama yedek olarak kalır ve
  seyrekleşir.
- **Hepsiburada webhook:** SIT merchant id ve şifresini kullanıcı HB'den talep eder. Base URL, Basic Auth ve HB'ye gidecek
  metni biz hazırlarız; onaya kadar yoklama sürer.
- **Anlık işlemler:** istek içinde doğrudan çalışır; zaman aşımı veya belirsiz sonuçta otomatik olarak kuyruğa düşer.
- **Trendyol vitrinleri:** vitrinler otomatik tespit edilir; aktif olanlar sık, diğerleri seyrek okunur.
- **İkinci worker:** şimdilik yok.

---

## 3. Platform API sınırları (resmi dokümandan, 2026-10-18)
Güven sütunu:
- **R**: resmi doküman.
- **N**: repodaki plan notu (resmi dokümandan yazılmış).
- **B**: belgelenmemiş. Bu durumda canlıda bugün sorunsuz çalışan değer kullanılır.

### Trendyol (developers.trendyol.com)
| Konu | Sınır | Güven |
|---|---|---|
| Sipariş akışı (stream) | 3 ay geçmiş; istek başına ≤14 gün; yalnız son değişiklik tarihine göre filtre; sayfa ≤200, `nextCursor` | R |
| Sipariş paketleri V2 | 1 ay geçmiş (5 Mart 2026'dan beri); istek başına ≤14 gün; `orderNumber`, istek başına ≤50 `shipmentPackageIds`; sayfa ≤200; sorgu başına ≤10.000 kayıt | R |
| Sipariş okuma kotası | Dakikada 30 / 40 / 50 / 100 (ürün listeleme limiti kademesine göre); aşımda 429 | R |
| Genel kota | Aynı uç noktaya 10 sn'de 50 istek; Retry-After belgelenmemiş | R |
| Stream aralığı | İstekler arası 5 sn **öneriliyor** (zorunlu değil) | R |
| İadeler (getClaims) | Tarih filtresi son değişikliğe göre; sayfa ≤200; 1000/dk; geçmiş derinliği belgesiz (kod 3 ay okuyor, çalışıyor) | R / B |
| Sorular | İstek başına ≤14 gün (aşılırsa sessizce kırpılır); sayfa ≤50; okuma 1000/dk, yanıt 500/dk; geçmiş belgesiz (kod 1 yıl) | R / B |
| Kargo firması değişimi | Aynı paket için 5 dk'da en çok 1 | R |
| Tedarik edememe | Kısmi iptalde paket ID'si ve kargo barkodu değişir | R |
| Fatura linki | 409: pakete zaten fatura var veya link başka pakette kullanılmış. `invoiceLink` yalnız paket Invoiced olunca dolu | R |
| Vitrinler | TR (varsayılan) + DE, SA, AE, GR, SK, RO, CZ, BG, KW | R |
| Webhook | 13 sipariş statüsü (boş liste = hepsi); Basic Auth veya API key; tam paket yükü (vitrin kodu yok, `orderCountryCode` var); başarılı olana kadar 5 dk'da bir yeniden dener, süre dolunca **PASİF'e alır**; satıcı başına ≤15 webhook; URL'de "Trendyol", "Dolap", "Localhost" geçemez; Trendyol periyodik çekmeyi de öneriyor | R |

### Hepsiburada (developers.hepsiburada.com)
| Konu | Sınır | Güven |
|---|---|---|
| Kota (sipariş servisleri) | Saniyede 1.000 istek; 429 + `X-RateLimit-Reset` | R |
| Ödemesi tamamlanmış siparişler | İstek başına ≤24 saat (aşılırsa kırpılır); tarihsiz çağrı yalnız son 1 gün; sayfa ≤100 | R |
| Paketler (gönderime hazır, bozulan) | İstek başına ≤24 saat; sayfa ≤10 | R |
| Kargoda / teslim edilen / edilemeyen / iptal | Son 1 ay; sayfa ≤50; teslim edilende `hasInvoice` var | R |
| Faturası eksik paketler | `missing-invoice` listesi; tarih filtresi yok; sayfa ≤50 | R |
| Talepler (iadeler) | Talep tarihi ve durum tarihi filtresi; sayfa ≤100; geçmiş belgesiz (kod 30 gün) | R / B |
| Sorular (Satıcıya Sor) | Oluşturma ve değişiklik tarihi filtresi; sayfa varsayılan 25 (üst sınır belgesiz) | R / B |
| İptal (satıcı) | Günde en çok 100 | R |
| Webhook | Sipariş: 8 uç (`/orders`, `/packages`, iptal, unpack, intransit, deliver, undeliver, adres). Talep: 5 uç. Yalnız Basic Auth. Yanıt **5 sn altında** olmalı; 1 saatte 20'den fazla yavaş yanıt → akış kara listeye alınır. Yeniden deneme politikası belgesiz. Önce SIT, sonra canlı | R |

### N11 (developer.n11.com)
| Konu | Sınır | Güven |
|---|---|---|
| Siparişler | **2024-11-01'den beri** (~23 ay; "3 ay" bir API sınırı değil); istek başına ≤15 gün (aşılırsa **sessizce kırpılır**); son değişikliğe göre filtre; sayfa ≤100; 1000/dk | R |
| İadeler (SOAP) | Yalnız talep tarihi (gün hassasiyeti) + durum filtresi; değişiklik filtresi yok; sayfa 20 sabit; geçmiş belgesiz (kod 90 gün) | R / B |
| Sorular (SOAP) | Liste dakikada 1 | R |
| Ürün okuma | Sayfa ≤250; değişiklik filtresi yok | R |
| Webhook | Yok | R |

### Pazarama (isortagim.pazarama.com/auth/integration — tarayıcıda okundu)
| Konu | Sınır | Güven |
|---|---|---|
| Siparişler (`getOrdersForApi`) | Sipariş tarihi filtresi; **saat ve dakika verilebilir** (yalnız gün değil); istek başına ≤1 ay; bitiş tarihi hariç; ayrıca **sipariş kalemi statüsüne göre** filtre; sipariş no ile 6 aya kadar; değişiklik tarihi filtresi yok. Örnek istekte sayfa 500 | R |
| Statü tarihçesi | 21.04.2026'dan beri yanıtta `orderItemStatusHistory` (statü değişim tarihleri) var | R |
| İadeler (`getRefund`) | Talep tarihi (**saat ve dakika verilebilir**) + iade statüsü filtresi; geçmiş ve aralık sınırı yazmıyor | R / B |
| Sorular | Filtre servisinde tarih veya sınır yazmıyor | B |
| Okuma kotaları | Servis limitleri sayfasında okuma kotası yok (kodun 60/dk ve 30/dk değerleri temkinli varsayım) | B |
| Fiyat / stok | Satıcı başına istekler arası 10 sn; istek başına ≤3.000 ürün; sonuç 4 saate kadar gecikebilir | R |
| Ürün ekleme | İstekler arası 10 sn; istek başına ≤500 ürün | R |
| Webhook | Doküman menüsünde webhook bölümü yok → yoklama | R |

### Shopify (shopify.dev)
| Konu | Sınır | Güven |
|---|---|---|
| Sipariş geçmişi | `read_orders` ile son 60 gün; `read_all_orders` (Shopify onayı) ile tümü | R |
| Artımlı okuma | `updated_at` sorgusu + `UPDATED_AT` sıralaması; sayfa ≤250; sayfalama ≤25.000 kayıt | R |
| Kota | GraphQL: Standard 100 puan/sn (Advanced 200, Plus 1000); tek sorgu ≤1.000 puan; aşımda `THROTTLED` | R |
| Webhook | Yük REST biçiminde; yanıt ≤5 sn; 4 saatte 8 deneme; **8 ardışık hatada abonelik silinir**; sıralama garantisi yok; tekilleştirme `X-Shopify-Webhook-Id` ile; FULFILLMENTS_* konuları `read_fulfillments` ister | R |

---

## 4. Platform parametreleri — bugün → olacak

**İlkeler:**
- **Yeni sipariş:** webhook yokken ≤1 dk kontrol. Webhook sağlıklıyken 15 dk güvenlik taraması.
- **Artımlı okuma:** son değişiklik filtresi olan her yerde watermark − 10 dk örtüşmeyle yapılır. Sabit geniş pencere yalnız
  uzlaştırma turlarında okunur.
- **Seyreltilen kontroller:** filtresi olmayan veriler (N11/Pazarama iadeleri) sık ama dar okunur, seyrek ama geniş okunur.
- **Sorular** hacim çok düşük olduğu için uyarlanır aralıkla okunur:
  - taban 2 dk,
  - boş dönüşte ×2, en çok 10 dk,
  - yeni soru gelince tabana döner.
- **Gece:** sunucu kapalı olduğu için ayrı gece kadansı yok. Açılışta tüm artımlı okumalar hemen çalışır ve webhook
  abonelikleri kontrol edilir.
- **Kod varsayılanları:** yeni değerler kod varsayılanı olur; `IsKnownDefault` listesi güncellenir. Kullanıcının bilinçli
  seçtiği değer ezilmez.

### 4.1 Trendyol
| Veri türü | Bugün | Olacak (webhook yok) | Webhook sağlıklıyken |
|---|---|---|---|
| Yeni/değişen sipariş (stream, son değişiklik) | 180 sn; her turda 14 vitrin; tur ~70–120 sn | **60 sn**; TR + son 90 günde siparişi olan vitrinler; watermark − 10 dk; tur ≤15 sn | 15 dk |
| Pasif vitrin yoklaması | Her turda 13 vitrin | Resmi 9 vitrin 6 saatte bir, turda en çok 1; QA/OM/BH/AZ 24 saatte bir (400/404 → 30 gün atlanır) | Aynı |
| Geçmiş kurtarma (ikinci artımlı okuma) | 15 dk; 14 vitrin | 15 dk; aktif vitrinler | 60 dk |
| Açık paket durumu | 3 dk; turda 1 sipariş; sipariş başına ≤28 istek | **30 dk**; son 30 gündeki tüm açık paketler V2 ile 50 ID'lik gruplar halinde. 30 günden eski açık paketler günde 1 kez stream penceresiyle (bilinen durum tarihi ±6 gün, kendi vitrini) | 60 dk |
| Sipariş uzlaştırma kısa / orta / günlük | 15 dk / 1 sa / 24 sa; 1 / 3 / 90 gün; sipariş başına tekil V2 (90 gün turu hep ıskalıyor) | 1 sa / 6 sa / 24 sa; son 1 / 3 / 90 gün **stream pencereleriyle** (90 gün = 14 günlük 7 pencere), aktif vitrinler | Aynı |
| Fatura uzlaştırma | 15 dk; 50 sipariş; ≤600 sn; faturasız her paket 15 dk'da bir | Yerel faturası teyitsiz paket: 15 dk. Kargodaki/teslim edilmiş faturasız paket (≤30 gün): 6 saat, V2 50'li gruplar. 30–90 gün: **6 saat**, stream pencereleriyle toplu (bugün 6 paket). Yeni / işleme alınmış / ödeme bekleyen ve 90 günden eski: kapsam dışı. Koşu ≤30 sn. 0.9 kontrolü olumlu çıkarsa panel dışı faturalar sipariş akışıyla ≤2 dk'da gelir; bu tur yedek kalır | Aynı (panel dışı fatura linki bildirim tetiklemeyebilir) |
| Kargo bilgisi | 5 dk; 1 paket; paket başına ≤14 istek + stream | 30 dk; son 30 gün V2 50'li gruplar; 30–90 gün günde 1 | Aynı |
| İadeler (getClaims, son değişiklik) | 3 dk; watermark − 15 dk; sayfa 50 | 3 dk; watermark − 15 dk; sayfa 200 | Aynı (iade webhook'u yok) |
| İade durum taraması | 3 dk; 25 iade | 30 dk (artımlı okuma durum değişikliğini zaten getiriyor) | Aynı |
| İade uzlaştırma kısa / orta / günlük | 15 dk / 1 sa / 24 sa; 1 / 3 / 90 gün | 1 sa / 6 sa / 24 sa; 1 / 3 / 90 gün | Aynı |
| Sorular | 60 sn sabit; son 14 gün; statü listesinde geçersiz `UNANSWERED`, eksik `WAITING_FOR_APPROVE` | Taban 2 dk, geri çekilme en çok 10 dk; son 14 gün; statüler `WAITING_FOR_ANSWER`, `WAITING_FOR_APPROVE`, `ANSWERED`, `REPORTED`, `REJECTED` | Aynı |
| Stok uzlaştırma | Kapalı (dış yazma kapalı) | Değişmez | — |
| Referans verisi | 24 saat | 24 saat | — |

**Kota bütçesi:**
- Sipariş kotası (30/dk, kademe teyidiyle 40/50/100) ikiye bölünür:
  - **Ön plan (fg) 12/dk:** yeni sipariş, kullanıcı ve anlık işlem okumaları.
  - **Arka plan (bg) 18/dk:** diğer okumalar.
- Stream'de 5 sn aralık aynı imleç zincirinin sayfaları arasında uygulanır; farklı okumalar arasında en az 1 sn.
- Genel 50 / 10 sn kotası uç nokta başına ayrı tutulur.
- **Hedef:** sipariş kotası kullanımı ≤%20 (yaklaşık saatte 100–150 istek).

### 4.2 Hepsiburada
| Veri türü | Bugün | Olacak (webhook yok) | Webhook sağlıklıyken |
|---|---|---|---|
| Yeni sipariş + paket taraması | 60 sn; her uçta sayfa 10 | 60 sn; sipariş sayfası 100, liste uçları 50, paket uçları 10 (resmi azami) | 15 dk |
| Geçmiş kurtarma | 15 dk (artımlı, en eski 1 ay) | 15 dk | 60 dk |
| Açık sipariş durumu | 3 dk; turda 5 sipariş | 3 dk; turda 25 sipariş | 30 dk |
| İptal ve bozulan paket listeleri | Okunmuyor | 15 dk; son 1 gün (iptal) ve son 24 saat (bozulan); günlük tur son 1 ay | 60 dk |
| Paket durum uzlaştırması | 24 saat; batch 10 | Aynı | Aynı |
| Fatura uzlaştırma | 5 dk; 100 sipariş tek tek | **30 dk**; `missing-invoice` listesi (1–2 istek) + teslim edilen listesindeki `hasInvoice` | Aynı |
| İadeler (talepler, durum tarihi) | 3 dk; watermark − 15 dk | 3 dk; watermark − 15 dk | 15 dk (talep bildirimleri açıksa) |
| İade durum taraması | 3 dk; 7 statü × 30 gün | 30 dk | 60 dk |
| Sorular | 60 sn; son 7 gün; sayfa 25 | Taban 2 dk, en çok 10 dk; değişiklik tarihine göre artımlı; sayfa 25 | Aynı |
| Referans verisi | 24 saat | 24 saat | — |

**Kota bütçesi:** bugün sınırlayıcı yok. Paylaşılan kova eklenir: OMS 10 istek/sn (resmi 1.000/sn'nin çok altında),
katalog 180/dk, ürün güncelleme 100/dk.

### 4.3 N11
| Veri türü | Bugün | Olacak |
|---|---|---|
| Yeni/değişen sipariş (son değişiklik) | 60 sn; watermark − 2 dk | 60 sn; watermark − 10 dk; 15 gün kırpmasına karşı pencere bölme testle korunur |
| Geçmiş kurtarma | 15 dk | 15 dk |
| Açık sipariş durumu | Yok (gerekmiyor) | Yok |
| Sipariş uzlaştırma kısa / orta / günlük | 15 dk / 1 sa / 24 sa; 1 / 3 / 90 gün | 1 sa / 6 sa / 24 sa; 1 / 3 / 90 gün |
| İadeler (yalnız talep tarihi) | Her 3 dk son 45 gün | **3 dk son 2 gün** (yeni talepler) + **30 dk son 45 gün** (durum değişiklikleri) |
| Sorular (liste dakikada 1) | 60 sn; istekler arası 75 sn | Taban 2 dk, en çok 10 dk |
| Stok uzlaştırma | 15 dk / 1 sa / 24 sa | Aynı |
| Referans verisi | 24 saat | 24 saat |

**Kota:** değişmez (sipariş 600/dk ≤ resmi 1000/dk, SOAP 60/dk, soru listesi 75 sn). N11'de webhook yok.

### 4.4 Pazarama
| Veri türü | Bugün | Olacak |
|---|---|---|
| Yeni sipariş (sipariş tarihi, saat-dakika) | 60 sn; her seferinde ~3 takvim günü | 60 sn; watermark − 10 dk (saat-dakika hassasiyetiyle) |
| Geçmiş kurtarma | 15 dk | 15 dk |
| Açık sipariş durumu | 3 dk; turda 5 sipariş, sipariş no ile tek tek | 3 dk; **statü filtresiyle toplu** (alındı, hazırlanıyor, kargoda, mağazada, teslimat noktasında); son 3 ay, 1 aylık 3 pencere. Statü tarihçesinden değişiklik zamanı okunur. Toplu okuma beklendiği gibi çalışmazsa sipariş no ile turda 25 |
| İadeler (talep tarihi, saat-dakika) | Her 3 dk son 45 gün | **3 dk watermark − 15 dk** (yeni talepler) + **30 dk son 45 gün** (statü değişiklikleri) |
| Sorular | 60 sn | Taban 2 dk, en çok 10 dk |
| Sipariş işlerinin zaman aşımı | 60 dk (listede yok) | 10 dk (diğer sipariş okumalarıyla aynı) |
| Referans verisi | 24 saat | 24 saat |

**Kota:** değişmez (sipariş 60/dk, diğer 30/dk, fiyat-stok 12,5 sn). Pazarama'da webhook yok; yoklama sürer.

Doğrulanacak: saat-dakikalı sorguda bitiş anının hariç tutulup tutulmadığı; statü filtresinin tek değer mi liste mi aldığı.

### 4.5 Shopify
| Veri türü | Bugün | Olacak (webhook yok) | Webhook sağlıklıyken |
|---|---|---|---|
| Yeni/değişen sipariş (`updated_at`) | 540 sn (9 dk) | **180 sn**; watermark − 10 dk; sayfa 100 | 15 dk |
| Geçmiş kurtarma | 45 dk; her seferinde son 3 ay | 60 dk artımlı (`updated_at`); son 3 ay tam okuma günde 1 | Aynı |
| Açık sipariş durumu | 9 dk; turda 5 sipariş | 30 dk; turda 25 sipariş | 60 dk |
| Sipariş uzlaştırma kısa / orta / günlük | 45 dk / 3 sa / 3 gün | Kısa ve orta kaldırılır; günlük tam okuma (geçmiş kurtarma) yeterli | Aynı |
| Fatura uzlaştırma | 15 dk; 20 sipariş | Aynı | Aynı |
| İadeler | Siparişle gelir; manuel tam okuma | Aynı | Aynı |

**Kota:** GraphQL `throttleStatus` okunur. İstenen sorgu maliyeti (`requestedQueryCost`) loglanır. 1.000 puan tavanını
aşan sorgu varsa iç içe sayfa boyutları düşürülür.

---

## 5. İlk aktarım (yeni bağlantı)

### 5.1 Pencereler
Kural (rev. 4): her tür platformun izin verdiği kadar, **en çok 3 ay**. Sınırı belgelenmemiş türler 3 ay okunur.
`PlatformHistoryLimits.MaxInitialWindow = 3 ay` tek sabittir.

| Platform | Sipariş | İade | Soru | Bugün kodda |
|---|---|---|---|---|
| Trendyol | **3 ay** (stream azamisi) | **3 ay** (belgesiz) | **3 ay** (belgesiz) | Sipariş 3 ay, iade 3 ay, soru 1 yıl |
| Hepsiburada | **1 ay** (platform sınırı) | **1 ay** (kullanıcı kararı) | **1 ay** (kullanıcı kararı) | Sipariş 1 ay, iade 30 gün, soru 1 yıl |
| N11 | **3 ay** (platform ~23 ay veriyor) | **3 ay** (belgesiz) | **3 ay** (belgesiz) | Sipariş 3 ay, iade 90 gün (ilk aktarımda değil), soru 1 yıl |
| Pazarama | **3 ay** (sınır yazmıyor) | **3 ay** (sınır yazmıyor) | **3 ay** (sınır yazmıyor) | Sipariş 3 ay, iade 90 gün (ilk aktarımda değil), soru 1 yıl |
| Shopify | **60 gün** (`read_orders`); `read_all_orders` varsa **3 ay** | Siparişle gelir | — | Tarih filtresi yok (erişilebilen tüm geçmiş) |

Belgesiz türlerde platform 3 aylık sorguyu reddederse (hata veya boş yanıt), o tür bugün çalışan değere düşer ve operasyon
kaydı açılır. Bu değerler: N11/Pazarama iadesi 90 gün.

### 5.2 Okuma biçimi
| Platform | Sipariş | İade | Soru | Tahmini süre |
|---|---|---|---|---|
| Trendyol | Stream, 14 günlük 7 pencere × tüm vitrinler (ilk aktarımda hepsi) | 3 ay, sayfa 200 | 14 günlük 7 pencere | ~10 dk |
| Hepsiburada | 24 saatlik 30 pencere + 1 aylık liste uçları | 1 ay, sayfa 100 | 1 ay | ~2 dk |
| N11 | 15 günlük 6 pencere | 3 ay, sayfa 20 | 3 ay (liste dakikada 1) | ~5 dk |
| Pazarama | 1 aylık 3 pencere | 3 ay | 3 ay | ~2 dk |
| Shopify | Tek sorgu, sayfa 250 | Siparişle | — | <1 dk |

### 5.3 Ortak kurallar
- Pencereler **yeniden eskiye** işlenir; son siparişler ilk dakikada görünür.
- Her pencere ayrı, kısa (≤30 sn) bir iştir. Biten pencere `SyncCursor`'a kontrol noktası yazar ve sonrakini **hemen**
  kuyruğa ekler (bugün Trendyol her pencere için 15 dk bekliyor). Deploy veya çökme sonrası kaldığı yerden devam eder.
- Bugünkü hata düzeltilir: Trendyol bağlantısında son 14 günde sipariş yoksa ilk aktarım her koşuda ilk pencereye dönüyor ve
  eski pencerelere geçemiyor.
- Arayüz: entegrasyon kartında "İlk aktarım: Siparişler %60 · İadeler tamam · Sorular tamam".
- İade ve sorular da ilk aktarımın parçası olur (bugün N11/Pazarama iadeleri ilk zamanlanmış koşuya kalıyor).

---

## 6. Fazlar

### Faz 0 — Trendyol okuma bütçesi (en büyük etki, düşük risk)
**Hedef:**
- Yeni sipariş okuması bekleme p95 390 → ≤30 sn; çalışma 123 → ≤15 sn.
- Fatura uzlaştırması 600+ → ≤30 sn.
- 30 sn'den uzun bekleyen sipariş okuması 225 / 48 saat → <10.

İlk üç adım migration gerektirmez.

- **0.1 Fatura uzlaştırması** (bölüm 4.1 satırı):
  - Sınıflara göre seçilir; kalan okumalar toplu yapılır (V2 50'li `shipmentPackageIds`; stream pencereleri).
  - V2 paketi bulduysa ve `invoiceLink` boşsa paket faturasız sayılır. Resmi kural: link yalnız Invoiced'da dolu.
    Stream'e düşülmez.
- **0.2 Hedefli okumalar ve uzlaştırma:**
  - V2 tekil sorgu penceresi 14 → 30 gün (resmi 1 ay).
  - 30 günden eski siparişte V2 sorgusu yapılmaz; stream penceresi (bilinen durum tarihi ±6 gün) siparişin kendi vitriniyle
    okunur.
  - Sipariş uzlaştırması tekil sorgu yerine stream pencereleriyle yapılır; aralıklar bölüm 4.1'deki gibi.
- **0.3 Açık paket taraması:** 30 dk; V2 50'li gruplar; 30 günden eskiler günde 1.
- **0.4 Kota önceliği:** bölüm 4.1'deki fg/bg ayrımı.
  - Sınıf, isteğin `AdapterContext` amacından (iş türü) belirlenir.
  - Paylar `Trendyol:OrderForegroundShare` ile ayarlanır.
  - Kota `Trendyol:OrderRequestsPerInterval` ile kademeye göre ayarlanır.
- **0.5 Vitrin siparişe yazılır** (migration):
  - Yeni kolon `sales.orders."StoreFrontCode"`.
  - Stream okumasında okunan vitrin yazılır; hedefli okuma bulduğu vitrini yazar.
  - Hedefli okumalara vitrin ipucu eklenir; tırnaklı `orderNumber` biçimi yalnız 400'de denenir.
  - Etki: ıskada 28 istek → ≤2.
- **0.6 Aktif vitrinler** (`TrendyolStorefrontPolicy`): bölüm 4.1'deki kural. Son deneme zamanları sipariş `SyncCursor`
  durumunda tutulur.
- **0.7 Dilimleme:** fatura uzlaştırması, sipariş uzlaştırması ve açık paket taraması dilim başına ≤30 sn çalışır, kontrol
  noktası yazar ve +2 sn sonraya yeniden kuyruğa girer.
- **0.8 Stream saat dilimi kontrolü:** doküman tarih parametrelerini "ms, GMT+3" diye tanımlıyor; kod UTC epoch gönderiyor.
  Dönen paketlerin `lastModifiedDate` değerleri pencere sınırlarıyla loglanıp karşılaştırılır. Kayma varsa düzeltilir.
- **0.9 Panel dışı fatura kontrolü:** Trendyol'a panel dışından fatura linki yüklendiğinde paketin `lastModifiedDate`
  değerinin güncellenip güncellenmediği ölçülür.
  - Yöntem: fatura uzlaştırmasının yeni faturalı bulduğu her paket için, o paketin daha önce sipariş akışında aynı fatura
    linkiyle görülüp görülmediği loglanır.
  - Sonuç olumluysa panel dışı faturalar ≤2 dk'da yansır; 6 saatlik tur yedek kalır.
  - Olumsuzsa 6 saatlik tur asıl yol olarak kalır.
  - 1 hafta veri toplanır, sonuç raporlanır.

**Ölçüm:** 0.2 ve 0.7 sonrası canlı salt-okunur SQL (bölüm 10).

### Faz 1 — Trendyol webhook (kullanıcı onaylı kayıt)
- **1.1 Kayıt** (`IWebhookRegistrar`):
  - Desteklenen işlemler: create / list / update / delete / activate / deactivate.
  - Kimlik doğrulama Basic Auth: bilgiler bağlantı başına üretilir (`ProtectedVerifierSecret`) ve `TrendyolWebhookVerifier`
    ile doğrulanır.
  - Statü listesi boş (13 statünün hepsi), `countryCodes` boş (tüm ülkeler).
  - Bağlantı başına 1 webhook (satıcı sınırı 15).
  - URL "Trendyol", "Dolap", "Localhost" içermez.
  - Mevcut `POST /hooks/{publicId}/{routeToken}` yeterli.
- **1.2 İşleyici:**
  - Tam paket yükü mevcut eşleyiciyle upsert edilir; ek API çağrısı yapılmaz.
  - Eski olay yeniyi ezmez: paket son değişiklik zamanı karşılaştırılır.
  - Inbox ile tekilleştirilir; hızlı 200 döner; iş önceliği 0.
- **1.3 Arayüz:** "Anlık Bildirimler" sekmesi.
  - Gösterilenler: durum, son bildirim, olay sayısı.
  - "Bildirimleri Aç" onay penceresinde URL, olay listesi ve "pasife düşerse otomatik yeniden açılır" maddesi yazar.
  - "Kapat" kaydı siler.
- **1.4 Sağlık:**
  - Sağlıklıyken bölüm 4.1'deki "webhook sağlıklıyken" aralıkları geçerli.
  - Yoklamanın yakaladığı ama bildirimin getirmediği değişiklik "boşluk" sayılır. 2 boşlukta veya 6 saat bildirimsizlikte
    (iş saatinde) abonelik `DEGRADED` olur, normal aralıklara dönülür ve panoda uyarı gösterilir.
- **1.5 Sunucu kapalılığı:** Trendyol başarılı olana kadar 5 dk'da bir dener ve süre dolunca webhook'u pasife alır. Worker
  açılışında sırayla:
  1. Abonelik listelenir.
  2. Pasifse `activate` çağrılır.
  3. Watermark'tan artımlı okuma hemen çalışır.
  4. İlk tur normal aralıkla yapılır.

### Faz 2 — Anlık işlemler (doğrudan + otomatik kuyruk yedeği)
- **2.0 Kullanıcı şeridi ve tetikleyici türü:**
  - Öncelikler: `UserAction = -2`, `InitialSync = -1`, `Hot = 0..2`, `Background ≥ 3`.
  - Yeni şerit `Lane("user", 2, …)`.
  - `TriggerType` (`user` / `schedule` / `webhook` / `system`) ezilmez.
  - Kullanıcı uçları `user` ve `UserAction` verir: fatura gönder/teslim, kargo işlemi, iade kararı, tekil sipariş yenileme,
    "Senkronize Et".
  - `ContentionResult` kullanıcı işini birleştirmez, 30 sn erteler.
- **2.1 `InstantActionRunner`:**
  1. Ön kontroller.
  2. Effect rezervasyonu.
  3. Kota en çok 2 sn beklenir (`SharedRateLimits.WaitAsync`'e üst sınır; okumalar fg kovasından).
  4. Adaptör çağrısı 8 sn son süreyle yapılır.

  Sonuca göre:
  - **Başarı:** 200 + yerel güncelleme + SignalR.
  - **"Zaten istenen durumda":** başarı.
  - **Kesin 4xx:** effect silinir, anlaşılır hata döner.
  - **Zaman aşımı / 429 / 5xx / belirsiz:** effect `PENDING_RECONCILE` olur; `UserAction` önceliğinde yedek iş eklenir
    (+5 sn); 202 `{state:"QUEUED", jobId}` döner. Yedek iş **önce uzak durumu okur**, yalnız işlem uygulanmamışsa yeniden
    gönderir.
- **2.2 Platform kuralları** (resmi):

  | İşlem | Kural |
  |---|---|
  | Trendyol kargo firması değişimi | Aynı pakete 5 dk'da 1 → yedek iş en erken 5 dk sonra; aynı firmaya tekrar gönderilmez |
  | Trendyol kısmi tedarik edememe | Paket ID'si değişir → uzak okuma sipariş numarasıyla yapılır |
  | Trendyol fatura linki 409 | Link aynıysa başarı, farklıysa hata |
  | Trendyol Picking/Invoiced tekrar gönderimi | Belgesiz; ilk canlı denemede kullanıcıyla gözlenir |
  | HB paket oluşturma tekrarı | 404/409 döner → uzak okuma ile doğrulanır |
  | HB paket bölme | Paket numarası değişir |
  | HB iptal | Günde en çok 100 |

- **2.3 Effect uzlaştırıcı:**
  - Yeni iş `EXTERNAL_EFFECT_RECONCILE`, 2 dk'da bir.
  - 2 dk'dan eski açık effect'lerde uzak durum okunur; 30 dk sonra hâlâ belirsizse operasyon kaydı açılır.
  - Bugün açık 3 effect de bununla kapanır.
- **2.4 Kararlı idempotency anahtarı (web):**
  - Biçim `instant-picking:{orderId}:v{packageVersion}`; diğer işlemlerde aynı desen.
  - 202'de satırda "Sırada" gösterilir; iş bitince SignalR ile güncellenir.
- **2.5 İşleme Al kapsamı:**
  - **Pazarama:** GUID satırlı dal anlık yola taşınır.
  - **Hepsiburada:** paket yoksa paket oluşturma.
  - **Shopify:** buton gizlenir.
- **2.6 Fatura kesme:**
  - `INVOICE_SUBMIT` runner ile istek içinde çalışır, 15 sn son süreyle.
  - Ardından uzlaştırma (+10/+30/+60 sn) → belge → pazaryerine teslim zinciri kullanıcı şeridinde ilerler.
- **2.7 Diğer işlemler:** kargo firması değişimi, tedarik edememe, iade onay/ret, takip no gönderme, soru yanıtlama aynı
  kalıpla. Stok/fiyat kuyrukta kalır, kullanıcı tetiklerse `UserAction` alır.

### Faz 3 — Kontrol aralıkları ve okuma biçimleri (diğer platformlar)
Bölüm 4.2–4.5'teki tablolar uygulanır. Ayrıca:
- HB sayfa boyutları resmi azamiye çıkarılır (sipariş 100, listeler 50, paketler 10).
- HB'de iptal ve bozulan paket listeleri okunmaya başlanır.
- HB'de fatura `missing-invoice` listesiyle uzlaştırılır.
- HB için paylaşılan hız kovası eklenir.
- N11/Pazarama iadeleri "sık-dar + seyrek-geniş" okumaya geçer.
- Shopify kurtarma artımlı olur; tam okuma günde 1 kez.
- Sorulara uyarlanır aralık uygulanır; Trendyol soru statü listesi düzeltilir.
- Pazarama sipariş işlerinin zaman aşımı 10 dk olur.
- Açık sipariş tarama batch'i 25 olur (`MarketplaceSync:OrderLifecycle:BatchSize`).
- Yeni varsayılanlar `ScheduledJobProducer.DefaultPolicies` ve `ScheduledOrderPollingCadencePolicy`'ye yazılır. Eski
  varsayılanlar `IsKnownDefault`'a eklenir. Kullanıcının bilinçli seçimi ezilmez.

### Faz 4 — İlk aktarım
- Bölüm 5 uygulanır.
- `PlatformHistoryLimits` (Application/SynchronizationPolicies.cs) tek kaynak olur; dağınık geçmiş sabitlerinin yerini alır:
  `TrendyolOrderHistoryPolicy`, `N11OrderHistoryPolicy`, `PazaramaOrderHistoryPolicy`, `HepsiburadaOrderHistoryPolicy`,
  `HepsiburadaReturnHistoryPolicy`, `ShopifyOrderHistoryPolicy`, `*ReturnInitialLookbackDays`, soru geçmiş başlangıcı.
- Tavan tek sabit: `MaxInitialWindow = 3 ay`; her tür `min(platform sınırı, 3 ay)`.

### Faz 5 — Shopify ve Hepsiburada webhook
- **5.1 Alıcı:**
  - Route `/api/v1/hooks/{publicId}/{routeToken}/{**path}`, POST + PUT (HB alt yolları için).
  - Yanıt her zaman 5 sn altında: yalnız inbox'a yazılır, işleme kuyrukta yapılır.
- **5.2 Shopify:**
  - `webhookSubscriptionCreate` konuları: ORDERS_CREATE, ORDERS_UPDATED, ORDERS_CANCELLED, ORDERS_FULFILLED,
    ORDERS_PARTIALLY_FULFILLED, REFUNDS_CREATE; `read_returns` varsa RETURNS_*.
  - FULFILLMENTS_* kullanılmaz (`read_fulfillments` ister).
  - Yük REST biçiminde olduğu için **tetikleyici olarak** kullanılır: tek sipariş `order(id:)` ile okunur.
  - Tekilleştirme `X-Shopify-Webhook-Id` ile (bugün Event-Id kullanılıyor; düzeltilir).
  - Shopify 8 ardışık hatada aboneliği siler. Worker açılışında abonelikler listelenir, eksikler yeniden oluşturulur
    (kullanıcının onayladığı kapsam içinde).
- **5.3 Hepsiburada SIT → canlı:**
  - **Kullanıcı ön koşulu:** HB'den SIT merchant id ve test kullanıcı/şifre.
  - **Hazırlık:**
    - STAGE bağlantı (SIT adresleri).
    - Bağlantıya özel base URL ve Basic Auth.
    - HB'ye gidecek kopyalanabilir metin: 8 sipariş ucu + 5 talep ucu.
  - **İşleyiciler:**
    - Statü olayları (iptal, unpack, intransit, deliver, undeliver, adres) doğrudan günceller.
    - `orders` / `packages` / talep olaylarında hedefli okuma yapılır.
    - Hepsi idempotent.
  - **Test:** SIT test siparişi ve test talebi (stub servisleri) → panelde görünür → canlı başvurusu (Satıcı Paneli →
    Yardım Merkezi → Talepler → "API ENTEGRASYON") kullanıcı onayıyla.
  - **Kara liste koruması:** 5 sn'yi aşan yanıt sayısı izlenir; saatte 5'i geçerse uyarı verilir (HB sınırı 20).

### Faz 6 — Gözlemlenebilirlik ve hedefler
- Ölçüm Faz 0 ile başlar.
- **Tazelik göstergesi:** Siparişler, İadeler ve Sorular başlığında "Son güncelleme 35 sn önce · Anlık bildirim açık".
- **Kuyruk performansı kartları:**
  - şerit bazında p95 bekleme,
  - anlık işlem gecikmesi ve kuyruğa düşme oranı,
  - webhook gecikmesi,
  - platform başına dakikalık istek ve kota doluluğu (Trendyol fg/bg ayrı).
- **Hedefler (SLO):**
  - Yeni sipariş görünürlüğü: webhook ile ≤30 sn; yoklama ile ≤2 dk (Trendyol, HB, N11, Pazarama), ≤4 dk (Shopify) (p95).
  - Anlık işlem yanıtı ≤5 sn (p95); kuyruğa düşme <%5.
  - Kullanıcı şeridi beklemesi p95 ≤10 sn.
  - Trendyol sipariş kotası doluluğu ≤%20.

---

## 7. Plan dışı bulgular (ayrı iş olarak ele alınacak)
Araştırmada çıkan, veri çekimiyle doğrudan ilgili olmayan hatalar ve riskler:

| Platform | Bulgu |
|---|---|
| N11 | Fatura linkinde `invoiceDateTime` resmi olarak ms timestamp (GMT+3) bekleniyor; kod `dd/MM/yyyy HH:mm` gönderiyor. `invoiceNumber` 16 karakter kuralına uymazsa red riski var |
| N11 | Sipariş, iade ve ürün sorgularında `sender` varsayılanı SELLER; n11depom kullanılıyorsa o siparişler gelmez |
| Hepsiburada | Kimlik doğrulamada resmi sıra kullanıcı adı = merchantId; kod önce entegratör adını deniyor |
| Shopify | HMAC için client secret gerekir; doğrulayıcı önce `ApiKey` alanına bakıyor |
| Shopify | Doğrulayıcıdaki yedek konu adı `orders/update` yanlış (`orders/updated`) |

## 8. Açık sorular (kullanıcıya)
1. **Trendyol kota kademesi:** Trendyol panelindeki ürün listeleme limitiniz nedir? Kota buna göre 30/40/50/100 istek/dk
   olur.
2. **Shopify izni:** mağazada `read_all_orders` izni var mı? Panelde 60 günden eski Shopify siparişi var, bu izin var gibi
   görünüyor.
3. **Webhook onayları:** Trendyol (Faz 1) ve Shopify (Faz 5) kayıtları kod canlıya çıkınca sizin onayınızla açılır.
   Hepsiburada için SIT bilgilerini HB'den talep etmeniz gerekiyor.

## 9. Uygulama sırası
1. **Faz 0:** 0.1 → 0.2 → (ölçüm) → 0.3 → 0.4 → 0.5 → 0.6 → 0.7 → 0.8 → 0.9 → (ölçüm).
2. **Faz 1:** Trendyol webhook; kod canlıya çıkınca kayıt sizin onayınızla.
3. **Faz 2:** 2.0 → runner + uzlaştırıcı → işleme alma kapsamı → fatura → diğerleri.
4. **Faz 3:** HB → N11/Pazarama → Shopify → sorular.
5. **Faz 4.**
6. **Faz 5:** Shopify, sonra HB SIT (SIT bilgisi gelince).
7. **Faz 6:** sürekli.

Her adım ayrı commit: commit → `git push origin HEAD:main` → Validate + Deploy → canlıda salt-okunur kontrol → rapor.

## 10. Doğrulama
- **xUnit testleri:**
  - **Faz 0:**
    - fatura sınıf seçimi,
    - V2 `invoiceLink` boşken stream'e düşmeme,
    - 30 gün kuralı,
    - stream pencereleriyle uzlaştırma (NotFound aynı siparişi yeniden seçmez),
    - fg/bg sınıflandırması ve toplam limit,
    - vitrin ipucuyla tek istek (sahte HTTP işleyicide istek sayısı),
    - `TrendyolStorefrontPolicy`,
    - dilim bütçesi.
  - **Faz 1:** Basic Auth, paket yükü upsert'i, eski olayın yeniyi ezmemesi, `WebhookHealthPolicy`, açılışta pasif
    aboneliğin yeniden açılması.
  - **Faz 2:**
    - runner sonuçları (200 / "zaten" / 202 / 4xx / belirsiz → önce okuma),
    - kargo değişiminde 5 dk kuralı,
    - Pazarama GUID,
    - `TriggerType` korunması,
    - `ContentionResult`,
    - şerit seçimi.
  - **Faz 3:**
    - uyarlanır soru aralığı,
    - N11 15 gün kırpmasına karşı pencere bölme,
    - N11/Pazarama iade sık-dar / seyrek-geniş okuma,
    - HB `missing-invoice` eşlemesi,
    - `IsKnownDefault` kullanıcı seçimini ezmeme.
  - **Faz 4:** `PlatformHistoryLimits` 3 ay tavanı ve platform sınırı (HB sipariş 1 ay, Shopify 60 gün); belgesiz türde
    reddedilince bugünkü değere düşme; yeniden eskiye pencere sırası; boş bağlantıda ilk aktarımın ilerlemesi.
  - **Pazarama:** saat-dakikalı sipariş/iade penceresi; statü filtresiyle açık sipariş okuması.
  - **Faz 5:** webhook alt yolu + PUT; HB olay eşleme; Shopify Webhook-Id tekilleştirmesi.
- **Web testleri:** kararlı idempotency anahtarı, 202 "sırada" durumu. Komutlar: `npm test`, `npm run typecheck`.
- **Backend:** `dotnet test`. Testler ayrı ayrı çalıştırılır, sonuç görülmeden commit edilmez.
- **Canlı salt-okunur SQL** (`BEGIN READ ONLY; … ROLLBACK;`), öncesi/sonrası:
  - iş türü bazında bekleme p50/p95 ve çalışma süresi,
  - 30 sn'den uzun bekleyen Trendyol sipariş okumaları ve o anda çalışan işler,
  - günlük iş sayısı (platform × tür),
  - açık effect'ler,
  - `webhook_subscriptions.LastReceivedAt`,
  - `ORDER_LIFECYCLE_GAP` kayıtları.
- **Arayüz:** yalnız pencere/menü açılıp kapatılır. Gerçek işleme alma, fatura ve webhook kaydı denemesini kullanıcı yapar.

## 11. Kaynaklar
- **Trendyol:** developers.trendyol.com — sipariş paketleri (V2 ve stream), servis limitleri, getClaims, sorular, fatura
  linki, webhook model / create / update / activate, changelog (05.03, 02.06, 29.06, 30.07.2026).
- **Hepsiburada:** developers.hepsiburada.com — sipariş entegrasyonu önemli bilgiler (28.09.2026), Efsane Kasım duyurusu
  (28.09.2026), webhook rehberleri (08.05.2026), test süreci (21.05.2026), talep entegrasyonu (04.06.2026), Satıcıya Sor.
- **N11:** developer.n11.com — sipariş listeleme, sipariş kalemi güncelleme, fatura linki, iade talepleri, ürün soru-cevap,
  fiyat-stok, changelog.
- **Pazarama:** isortagim.pazarama.com/auth/integration — servis limitleri (5.10.2026), siparişler, iadeler (17.02.2026),
  soru filtreleme (22.12.2025), changelog (21.04.2026 statü tarihçesi).
- **Shopify:** shopify.dev — access scopes, orders query, pagination, GraphQL rate limits, webhooks (delivery structure,
  https, best practices, ignore duplicates, verify), versioning.

---

## 12. Kısa özet

### Her platformda ne olacak
| Platform | İlk bağlantıda çekilecek geçmiş | Yeni sipariş kontrolü (bugün → olacak) | Anlık bildirim (webhook) |
|---|---|---|---|
| Trendyol | Sipariş, iade, soru: 3 ay | 3 dk → **1 dk** | Var; sizin onayınızla açılır, sonra kontrol 15 dk'ya iner |
| Hepsiburada | Sipariş, iade, soru: 1 ay | 1 dk → 1 dk | Var; HB test onayından sonra |
| N11 | Sipariş, iade, soru: 3 ay | 1 dk → 1 dk | Platformda yok |
| Pazarama | Sipariş, iade, soru: 3 ay | 1 dk → 1 dk | Platformda yok |
| Shopify | Sipariş 60 gün (izin varsa 3 ay) | 9 dk → **3 dk** | Var; sizin onayınızla açılır |

### Sırayla ne yapılacak
| Adım | Ne | Sizin için sonucu |
|---|---|---|
| 0 | Trendyol okumalarını düzeltmek (gereksiz istekleri kesmek, kotayı yeni siparişe ayırmak) | Yeni Trendyol siparişi 3–10 dk yerine ≤2 dk'da görünür |
| 1 | Trendyol anlık bildirimi | Yeni sipariş ≤30 sn'de görünür |
| 2 | Anlık işlemler | İşleme Al, fatura, iade kararı ≤5 sn; sorun olursa otomatik sıraya girer, çift işlem olmaz |
| 3 | Diğer platformların kontrol aralıkları | Gereksiz okumalar azalır: sorular 1 dk yerine 2–10 dk; N11/Pazarama iadeleri her 3 dk 45 gün yerine 3 dk'da yalnız yeniler + 30 dk'da 45 gün; Pazarama açık siparişleri tek tek değil toplu okunur |
| 4 | İlk aktarım | Yeni bağlantıda geçmiş en yeniden eskiye, dakikalar içinde gelir; ilerleme kartta görünür |
| 5 | Shopify ve Hepsiburada anlık bildirimleri | Bu platformlarda da ≤30 sn |
| 6 | Göstergeler | "Son güncelleme … önce" ve kota doluluğu panelde görünür |

### Sizden beklenenler
1. Trendyol panelindeki ürün listeleme limiti.
2. Shopify'da `read_all_orders` izni var mı?
3. Webhook kayıtlarına onay (kod hazır olunca) ve HB'den SIT test bilgisi.
