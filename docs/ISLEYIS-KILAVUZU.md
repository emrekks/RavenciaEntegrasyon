# Ravencia Marketplace Hub — İşleyiş Kılavuzu

Panelin bugün nasıl çalıştığını anlatan tam başvuru belgesidir: hangi ekran ne gösterir, veri nereden gelir, hangi düğme
pazaryerine ne gönderir, hangi kural nerede durur. Kaynak: `origin/main` = `e47b8be1a` (13 Kasım 2026; sahibin 13 Kasım'da yanıtladığı 14 sorunun kararları ve sonrası dahil). Canlıda api,
worker ve caddy aynı sürümü çalıştırıyor (canlı: `manual-e47b8be1aab8`, 13 Kas 2026 00:04Z).

---

## Bu kılavuz nasıl okunur

- **Ekran adları** panelde yazdığı gibidir (ör. **İşlem Takibi**, **Verileri Sıfırla**). Bir ad kalın yazılmışsa ekranda
  birebir o yazı vardır.
- **Kaynak referansları** `Dosya.uzantı:satır` biçimindedir. Dosyalar `src/` altındadır ve adıyla bulunur
  (ör. `IntegrationPages.tsx:152`). Adı birden çok yerde geçen dosyada klasör de yazılır: `operations/pages.tsx`,
  `invoicing/pages.tsx`. `deploy/…`, `docs/…`, `.github/…` depo köküne göredir. `docs:trendyol:12` =
  `.claude/skills/marketplace-docs/trendyol.md` dosyasının 12. satırı (her pazaryeri bilgisinin resmî kaynağı orada).
- **(canlı: …)** notları canlı veritabanından ya da sunucudan salt okunur okunmuş bilgidir; tarihi yanındadır. 12 Kasım
  2026 akşamı bütün bağlantılar elle **Pasif** yapılmış ve 6 bağlantıda **Tam Sıfırlama** çalışmıştı; bu yüzden sipariş,
  iade, fatura, ürün ve finans tabloları o gün boştu ve birçok kural canlı veriyle değil koddan anlatılır.
- Teknik terimler ilk geçtikleri yerde tek cümleyle açıklanır; hepsi **8. Terimler Sözlüğü**'ndedir.
- Bir kural ile ekrandaki metin çelişiyorsa doğrusu bölümde yazılır, çelişki ya da bekleyen karar **9. Bilinen Tutarsızlıklar**'da listelenir.
- Sahibin verdiği ve sistemin uyduğu kararlar **10. Sahip Kuralları**'ndadır.

## İçindekiler

- [Panel bir bakışta](#panel-bir-bakışta)
- [1. Sayfa Haritası ve Panelin Kabuğu](#1-sayfa-haritası-ve-panelin-kabuğu)
- [2. Pazaryeri Bağlantıları ve Veri Eşitleme](#2-pazaryeri-bağlantıları-ve-veri-eşitleme)
- [3. Katalog ve Ürünler](#3-katalog-ve-ürünler)
- [4. Siparişler, İadeler, Faturalar, Stok ve Ürün Soruları](#4-siparişler-iadeler-faturalar-stok-ve-ürün-soruları)
- [5. Genel Bakış, Raporlar, Hakediş ve Promosyon](#5-genel-bakış-raporlar-hakediş-ve-promosyon)
- [6. Altyapı, Arka Plan İşleri ve İşlem Takibi](#6-altyapı-arka-plan-işleri-ve-işlem-takibi)
- [7. Sistem Ayarları, Kullanıcılar, Bildirimler ve Veri Temizliği](#7-sistem-ayarları-kullanıcılar-bildirimler-ve-veri-temizliği)
- [8. Terimler Sözlüğü](#8-terimler-sözlüğü)
- [9. Bilinen Tutarsızlıklar](#9-bilinen-tutarsızlıklar)
- [10. Sahip Kuralları](#10-sahip-kuralları)

---

## Panel bir bakışta

Panel pazaryerleri ile satıcı arasında duran tek bir kayıt merkezidir. Veri üç yönde akar:

```
 PAZARYERLERİ (Trendyol, Hepsiburada, N11, Pazarama, PttAVM, Shopify)
      │  okuma işleri (zamanlayıcı + webhook + düğmeler)          ▲  yazma işleri (yalnız izin varsa)
      ▼                                                          │
 ┌────────────────────────── İŞ KUYRUĞU (integration.jobs) ───────────────────────────┐
 │  Worker işleri kiralar, kota ve kilit kurallarıyla sırayla çalıştırır               │
 └────────────────────────────────────────────────────────────────────────────────────┘
      │ yazar                                                    ▲ kuyruğa koyar
      ▼                                                          │
 PANEL TABLOLARI  (siparişler, paketler, iadeler, ürünler, stok, finans, faturalar)
      │ okunur                                                   │
      ▼                                                          │
 EKRANLAR  (Genel Bakış, Siparişler, İadeler, Faturalar, Raporlar …) ── düğmeler ──┘

 FATURALAR:  Faturalar ekranı ── sahip onayı ──► E-Faturam (fatura kesimi)
             E-Faturam ── belge/link ──► panel ──► pazaryeri (fatura linki iletimi)
```

1. **Pazaryerinden panele (okuma).** Her bağlantının **akışları** vardır: "Siparişler ve Paket Durumları", "İade
   Talepleri", "Cari Hesap ve Masraflar" gibi. Akış, bir okuma işini ne sıklıkla üreteceğini söyleyen ayardır.
   Zamanlayıcı bu ayarlara göre her birkaç dakikada bir **iş** (arka planda çalışan tek görev) kuyruğa koyar. Trendyol,
   Hepsiburada ve Shopify ayrıca **webhook** (pazaryerinin panele attığı anlık bildirim) gönderir; o zaman yoklama
   seyrekleşir ama doğruluk kaynağı yine okumadır. Okunan kayıtlar panel tablolarına yazılır (bkz. 2 ve 6).
2. **Panel tablolarından ekranlara.** Ekranlar tabloları okur. Bazı sayılar canlı sorgudur (Bekleyen Siparişler,
   performans paneli), bazıları her 30 sn–5 dk yeniden kurulan özet tablodan gelir (Geciken Siparişler, Bekleyen
   İadeler). Hangisinin hangisi olduğu 5.2.1'de.
3. **Panelden pazaryerine (yazma).** Bir düğme (İşleme Al, İadeyi Onayla, Yayınla, Platformlarda Güncelle…) bir
   **yazma işi** açar. İş ancak kapılar açıksa gider: sunucu yazma bayrağı, bağlantının **Dış Yazma Ana Anahtarı**,
   işin türüne göre akış izni ve bağlantının **Bağlı ve Aktif** olması (2.5). Otomatik fiyat/stok gönderimi sahip
   kararıyla bütün bağlantılarda kapalıdır; elle gönderim çalışır.
4. **Faturalar → E-Faturam.** Panel hiçbir zaman kendiliğinden fatura kesmez. Sahip ya da yönetici önizlemede
   **Onayla ve Faturayı Kes** dediğinde iş E-Faturam'a gider; kesilen faturanın **linki** sonra pazaryerine
   kendiliğinden iletilir ve gerekirse yeniden denenir (4.4).
5. **Arka plan.** Her şey tek PostgreSQL veritabanındadır. Worker işleri çalıştırır, API paneli ve webhook'ları
   karşılar, Caddy HTTPS ucudur. Günde iki yedek alınır, sağlık izleme 5 dakikada bir bakar (6).

---

## 1. Sayfa Haritası ve Panelin Kabuğu

### 1.1 Menü ve sayfalar

**Sol menü** sırasıyla: Genel Bakış, Ürünler, Siparişler, İadeler, Faturalar, Ürün Soruları, Raporlar, Promosyon,
Entegrasyonlar, Eşleştirmeler, İşlem Takibi. Altta **Sistem Ayarları** ve **Oturumu Kapat** (`App.tsx:201-202,219`).

| Sayfa (adres) | Ne işe yarar | Ayrıntı |
|---|---|---|
| **Genel Bakış** (`/dashboard`) | Günlük operasyon merkezi: Bekleyen Siparişler, Geciken Siparişler, Bekleyen İadeler, Faturalar (Bekleyen / Yaklaşan), İptal (bugün), Düşük Stok kartları (`DashboardPage.tsx:268-280`); dönem ve mağaza seçimiyle satış özeti; **Raporu İndir** (CSV). Her kart ilgili listeyi filtreli açar. | 5.2 |
| **Ürünler** (`/products`) | Ana katalog. Sekmeler: Tüm Ürünler, Satışa Açık, Taslak, Satışa Kapalı. Yeni Ürün (`/products/new`), ürün düzenleyici (`/products/:id`, beden tablosu yapay zekâsı burada), İçe Aktarımlar (`/imports`, menüde yok) buradan açılır. | 3 |
| **Siparişler** (`/orders`) | Bütün pazaryerlerinin siparişleri; durum sekmeleri, kargo/fatura/alıcı tipi filtreleri, İşleme Al, Paket Oluştur, etiket, fatura menüsü. | 4.2 |
| **İadeler** (`/returns`, detay `/returns/:id`) | İade talepleri; onay/ret, toplu karar, stoğa geri alma (Satılabilir — Stoğa Ekle, Karantina, Hasarlı, Teslim Alınmadı). | 4.3 |
| **Faturalar** (`/invoices`, detay `/invoices/:id`) | Fatura çalışma alanı. Sekmeler: Tümü, Faturalandırılmamışlar, Faturalandırılmışlar, Süresi Yaklaşanlar, Gizlenenler (`invoicing/pages.tsx:359`). Fatura kesmek sahip/yönetici onayı ister. | 4.4 |
| **Ürün Soruları** (`/questions`) | Pazaryeri müşteri soruları; Cevap Bekleyenler, Cevaplananlar, Süresi Dolanlar, Diğer Durumlar, Tümü; Hazır Cevaplar. | 4.6 |
| **Raporlar** (`/reports`) | Dönem raporları. Sekmeler: Genel Bakış, Kârlılık, Hakediş, Sipariş Kârlılığı, Ürünler, Stok, Operasyon, Müşteriler, Coğrafya (`ReportsPage.tsx:181-189`). Açılış dönemi Son 30 Gün. | 5.3 |
| **Promosyon** (`/promotions`) | Trendyol promosyon kârlılığı: Ürün Komisyon Tarifesi, Plus Komisyon Tarifesi, Avantajlı Ürün Etiketi, Flash Ürünler. Pazaryerine hiçbir şey göndermez. | 5.4 |
| **Entegrasyonlar** (`/integrations`, `/integrations/:id`) | Pazaryeri ve E-Faturam bağlantıları; bağlantı menüsü, akışlar, dış yazma anahtarları, Verileri Sıfırla. | 2 |
| **Eşleştirmeler** (`/mappings/categories`) | İki sekme: **Kategori Eşleme** ve **Marka Eşleme** (`MappingPages.tsx:92`). Panel kategori, seçenek, özellik ve markalarının pazaryeri karşılıkları. Markalar `?view=brands`. | 3.10 |
| **İşlem Takibi** (`/jobs`) | Arka plan işleri. Ana sekmeler: **İşler**, **Loglar** (Ürün Hareketleri, Pazaryeri Gönderimleri), **API Sağlık**, **Kuyruk ve Kota**, **Bildirimler** (`operations/pages.tsx:53,590`). | 6.10 |
| **Sistem Ayarları** (`/settings`) | Güvenlik ve Oturumlar, Kullanıcılar, Veri, Yapay Zekâ, Kargo Ayarları, Görünüm. | 7 |

Menüde olmayan detay sayfaları: Gönderi Ayrıntıları (`/shipments/:id`), Fatura detayı (`/invoices/:id`), İçe Aktarma
Ayrıntıları (`/imports/:id`). Ayrı Kategoriler/Markalar/Özellikler sayfası yoktur; hepsi Eşleştirmeler'dedir.

**Eski adresler** yönlendirilir (`App.tsx:226`): `/settings/security` → `?tab=auth`, `/settings/appearance` →
`?tab=read`, `/catalog/categories` ve `/catalog/attributes` → Eşleştirmeler, `/catalog/brands` → Marka Eşleme,
`/shipments` → Siparişler (Kargoda), `/orders/:id` → Siparişler, bilinmeyen her adres → Genel Bakış.

### 1.2 Menü rozetleri

- **Siparişler**: bekleyen sipariş sayısı. Genel Bakış'taki "Bekleyen Siparişler" kartıyla aynı sorgu (`App.tsx:94-95`).
- **İadeler**: bekleyen iade sayısı (yalnız **Aksiyon Bekliyor** talepler, 4.3).
- **Faturalar**: süresi yaklaşan fatura sayısı; 0'dan büyükse rozet uyarı rengine döner.
- **Ürün Soruları**: cevap bekleyen soru sayısı.
- 999'dan büyük sayı "999" görünür. Rozetler yalnız sekme ekrandayken yenilenir; bu yenilemeler kullanıcı hareketi
  sayılmaz, açık kalan sekme oturumun boşta kalma süresini uzatmaz (`App.tsx:90-96`).

### 1.3 Üst çubuk, arama, tema

- **Menüyü sabitle / daralt**: üstteki iğne düğmesi; tercih yalnız bu tarayıcıda tutulur (`App.tsx:100,188`). Daralmış
  menü fare üstüne gelince geçici açılır, sayfa kaymaz.
- **Telefon (≤768 px)**: sol menü gizlenir, üst çubukta **Ana menüyü aç** düğmesi çıkar, menü çekmece olarak açılır.
  Aşağı kaydırınca üst çubuk gizlenir, yukarı kaydırınca gelir (64 px'ten sonra, 6 px eşikle; `App.tsx:51-68`).
- **Sayfa değişince** sayfa en üstten açılır (sahip kararı, 12 Kas). Aynı sayfada filtre ya da sekme değişince kaydırma
  korunur (`App.tsx:78-83`). Açık açılır menüler sayfa değişince kapanır (`App.tsx:125`).
- **Hızlı Arama** (Ctrl+K / ⌘K ya da büyüteç): yalnız **sayfaya gitme** listesidir. Listede Genel Bakış, Ürünler,
  Siparişler, İadeler, Faturalar, Ürün Soruları, Raporlar, Promosyon, Entegrasyonlar, Eşleştirmeler, İşlem Takibi, Sistem
  Ayarları var, yani menünün bütün sayfaları (`App.tsx:206-220`). Sipariş, ürün ya da müşteri **aramaz**.
- **Koyu Tema / Açık Tema** düğmesi: tercih yalnız bu tarayıcıda (`ravencia.visualTheme`), varsayılan koyu
  (`visual-theme.ts:9-16`). Giriş ekranı da bu tercihi okur.
- **Sayı alanları**: odaktaki sayı alanının üstünde fare tekerleği çevrilince alan odaktan çıkar; değer yanlışlıkla
  değişmez (`App.tsx:257-264`).
- **Zil** (bildirim geçmişi): 7.4.

### 1.4 Oturum ve giriş

- **Beni Hatırla (30 gün)** varsayılan açıktır (`App.tsx:392-393,434`). Sunucu kuralları (`SessionLifetime.cs:5-14`):
  - **Hatırlanan oturum**: en çok 30 gün; 7 gün hiç kullanılmazsa kapanır; tarayıcı kapanınca da sürer.
  - **Hatırlanmayan oturum**: 30 dakika boşta kalınca, 12 saat dolunca ya da tarayıcı kapanınca biter.
  - **İkinci adımı tamamlanmamış oturum** (iki adımlı doğrulama ya da zorunlu parola değişikliği bekleyen): her zaman 30
    dakikalık kısa süre.
- **Parolamı Unuttum** parola sıfırlamaz; yalnız "sistem yöneticinizle iletişime geçin" yazar (`App.tsx:442`). Parolayı
  Sahip ya da Yönetici **Ayarlar › Kullanıcılar**'dan sıfırlar (7.5); kullanıcı yeni tek kullanımlık parola alır.
- **Yeni parola** 15–64 karakter; büyük ve küçük harf, rakam ve simge içermeli (`App.tsx:467`).
- Oturum açıkken sunucu "oturum yok" (401) derse panel yeniden yüklenmeden giriş ekranına döner; girişten sonra aynı
  sayfaya devam edilir (`App.tsx:243-252`).
- Tarayıcıda tutulan bildirim geçmişi ve hatırlanan sayılar **hesap ve kullanıcı başına** ayrı tutulur (`App.tsx:274-275`).
- İki adımlı doğrulama, oturum listesi, giriş hız sınırları: 7.1.

---

## 2. Pazaryeri Bağlantıları ve Veri Eşitleme

> (canlı, 12 Kas 2026 akşamı) Yedi bağlantının hepsi **Pasif** (`DISABLED`, 16:52Z'de elle); 6 pazaryeri bağlantısında
> **Tam Sıfırlama** başarıyla bitti. Panelde 0 sipariş, 0 iade, 0 finans kaydı, 0 ürün bağı, 0 kategori/marka listesi
> var; 3 webhook aboneliği `ACTIVE` duruyor. Bağlantılar yeniden aktifleşene kadar hiçbir akış çalışmaz.

### 2.1 Bağlantı kurma

**Yeni Bağlantı penceresi** (Entegrasyonlar › Ekle): platform kartı seçilir, alanlar platforma göre değişir
(`IntegrationPages.tsx:262-270`; sunucu doğrulaması `MarketplaceConnectionService.cs:109-160`).

| Platform | Ad alanı | Mağaza alanı | Kimlik bilgisi | Ortam |
|---|---|---|---|---|
| Trendyol | Bağlantı Adı | Mağaza Kimliği (Seller ID) | API Key + API Secret + **User-Agent kimliği** (zorunlu; "SatıcıID - FirmaAdı" biçimi dışı yalnız uyarı, Türkçe harf ya da kontrol karakteri ret; `MarketplaceConnectionService.cs:128-131`) | Canlı / Test Ortamı (Stage / SIT) |
| Hepsiburada | Mağaza Adı | Hepsiburada Mağaza ID | Entegratör Kullanıcı Adı (varsayılan `ravencia_dev`, `IntegrationPages.tsx:57`) + Servis Anahtarı | Canlı / Test Ortamı (SIT) |
| N11 | Bağlantı Adı | N11 Mağaza Adı (test, anahtarın bu mağazaya ait olduğunu denetler) | App Key + App Secret | yalnız Canlı |
| Pazarama | Bağlantı Adı | Pazarama Mağaza Adı (API doğrulamaz, yalnız ayırt etmek için) | Client Id + Client Secret | yalnız Canlı |
| PttAVM | Bağlantı Adı | Mağaza ID (yalnız rakam) | API Kullanıcı Adı + API Şifresi | yalnız Canlı |
| Shopify | Bağlantı Adı | Shopify Mağaza Adresi (kısa ad ya da myshopify.com) | Shopify Uygulama Tokenı (`shpat_…`) | yalnız Canlı |
| Trendyol E-Faturam | Bağlantı Adı | VKN / TCKN | Hesap E-Postası + Hesap Parolası | Canlı / Test |

- Kimlik bilgileri bağlantıya bağlı şifrelemeyle saklanır, bir daha gösterilmez (`MarketplaceConnectionService.cs:198`).
  Kartta "Kimlik Bilgileri: Şifreli kayıtlı / Bekleniyor" yazar (`IntegrationPages.tsx:58`).
- **Kimlik yenilenince eski anahtar hemen silinir** (sahip, 12 Kas 2026: eski anahtarı hiçbir şey okumaz). Eskiden
  kalmış "iptal edilmiş" anahtarları saatlik temizlik siler (`MarketplaceConnectionService.cs:460-461`;
  `ConnectionLeftovers.cs:8-9`).
- Aynı platform + ortam + mağaza kimliği ikinci kez açılamaz (`CONNECTION_ALREADY_EXISTS`,
  `MarketplaceConnectionService.cs:158`).
- Ortam ve mağaza bağlantı eklenirken belirlenir, **sonradan değiştirilemez**; başka mağaza ya da ortam = yeni bağlantı
  (`IntegrationPages.tsx:836`).
- Test ortamı yalnız Trendyol, Hepsiburada ve E-Faturam'da var; N11, Pazarama, PttAVM'nin test ortamı yok, Shopify'da
  kurulmuyor (`MarketplaceConnectionService.cs:115-119`). Test bağlantısı en sık 10 dakikada bir okunur
  (`ScheduledOrderPollingCadencePolicy.cs:8`).

**Bağlantı durumları** (iç ad → ekranda):

| İç durum | Kartta | Anlamı ve geçiş |
|---|---|---|
| `DRAFT` | **Test Edilmedi** | Yeni oluşturuldu ya da kimlik/User-Agent değişti (`MarketplaceConnectionService.cs:170,401,490`). Okuma yapmaz. |
| `VERIFIED` | **Bağlı, Aktifleştirilmedi** | Bağlantı testi geçti. Okur, ama canlı ortamda pazaryerine yazmaz; menüde **Bağlantıyı Aktifleştir** çıkar (`IntegrationPages.tsx:145-149`). |
| `ACTIVE` | **Bağlı ve Aktif** | Etkinleştirildi. Şart: başarılı test **ve** pasife alındıktan sonra yeni bir test (`MarketplaceConnectionService.cs:555-557`). |
| `DISABLED` | **Pasif** | **Bağlantıyı Pasife Al** (`MarketplaceConnectionService.cs:569`). Okuma ve yazma durur; kimlik ve mağaza bilgisi kartta "Gizli" görünür. **Trendyol ve Shopify webhook'u pazaryerinde de kapatılır**, bağlantı yeniden aktifleşince panel webhook'u kendiliğinden açar ve aradaki siparişleri okur; Hepsiburada webhook'u elle tanımlı olduğu için kapatılmaz (sahip kararı, 13 Kas; 2.6). |
| `HIDDEN` | Veriler Gizli | Ayarlar'daki veri görünürlüğü; doğrudan etkinleştirilemez (`MarketplaceConnectionService.cs:552`). |
| `DELETED` | listede yok | **Bağlantıyı Sil** sonrası; ayarları ve satırı hemen ardından kaldırılır (2.7). |

Bağlantı penceresinin başlığı da aynı adları kullanır: kapatılmış bağlantı **Pasif**, hiç test edilmemiş ya da kimliği
değişmiş bağlantı **Test Edilmedi** (`connectionStatusLabel`, `integration-helpers.ts:274-281`); diğer durumlar ortak etiketlerden gelir.

**Etkinleştirince ne olur.** Pasif ya da taslak bağlantı etkinleşince bekleyen zamanlanmış işler iptal edilir ve **ilk
çekim planı** kuyruğa girer (`MarketplaceConnectionService.cs:562-564,578-582`). Menüdeki **Verileri Çek** aynı planı elle
başlatır (`IntegrationPages.tsx:154`). Plan (`MarketplaceConnectionService.cs:921-951`):

1. Referans listeleri: kategori ağacı (her platform; Shopify'da mağazanın ürün tipleri), Trendyol ve Pazarama'da marka
   listesi; eşlenmiş kategorilerin özellik/değer listeleri ardından. Hepsiburada'da katalog listeleri siparişten sonra.
2. Sipariş geçmişi tam okuma (`*_ORDER_RECOVERY_SYNC`).
3. İade tam okuma (`RETURN_SYNC`).
4. **Ürün çekimi bu planda asla yok** (`MarketplaceConnectionService.cs:940`): ürünler Ürünler › **Platformdan Ürün
   Aktar** ile, kendi seçenekleriyle gelir (3.11).

İlk okuma derinliği: Hepsiburada **1 ay**, diğerleri **3 ay** (`SynchronizationPolicies.cs:1382`); Hepsiburada iadeleri
30 gün (`SynchronizationPolicies.cs:178`); Shopify iadeleri 180 gün (`docs:shopify:30`).

**Bağlantı testi ne okur** (hiçbir şey yazmaz):

- Hepsiburada: en çok bir sipariş kaydı. Kullanıcı adı reddedilirse Mağaza ID'yi kullanıcı adı olarak dener ve çalışanı
  saklar (`docs:hepsiburada:20`).
- N11: ürün ve sipariş listesinden birer kayıt; anahtarın mağazaya ait olduğunu denetler.
- Pazarama: ürün, sipariş ve marka listesinden birer kayıt.
- PttAVM: mağaza bilgisi, ana kategori listesi, son iki günün siparişleri, stok listesinin ilk sayfası.
- Shopify: mağaza, izinler (`read_products`, `read_inventory`, `read_orders`, `read_locations`; yazma için
  `write_products`, `write_inventory`; 60 günden eskisi için `read_all_orders`), para birimi, depolar
  (`IntegrationPages.tsx:72-92`).
- Kimlik reddinden sonra otomatik yeniden test: 15 dk → 1 sa → 6 sa aralıklarla (`SynchronizationPolicies.cs:1119-1121`).

### 2.2 Entegrasyonlar sayfası

**Liste kartı** (`IntegrationPages.tsx:107-215`): mağaza kimliği, Kimlik Bilgileri, Ortam (Canlı / Test Ortamı),
**Dış Yazma** (Açık / Salt-okunur), alt köşede **Bağlantıyı Test Et** ve **Yönet**. Üç nokta menüsü (`IntegrationPages.tsx:148-157`):
Bağlantı Ayarlarını Aç · Bağlantıyı Aktifleştir / Bağlantıyı Pasife Al · **Bağlantıyı Test Et** ·
Bağlantıyı Sil · Verileri Çek (pazaryerleri) · Verileri Sıfırla (E-Faturam hariç). Test düğmesi her yerde (menü, kart,
Mağaza ve Kimlik sekmesi) **Bağlantıyı Test Et** adını taşır.

**Yönet** bağlantı ayarları penceresini açar; sekmeler (`IntegrationPages.tsx:825-837`):

- **Genel Bakış**: Kimlik Bilgisi, Son Doğrulama, **Pazaryerine Yazma** özeti ("Otomatik: … · Manuel: …"), **Akış
  Sağlığı** (güncel akış / toplam), **Dikkat İsteyenler** (gecikmiş, kısmen hatalı, ilerlemeyen ya da hiç başarılı olmamış
  akışlar). Test bağlantısında üçüncü kutu "Test Ortamı İşlemleri: Hazır / Bloke" (`IntegrationPages.tsx:806-812`).
- **Veri Eşitleme**: zamanlanmış akışlar tablosu (Akış · Durum · Son Başarılı · Açık/Kapalı anahtarı), filtre çipleri
  Tümü / Dikkat / Bekleyen / Kapalı, grup başına "Tümünü Aç/Kapat" (`IntegrationPages.tsx:813-823`). Akışın adına
  tıklayınca sıklık penceresi açılır: **Otomatik Çalışma**, **Kontrol Sıklığı**, **Tekrar Payı**, **Seyrekleşme /
  Seyrek Sıklık**, imleç bilgileri, "Son 24 Saatte Deneme Limiti Dolan" iş sayısı ve **Şimdi Çalıştır** (`IntegrationPages.tsx:320,853`).
  Pencere bağlantıya özgü sabit **başlangıç payını** (jitter) da yazar ("Yük dağılsın diye bu bağlantının çalışma anı sabit
  bir payla…"); akış açıklamaları bağlantının kendi pazaryerinin adını söyler (N11'deki, PttAVM'deki, Trendyol'daki…).
  Kapalı akış veri çekmez; açınca kaldığı yerden devam eder. Trendyol, Hepsiburada ve Shopify'da aynı sekmede **Webhook**
  paneli de vardır (`IntegrationPages.tsx:795,829`).
- **Pazaryerine Yazma**: **Dış Yazma Ana Anahtarı** ve altında **Otomatik Dış Yazmalar** (Fiyat Gönderimleri + Stok
  Gönderimleri) ile **Manuel Dış Yazmalar** (Kargo ve Etiket İşlemleri + İade Kararları; PttAVM'de kendi **Manuel Dış
  Yazmalar** akışı) (`IntegrationPages.tsx:666-694,832-833`). E-Faturam'da bu sekme "E-Faturam'a Dış Yazma" anahtarıdır:
  kapalıyken fatura kesilmez.
- **Faturalama**: **Bu Bağlantıda Fatura Oluşturma**. Kapalıyken fatura düğmeleri pasif olur ve bekleyen gönderimler
  durur; mevcut faturalar etkilenmez (`IntegrationPages.tsx:834`). Her pazaryeri bağlantısında açılabilir
  (`MarketplaceInvoiceCreationPolicy.cs:10-15`).
- **Mağaza ve Kimlik**: mağaza bilgileri (yalnız Kayıt Adı / Hepsiburada'da Mağaza Adı değişir) ve kimlik yenileme formu
  ("API Bilgileri" / "Hepsiburada Servis Kimlik Bilgileri" / "Shopify Uygulama Yetkisi") + **Bağlantıyı Test Et**. Kimlik
  yenilenince bağlantı taslağa döner; test ve etkinleştirme yeniden gerekir (`MarketplaceConnectionService.cs:490`).
  E-Faturam'da belge türü bilgisi: kurumsal ve e-Fatura mükellefi TEMELFATURA, diğerleri EARSIVFATURA.
- **Araçlar** (yalnız N11, Pazarama, PttAVM ve test bağlantıları): N11 yayın ayarları (gönderim şablonu, hazırlık günü),
  N11/Pazarama/PttAVM fiyat-stok gönderim paneli, Pazarama satış durumu paneli, Hepsiburada SIT fiyat-stok, **Test
  Siparişi Oluştur** (yalnız Trendyol ve Hepsiburada test bağlantısı) (`IntegrationPages.tsx:837-844`).

**Akış grupları** (`IntegrationPages.tsx:695-702,738`):

| Grup | Akış (ekran adı) | İç ad |
|---|---|---|
| Sipariş Akışları | Siparişler ve Paket Durumları · Sipariş Geçmişi Kurtarma · Açık Sipariş Yaşam Döngüsü | `ORDERS`, `ORDER_RECOVERY`, `ORDER_LIFECYCLE` |
| İade Akışları | İade Talepleri · Açık İade Yaşam Döngüsü | `RETURNS`, `RETURN_LIFECYCLE` |
| Uzlaştırma Akışları | Kısa / Orta / Günlük Sipariş Uzlaştırması (Shopify'da günlük olan "Üç Günlük") · Kısa / Orta / Günlük İade Uzlaştırması · Kısa / Orta / Günlük Stok Uzlaştırması · Günlük Yayın Doğrulaması | `ORDER_RECONCILE_*`, `RETURN_RECONCILE_*`, `STOCK_RECONCILE_*`, `LISTING_VERIFY` |
| Faturalama Akışları | Paket Fatura Kontrolü | `ORDER_INVOICE_RECONCILIATION` |
| Finans Akışları | Cari Hesap ve Masraflar | `FINANCE` |
| Referans Veri Akışları | Katalog Referansları | `REFERENCE_DATA` |
| Diğer Akışlar | Kargo Bilgisi Eşitleme · Müşteri Soruları | `ORDER_CARGO_INFO`, `QUESTIONS` |

Yazma izinleri (`PRICE_WRITE`, `STOCK_WRITE`, `SHIPMENT_WRITE`, `RETURN_WRITE`, `MANUAL_WRITE`) bu tabloda görünmez;
Pazaryerine Yazma sekmesindedir.

### 2.3 Akışların sıklığı

**Nasıl çalışır.** Worker 10 sn'de bir zamanı gelen akışlara bakar (`Worker.cs:15,160`). Bir akış "zamanı geldi"
sayılır: son kuyruğa koyma + Kontrol Sıklığı + o akışın sabit kayması (jitter, 0..n sn) ≤ şimdi
(`ScheduledJobProducer.cs:316-319`). Yalnız `ACTIVE` ya da `VERIFIED` bağlantılar ve **Otomatik Çalışma** açık akışlar
kuyruğa girer; ilk veri çekimi sürerken o bağlantıya zamanlanmış iş üretilmez (`ScheduledJobProducer.cs:84-104`).
Aynı bağlantı ve kilit grubunda bekleyen/çalışan iş varsa yenisi eklenmez (yoğun sipariş akışının arkasına en çok birer
yeni sipariş, açık sipariş ve arka plan işi girebilir, `ScheduledJobProducer.cs:158-205`).

- **Kontrol Sıklığı** 30 sn – 3 gün arasına sıkıştırılır. Kullanıcının seçebileceği alt/üst sınırlar pazaryeri ve akışa
  göre değişir (ör. Trendyol Siparişler 60 sn–1 sa; açık kayıt taramaları en geç 6 saat; günlük uzlaştırma 6 sa–3 gün)
  (`SynchronizationPolicies.cs:1487-1525`).
- **Tekrar Payı**: okuma penceresinin geriye taşması; kaçan kayıt güvenliğidir.
- **Jitter** panelde görünmez; kaydederken panel değeri olduğu gibi geri yollar (`ScheduledJobProducer.cs:806-811`).
- Kullanıcı Kontrol Sıklığını değiştirip kaydederse akış "kullanıcı tanımlı" olur ve uygulama varsayılanları ona bir daha
  dokunmaz; yalnız aç/kapa yapmak bunu değiştirmez (`ScheduledJobProducer.cs:681-690,757-760`).
- **Aralığı 1 gün ya da daha uzun akışlar yalnız 14:00–17:00 İstanbul arasında** çalışır; bir gün kaçarsa sonraki fırsatta
  (`SynchronizationPolicies.cs:1247-1268`). İstisna: Günlük Yayın Doğrulaması 03:00–06:00 İstanbul.
- Sipariş akışı kod varsayılanı 180 sn'dir, altı pazaryerinde 120 sn'ye indirilir (`ScheduledJobProducer.cs:715`;
  `ScheduledOrderPollingCadencePolicy.cs:17,76-88`). İade akışının kod varsayılanı da 180 sn'dir, ama iade okuması hiçbir
  zaman 10 dakikadan sık yapılmaz; canlı değer 10 dk (`ScheduledJobProducer.cs:723`; `SynchronizationPolicies.cs:1304-1347`).

**Canlı varsayılanlar** (canlı: `integration.connection_sync_policies`, 12 Kas 2026; hiçbiri kullanıcı ayarı değil).
"—" = o pazaryerinde akış yok.

| Akış | Trendyol | Hepsiburada | N11 | Pazarama | PttAVM | Shopify |
|---|---|---|---|---|---|---|
| Siparişler ve Paket Durumları | 2 dk | 2 dk | 2 dk | 2 dk | 2 dk | 2 dk |
| Sipariş Geçmişi Kurtarma | 15 dk | 15 dk | 15 dk | 15 dk | 15 dk | 1 sa |
| Açık Sipariş Yaşam Döngüsü | 30 dk | 2 dk | — | 2 dk | 15 dk | 30 dk |
| Kargo Bilgisi Eşitleme | 30 dk | — | — | — | — | — |
| Sipariş Uzlaştırması kısa / orta / günlük | 1 sa / 6 sa / 1 gün | — | 1 sa / 6 sa / 1 gün | — | — | 45 dk / 3 sa / 3 gün |
| Paket Fatura Kontrolü | 15 dk | 30 dk | — | — | — | 6 sa |
| İade Talepleri | 10 dk | 10 dk | 10 dk | 10 dk | 10 dk | 10 dk |
| Açık İade Yaşam Döngüsü | 30 dk | 30 dk | — | — | — | — |
| İade Uzlaştırması kısa / orta / günlük | 1 sa / 6 sa / 1 gün | — | — | — | — | — |
| Müşteri Soruları | 2 dk | 2 dk | 2 dk | 2 dk | — | — |
| Cari Hesap ve Masraflar | 6 sa | 6 sa | 6 sa | 6 sa | — | — |
| Katalog Referansları (6 sa – 3 gün arası seçilebilir; `SynchronizationPolicies.cs:1519`) | 1 gün | 1 gün | 1 gün | 1 gün | 1 gün | — |
| Stok Uzlaştırması kısa / orta / günlük | 15 dk / 1 sa / 1 gün, hepsinde; **yalnız otomatik fiyat-stok yazması açık bağlantıda iş üretir** (`ScheduledJobProducer.cs:105-118`) |||||
| Günlük Yayın Doğrulaması | her gece 03:00–06:00, hepsinde |||||
| Fiyat / Stok Gönderimleri (izin) | Kapalı, hepsinde |||||
| Kargo ve Etiket İşlemleri / İade Kararları (izin) | Açık | Açık | Açık | Açık | **Kapalı** | Açık |
| Manuel Dış Yazmalar (PttAVM izni) | — | — | — | — | Açık | — |

Tekrar Payı canlıda: Siparişler 10 dk, Sipariş Geçmişi Kurtarma 10 dk, İade Talepleri 15 dk, diğerleri 0. İzin
akışları zamanlama değil **izin anahtarıdır** (aralık 0); değiştirmek sahip/yönetici yetkisi ister.

**Seyrekleşme** (sessizken aralığın büyümesi, `SynchronizationPolicies.cs:1474-1560`):

- **Webhook** (Trendyol, Hepsiburada, Shopify sipariş akışları): anlık bildirimler sağlıklıyken sipariş okuması 8 dk'lık
  güvenlik ağına, kurtarma ve açık sipariş taraması 1 saate uzar (Hepsiburada'nın açık sipariş taraması uzamaz). 6 saat
  sessizlik ya da 24 saatte 2 boşluk webhook'u sağlıksız sayar ve normal sıklığa dönülür (`SynchronizationPolicies.cs:968-985`).
- **Sessiz** (Müşteri Soruları): uzun süre yeni soru gelmezse seyrekleşir.
- **İadeler**: açık iade yoksa 20 dk (1 gün sessiz), 30 dk (1 hafta sessiz); Hepsiburada webhook'u iadeleri de getirdiği
  için onda 30/60 dk.

**İmleç** (bir okumanın kaldığı yer; `integration.sync_cursors`): bağlantı + kaynak başına son okunan konum, son deneme ve
başarı, son ilerleme. Sıklık penceresinde "İmleç Durumu", "Son İmleç İlerlemesi", "Durgunluk Takibi Başlangıcı".
Sınıflama: hiç başarı yok → Bilinmiyor; son tur 0 kayıt → Veri Yok; imleç üç tur süresi kadar ilerlemediyse → Takıldı
(`SynchronizationPolicies.cs:256-283`). Bir okuma art arda 3 kez başarısız olursa zile bildirim düşer (7.4).

### 2.4 Pazaryerleri tek tek: neler çekilir

Ortak kurallar:

- Zaman: ofsetsiz metin saat = Türkiye saati (UTC+3, yaz saati yok). Trendyol ve N11 epoch-ms'leri belgede GMT+3 denir
  ama bilerek düz UTC epoch gönderilip okunur (`docs:SKILL:29-30`). Veritabanına hep UTC yazılır.
- Kota: API ve tüm Worker'lar tek PostgreSQL jeton kovasını paylaşır (6.6).
- **Bir kaydın alanı yoksa durum çıkarılmaz; bilinmeyen bilinmeyen kalır** (ör. fatura linki yok ≠ faturasız).

**Trendyol** (REST, Basic; `docs:trendyol`)
- Bağlantı: `apigw.trendyol.com/integration/` (test `stageapigw…`); User-Agent "{sellerId} - {Entegratör}" yoksa 403
  (`docs:trendyol:12-14`). API sürümü yalnız V2.
- Kota: belge 10 sn'de uç başına 50; kod 10 sn'de toplam 50, eşzamanlı 8. Sipariş uçları dakikada 30–100 (kademe; kod en
  düşük 30'dan öğrenir), %40'ı ön plana ayrılır (`docs:trendyol:17-21`).
- Sipariş: stream `order/sellers/{id}/orders/stream`, istek başı en çok 14 gün, 3 ay geriye, sayfa ≤200; ilk okuma 3 ay,
  sıcak okuma 10 dk örtüşür (`docs:trendyol:33-35`). Paket durumu: stream her değişikliği getirir; açık paket taraması 30
  dk'lık güvenlik ağı.
- İade: `claims`, `claimItemStatus` başına ayrı imleç; yalnız TR vitrini (`docs:trendyol:40-41`). **Webhook iade
  taşımaz** (`docs:trendyol:318`).
- Soru: varsayılan son 7 gün, istek ≤14 gün, sayfa ≤50.
- Finans: `settlements`, `otherfinancials`, kargo faturası kalemleri; pencere ≤15 gün; ilk okuma 90 gün, 3 gün örtüşme
  (`docs:trendyol:42,117`). Canlı aralık 6 saat.
- Ürün/referans: onaylı ve onaysız ürünler (v2), kategori/marka/özellik/değer listeleri. **Marka hiçbir zaman adla
  eşlenmez** (`docs:trendyol:231-237`).
- Kargo/etiket: ortak etiket `common-label` (ZPL); paket işlemleri: tedarik edilemedi, takip bilgisi, kargo firması
  değiştir (paket başına 5 dk'da bir), böl (`docs:trendyol:113-114,207-208`).

**Hepsiburada** (REST, birden çok sunucu, Basic; `docs:hepsiburada`)
- Kota: katalog dakikada 180 **IP başına** (tüm hesaplar paylaşır), sipariş 600, listing 100, soru 60 (`docs:hepsiburada:26-30`).
- Saat: İstanbul yerel saatini ofsetsiz yazar; sorgular da İstanbul saatiyle (`docs:hepsiburada:70-71`).
- Sipariş: pencere ≤24 sa, limit ≤100; yalnız açık/paketlenmemiş ve 1 ay içi. Paketler limit ≤10; gönderilen/teslim/
  teslim edilemeyen listeleri en çok 5 dk'da bir. İptal listesi: son gün 15 dk'da bir, 30 gün günde bir
  (`docs:hepsiburada:40-43`; `SynchronizationPolicies.cs:1419-1466`).
- Fatura durumu: sipariş detayında `hasInvoice`, yoksa eksik-fatura listesi.
- İade: durum başına `claims`, limit ≤100; ilk 30 gün, artımlı ≥2 gün geri, her saatin ilk turu tam (`docs:hepsiburada:64-66`).
- Finans: muhasebe kayıtları, 60 günlük okuma.
- Etiket: ZPL/PDF/PNG/JPG; kargo firması değiştir.
- Webhook: Basic auth; 5 sn'de cevap, saatte 20'den fazla yavaş cevap kara listeye alır; API'den yapılan değişiklik
  webhook olarak geri gelmez. Kurulum bilgileri Hepsiburada'ya metin olarak iletilir (`webhook-setup.ts:4-13`).

**N11** (REST + SOAP; `docs:n11`)
- Test ortamı yok. Kota (dakikada): sipariş 600, ürün 120, kategori 30, SOAP 60, soru listesi 1.
- Sipariş: `shipmentPackages`, `sender=ALL` (n11depom paketleri için), sayfa ≤100, sorgu ≤15 gün (kod 14), 1 Kasım
  2024'ten öncesi yok; sipariş tarihi alanı yok, paket geçmişindeki ilk kayıt kullanılır (`docs:n11:21-25,98-101`).
- İade: SOAP, sayfa 20 sabit, yalnız talep tarihiyle; her tur son 2 gün, 45 gün yeniden okuma 30 dk'da bir.
- Finans: `N11_SETTLEMENT_SYNC`, 45 gün geri – 30 gün ileri. **Etiket yok, webhook yok.**

**Pazarama** (REST, OAuth2; `docs:pazarama`)
- Token 1 saat; satıcı panelinde yeni anahtar üretmek eskisini hemen geçersiz kılar. Test ortamı yok.
- Kota: fiyat/stok ve ürün oluşturma arası 10 sn (kod 12,5 sn); sipariş 60/dk.
- Sipariş: yalnız **sipariş tarihiyle** süzer, istek ≤1 ay (kod 28 gün); ilk 3 ay, sıcak okuma 2 gün, kurtarma 7 gün (sipariş
  listede bir gün sonra görünebilir). Açık siparişler kapanana ya da 45 gün olana kadar 3 dk'da bir numarayla okunur
  (`docs:pazarama:25-33`).
- İade: her tur 2 gün, 45 gün yeniden okuma 30 dk'da bir; durum "talep/yolda/ulaştı" ayrımı yapmaz.
- Finans: son 60 günün siparişleri tek tek. **Etiket yok, webhook yok.**

**PttAVM** (SOAP; `docs:pttavm`)
- REST API kullanılmıyor; test ortamı yok. Bozuk bir alan yanlış şifreyle aynı hatayı verir (`docs:pttavm:18`).
- Kota belgelenmemiş; kod dakikada 30; aynı istek 5 dk içinde tekrar edilemez.
- Sipariş: sayfalama yok (pencerenin tamamı tek cevap), kod 35 gün; ilk 3 ay, artımlı 2 gün; açık sipariş taraması 15 dk.
- **İade servisi yok**: "iade" satırları okunur, iade işlemleri reddedilir; her tur 2 gün, 6 saatte bir 3 ay.
- **Soru, finans, etiket yok.**

**Shopify** (GraphQL Admin 2026-10; `docs:shopify`)
- Sipariş: imleçle, sayfa 100; ilk 3 ay, `read_all_orders` izni yoksa yalnız son 60 gün.
- İade: siparişin `returns` listesi; ilk/tam okuma 180 gün.
- **Soru, finans, referans akışı ve etiket API'si yok.**
- Webhook konuları: orders/create, updated, cancelled, fulfilled, partially_fulfilled, refunds/create, products/delete
  (`ShopifyHttpClient.Webhooks.cs:16`); 8 ardışık hata aboneliği siler.

**Trendyol E-Faturam** (fatura sağlayıcı; `docs:efaturam`): pazaryeri değildir; okuma akışı, Veri Eşitleme ve Tam
Sıfırlama yok. Kalan kontör okunamaz; yetersiz bakiye yalnız fatura oluştururken hata olarak görünür (`docs:efaturam:26-38`).

### 2.5 Pazaryerine yazma: kapılar

Bir yazma işinin gitmesi için **hepsi** açık olmalı:

1. **Sunucu genel yazma bayrağı** (`FeatureFlags:ExternalWrites`; ekranda yok, kapalıysa kart "Salt-okunur" der,
   `MarketplaceConnectionService.cs:985`).
2. **Dış Yazma Ana Anahtarı** (bağlantı başına). Kapatılınca bağlı otomatik akışlar da kapanır ve bekleyen fiyat-stok
   işleri iptal edilir (`MarketplaceConnectionService.cs:416`).
3. İşin türüne göre izin: otomatik fiyat/stok için **Fiyat Gönderimleri ve Stok Gönderimleri** birlikte
   (`SynchronizationPolicies.cs:631-634`); kargo/etiket için **Kargo ve Etiket İşlemleri**, iade kararı için **İade
   Kararları** (PttAVM'de **Manuel Dış Yazmalar**).
4. Canlı bağlantı **Bağlı ve Aktif** olmalı; **Bağlı, Aktifleştirilmedi** bağlantı pazaryerine yazmaz.

**Fiyat/stok — otomatik gönderim bütün bağlantılarda kapalı** (sahip kararı, 1 Kas 2026; 30 Eki'den beri kapalı).

- (canlı) 6 pazaryeri bağlantısının hepsinde Fiyat ve Stok Gönderimleri kapalı; ana anahtar açık.
- Açılırsa: açıldığı an tüm teklifler "gönderilmiş" sayılır; kapalıyken yapılan değişiklikler geriye dönük **gönderilmez**.
  Açmadan önce pazaryerindeki stok/fiyatı elle düzeltmek gerekir (onay penceresi uyarısı, `integration-helpers.ts:6-8`;
  `SynchronizationPolicies.cs:636-640`).
- **Elle gönderim kapalı değildir**: Yayınla, **Hızlı Güncelle**, **Platformlarda Güncelle** ve toplu **Platformlara
  Güncelle** elle yazma sayılır; yalnız 1. ve 2. kapıya bakar (sahip, 9 Kas 2026; `SynchronizationPolicies.cs:643-666`).
- Otomatik açıkken nasıl işler: panel stokta mutlak efendidir, pazaryerine mutlak sayı gider. Bir değişiklik, kendisinden
  sonra başlayan bir sipariş okuması bitene kadar bekler (en çok 10 dk; webhook sağlıklıysa 2 dk), böylece araya düşen
  sipariş önce stoktan düşer (`SynchronizationPolicies.cs:670-718`). Gönderilemeyenleri Stok Uzlaştırması yeniden dener.
- Platform sınırları: Trendyol aynı isteği 15 dk tekrar kabul etmez, barkod başına stok ≤20.000; Hepsiburada sonuçta yalnız
  reddedilen SKU'ları sayar; N11'de `listPrice ≥ salePrice`, n11depom SKU'su yalnız fiyat alır; Pazarama mevcut değere eşit
  fiyat/stoğu reddeder, %70+ indirim onaya düşer; PttAVM aynı istek 5 dk tekrar edilemez.

**Ürün yayınlama/güncelleme** (ayrıntı 3.13):

- Trendyol: oluşturma v2; onaylı ürün içerik, varyant ve teslimat güncellemeleri ayrı (null gönderilmez: null alanı
  siler); onay bekleyen ürün bütün olarak; toplu sonuç 2 dk'da bir 10 dk, sonra 5/15/30 dk, 4 saatte durur
  (`docs:trendyol:153-200`).
- Hepsiburada: her varyant ayrı ilan, `VaryantGroupID` ile gruplanır.
- N11: aynı model kodu tek ürün. Pazarama: `groupCode` ile tek ürün.
- PttAVM: onay adımı yok; **varyant listesi olmadan gönderim tüm varyantları siler** (`docs:pttavm:63-72`).
- Shopify: her seferinde tüm varyant listesi gider (listede olmayanı siler).
- Panelin ürün kartı kazanır; profili olmayan kart için **Platformlarda Güncelle** reddedilir, önce ürün çekimi gerekir.

**Kargo, etiket, paket işlemleri** (Manuel Dış Yazmalar):

| | Etiket | Paket/kargo yazma | İade kararı |
|---|---|---|---|
| Trendyol | Ortak etiket (ZPL) | durum, tedarik edilemedi, takip, kargo değiştir, böl | satır bazında onay/ret |
| Hepsiburada | ZPL/PDF/PNG/JPG (SIT'te yok) | paketle, aç, kargo değiştir; satıcı iptali günde ≤100 | kabul / ön onay / ret, talep bütün olarak |
| N11 | yok | işleme alma, böl/birleştir | onay; ret için geri gönderim kargo bilgisi |
| Pazarama | yok (anlaşmalı kargo kodu PZ…) | kalem durumu, paket böl, iptal talebi kararı | onay/ret |
| PttAVM | yok | yok | yok (iade servisi yok) |
| Shopify | yok | takip bilgisi | iade bütün olarak onay/ret |

### 2.6 Webhook (anlık bildirim)

- Yalnız Trendyol, Hepsiburada, Shopify. Adres `POST|PUT /api/v1/hooks/{bağlantı}/{route-token}[/…]`
  (`MarketplaceEndpoints.cs:212-213`); bağlantı `ACTIVE`/`VERIFIED` olmalı.
- Trendyol: aboneliği panel kendisi açar (tüm durumlar); satıcı başına en çok 15, URL'de "Trendyol" geçemez; yalnız sipariş
  paketi taşır, iade taşımaz.
- Hepsiburada: panel bilgileri üretir, satıcı Hepsiburada'ya iletir; şifre yalnız o an gösterilir. "Önceki Tanımı Geri
  Yükle" ve "Test Mağazasının Tanımını Kullan" var (`IntegrationPages.tsx:353-380`). Sipariş **ve** iade olaylarını taşır.
- Sınırlar: bağlantı başına dakikada 1200, gönderen ağ başına 4800 (`WebhookRateLimits.cs:21-24`).
- Başka satıcıya ait teslimat 200 ile cevaplanır, kaydedilir, işlenmez (`WEBHOOK_MERCHANT_MISMATCH`). Aynı teslimat ikinci
  kez gelirse yeni iş açılmaz (`MarketplaceWebhookService.cs:84-87`).
- Trendyol başarısız teslimatlar yüzünden webhook'u pasif yapabilir; Worker açılıştan 1 dk sonra ve saatte bir pasif
  webhook'ları yeniden etkinleştirir (`Worker.cs:640-660`).
- **Webhook'u Kapat**: siparişler zamanlanmış okumayla gelmeye devam eder.
- **Bağlantıyı Pasife Al** (sahip kararı, 13 Kas 2026): **Trendyol ve Shopify**'da açık webhook'u pazaryerinde de kapatır
  (**Webhook'u Kapat**'ın çağrısıyla) ve kapatılan tanımı `PAUSED` olarak saklar; **Bağlantıyı Aktifleştir** webhook'u
  (**Webhook'u Aç**'ın çağrısıyla; Shopify'da aynı imza anahtarıyla) yeniden açar ve etkinleştirme açılışı aradaki siparişleri
  okur. **Hepsiburada**'da kayıt elle yapıldığı için panelden kapatılmaz: pasifken gelen bildirimler işlenmez, bağlantı
  aktifleşince yeniden alınır; pencere bunu söyler (`InstantNotificationService.cs:245,284,363`;
  `MarketplaceConnectionService.cs:584-606`; pencere metni `integration-helpers.ts:285-294`). Kapatma ya da açma pazaryerinde
  başarısız olursa bağlantı değişikliği yine yapılır, ekran notla uyarır ("Webhook'u Kapat/Aç'a basın") ve Worker kaydı
  saatte bir yeniden dener; bağlantı durumu değişirse kayıt kapanır (`InstantNotificationService.cs:323`; `Worker.cs:656`;
  `MarketplaceContracts.cs` `WebhookNotice`).
- **Bağlantıyı Sil** ise silmeden önce Trendyol ve Shopify aboneliğini aynı **Webhook'u Kapat** çağrısıyla kapatır;
  çağrı olmazsa silme yine yapılır ve "webhook kaydını pazaryeri tarafında silin" notu gösterilir; Hepsiburada'da kayıt
  elle yapıldığı için panelden kaldırılamaz, notu aynı şekilde söyler (`OperationalDataMaintenanceService.cs`
  `CloseMarketplaceWebhookAsync`; `MarketplaceContracts.cs` `WebhookNotice`).

### 2.7 Verileri Sıfırla, Tam Sıfırlama ve Bağlantıyı Sil

Entegrasyonlar'da kart menüsünde iki silme yolu vardır. Bağlantının **ayarları** (stok kuralları, kargo eşleşmeleri,
eşitleme kuralları, depo, fatura kuralları) bir sıfırlamada asla silinmez (10).

| | **Verileri Sıfırla › Tam Sıfırla** (`CONNECTION_FULL_RESET`) | **Bağlantıyı Sil** |
|---|---|---|
| Nerede | Kart menüsü › Verileri Sıfırla (E-Faturam'da yok) | Kart menüsü › Bağlantıyı Sil |
| Onay | Bağlantı adını yazmak | Bağlantı adını yazmak |
| Çalışma | Arka plan işi; önce bağlantının tüm eşitleme kilitlerini alır (en çok 2 dk bekler, yoksa 30 sn sonra yeniden) (`OperationalDataMaintenanceService.FullReset.cs:62-86`) | Veri tek işlemde, anında silinir (`OperationalDataMaintenanceService.cs:39-70`); ardından ayarları, anahtarları ve satırın kendisi kaldırılır (`MarketplaceEndpoints.cs:300-320`; `ConnectionLeftovers.cs:41-120`) |
| Kesilmiş faturalar | **Silinir** (E-Faturam'da ve GİB'de geçerli kalır; pazaryerine gönderilen link açılmaz) (`ConnectionFullResetPanel.tsx:65`) | Mali fatura varsa **silme reddedilir** (`ISSUED_INVOICES_KEPT`, `OperationalDataMaintenanceService.cs:50`); test sağlayıcının faturaları sayılmaz |
| Sipariş, iade, soru, ürün bağları, kanal fiyatları, eşleştirmeler, kategori/marka listeleri, imleçler, iş geçmişi | Silinir | Silinir |
| Eşleme şablonları, varsayılan özellik değerleri (Platform Varsayılanları) | **Silinir** (sahip kararı 12 Kas 2026, `OperationalDataMaintenanceService.FullReset.cs:166-170`) | Silinir (`ConnectionLeftovers.cs:70-73`) |
| Komisyon tarifeleri, promosyon ve reklam dosyaları | Silinir (`FullReset.cs:171-174`) | Silinir (`ConnectionLeftovers.cs:84-86`) |
| Finans hareketleri, kargo faturası kalemleri, hakediş/ödeme kayıtları | Silinir (`FullReset.cs:97-100`) | **Kalır**; bu kayıtlar bağlantıyı andığı sürece bağlantı satırı ayarsız ve anahtarsız bir "mezar taşı" olarak durur (`ConnectionLeftovers.cs:10-16,99-116`) |
| Ürünler | Yalnız bu bağlantıdan içe aktarılıp başka bağlantıda kullanılmayanlar; ortak ya da panelde oluşturulan ürün kalır, yalnız bağı gider | Bağlantının ürünleri (`OperationalDataMaintenanceService.cs:321`) |
| Kimlik bilgileri, akış ayarları, fatura/konum/stok ayarları, kargo eşleşmeleri, webhook aboneliği | **Korunur** | Silinir; bağlantı listeden kalkar |
| Stok | Siparişlerin düşürdüğü stok geri eklenmez; yeniden çekilen sipariş ikinci kez düşürmez | Silinen siparişlerin stoğu geri eklenmez |
| Pazaryerine/E-Faturam'a yazma | Yok | Yalnız webhook: Trendyol ve Shopify aboneliği kapatılır, olmazsa not gösterilir (2.6); başka yazma yok |
| Açık sorunlar | Bağlantıya/siparişe/faturaya ait açık sorunlar "Çözüldü" yapılır (`FullReset.cs:193-200`) | — |

- Tam Sıfırlama sonrası: "Otomatik eşitleme açıksa kayıtlar yeniden çekilir" (`ConnectionFullResetPanel.tsx:31`). Eski
  finans/kargo kayıtları pazaryerinin süre sınırı nedeniyle geri gelmeyebilir; yeniden çekilen siparişler faturasız
  görünür — **aynı siparişe ikinci fatura kesilmemeli** (`ConnectionFullResetPanel.tsx:65`).
- **Bağlantıyı Sil onay metni** artık bunu söyler: sipariş, iade, fatura, ürün sorusu, eşleştirme ve ayarlar silinir; yalnız
  bu mağazada olan ürünler gider, başka pazaryerinde de olan kalır; finans ve hakediş hareketleri silinmez; kesilmiş mali
  fatura varsa bağlantı silinmez (`IntegrationPages.tsx:211`).
- Bağlantıyı Sil'de ayarların hemen silinmesi bir hata yüzünden yarım kalırsa saatlik temizlik tamamlar
  (`PurgedFileRetentionService.cs:27-28`).
- Pencerenin sağladığı **seçmeli silme** (Verileri Sıfırla içinde kapsam satırları: Siparişler, Faturalar, Eşleştirmeler,
  İş Takibi Kayıtları …) aynı listeden alt kümeyi siler (`connection-data-reset.ts:7-21`).
- Sistem Ayarları › Veri'de birden çok bağlantı birlikte Tam Sıfırlanabilir (7.2 b).

### 2.8 Trendyol yurt dışı vitrinleri (cross-border)

- Trendyol siparişleri her vitrin (`storeFrontCode`) için ayrı okunur. Kodun listesi: TR, DE, SA, AE, GR, SK, RO, CZ, BG, KW
  ve AZ (`SynchronizationPolicies.cs:802`; AZ belgedeki mikro ihracat ülke kodlarından alınıp 12 Kas'ta eklendi); belge
  ayrıca QA, OM, BH'yi sayar, bunlar sipariş gelince okunur.
- Okuma sıklığı: TR ve son 1 günde siparişi olan vitrinler her turda; son 90 günde siparişi olanlar 30 dk'da bir; diğerleri
  6 sa (listedekiler) ya da 24 sa (liste dışı); 400/404 alan liste dışı vitrin 30 gün bekletilir.
- **Ülke kuralı** (sahip): SA, AZ, BG, RO, GR, AE siparişleri Trendyol'un **İstanbul ihracat deposuna** gider ama raporlarda
  ve dünya haritasında **vitrinin ülkesinde** sayılır; il dağılımına girmez. Kod TR dışındaki her vitrini böyle sayar
  (`ReportBuilder.cs:175-193`). İadeden kurulmuş ve vitrini hiçbir okumada görülmemiş sipariş "Bilinmiyor" sayılır.
- Sipariş listesindeki **Mikro İhracat** işareti yalnız Trendyol `micro=true` ile gelir; ülke fatura adresinden okunur
  (`MarketplaceSalesService.cs:3277-3282`).
- Hakediş: Azerbaycan ödemeleri ayrı şirket (10,99 TL KDV'siz platform bedeli, stopaj yok); Avrupa ayın 4. pazartesisi ödenir.

### 2.9 Platform farkları tablosu

| | Trendyol | Hepsiburada | N11 | Pazarama | PttAVM | Shopify |
|---|---|---|---|---|---|---|
| Protokol / kimlik | REST, Basic + User-Agent | REST, Basic | REST+SOAP, appkey | REST, OAuth2 | SOAP | GraphQL, token |
| Test ortamı | Var | Var (SIT) | Yok | Yok | Yok | Yok |
| Sipariş saati | epoch, UTC gibi | İstanbul yerel | epoch / TR | TR yerel | İstanbul | UTC |
| Sipariş penceresi | 14 gün/istek | 24 sa/istek | 14 gün | 28 gün, sipariş tarihine göre | 35 gün, sayfasız | imleç; izinsiz 60 gün |
| İlk okuma | 3 ay | 1 ay | 3 ay | 3 ay | 3 ay | 3 ay |
| Webhook | Var (iade yok) | Var (sipariş + iade) | Yok | Yok | Yok | Var |
| Soru | Var | Var | Var | Var | Yok | Yok |
| Finans / Hakediş | Var | Var | Var | Var | Yok | Yok |
| Etiket | Ortak etiket ZPL | Var | Yok | Yok | Yok | Yok |
| Ürün ailesi | renk = ayrı içerik | her varyant ayrı ilan | model kodu = tek ürün | groupCode | model kodu = tek ürün | tek ürün, tüm varyantlar |
| Satış durumu alanı | `onSale` | `isSalable` | `saleStatus` | `productStatus` (canlıda hiç gelmedi) | `Aktif` | `status` |
| Elle yazma izni | Kargo/İade | Kargo/İade | Kargo/İade | Kargo/İade | Manuel Dış Yazmalar | Kargo/İade |

---

## 3. Katalog ve Ürünler

> (canlı, 12 Kas 2026) `catalog.products`, `catalog.product_variants`, etkin marka ve etkin yaprak kategori sayısı **0**:
> panel kataloğu boş. Aşağıdaki kurallar koddan okundu, canlı veriyle sınanamadı.

Sık geçen dosyalar: `ProductEditorPages.tsx` (ürün düzenleyici), `ProductsPage.tsx` (liste), `MappingPages.tsx`
(Eşleştirmeler), `CatalogService.cs`, `MarketplaceJobProcessor.cs` (içe aktarma işleri), `MarketplaceSalesService.cs`.

### 3.1 Model, renk kartı, varyant

| Kavram | Ne olduğu | Kaynak |
|---|---|---|
| **Model** | Aynı **marka** + aynı **model kodu** taşıyan ürün kartlarının tamamı. Model kodu büyük/küçük harf, boşluk ve ayraç farkı gözetmeden karşılaştırılır ("abc-01" = "ABC 01"). Farklı markada aynı model kodu ayrı modeldir. Model kodu boş kart kendi başına bir modeldir. | `ProductFamilyKeyPolicy.cs:3-8` |
| **Renk kartı** (ürün kartı) | Modelin her rengi sunucuda ve pazaryerinde ayrı bir üründür (Trendyol: renk başına bir içerik). | `family-gallery-save.ts:1-4` |
| **Varyant** | Bir renk kartının bedeni/satış satırı: kendi barkodu, stok kodu, stoğu, fiyatları, maliyeti, desisi ve görsel listesi. | `ProductEditorPages.tsx:3934-3950` |
| **Tek Ürün** | Varyantları üretilmeden kaydedilmiş kartın seçeneksiz tek satırı. Birden çok seçenek satırı üretilince bu satırın yerini alırlar; kodu ve stoğu yeni satırlara dağıtılmaz. | `variant-generation.ts:133-143` |

**Ortak alanlar (model alanları)**: Ürün Adı, Açıklama, Marka, Kategori, KDV (oran + dahil mi), Garanti, Teslimat Süresi,
kategori özellikleri (`ProductFamilySharedFields.cs:3-8,21`). Düzenleyici hangi renkten açılırsa açılsın model tek ürün
gibi düzenlenir:

- **Değiştirdiğiniz** ortak alan modelin tüm renk kartlarına yazılır; **dokunmadığınız** alan her kartta olduğu gibi kalır.
  Kartlar arasında fark varsa düzenleyici farklı renkleri adıyla söyler (`ProductEditorPages.tsx:3689`).
- Marka farkı, aynı model kodunu taşıyan **her markadaki** kartlar üzerinden kontrol edilir.
- Ürün Adı'nda açılan kartın renk kelimesi geçiyorsa her kart kendi rengini o yerde alır ("Kadın Bordo …" → "Kadın Siyah …").
- Desi varyantta tutulur; değişen desi her rengin her bedenine gider.
- Renk, varyant kodları, stok, fiyatlar, görseller kartta/satırda kalır.

**Yeni renk eklenirse** aynı kayıtta o renge yeni renk kartı açılır; açılan kartın alanları kopyalanır, başlıktaki renk
kelimesi yeni renkle değişir (yoksa sona eklenir), Trendyol Web Color kopyalanmaz (`ProductFamilyNewColorCard.cs:6-13`).
Yeni satır rengini taşıyan karta gider; hiçbir satır başka rengin kartına düşmez (`ProductFamilyVariantPlacement.cs:3-17`).

**Sınırlar**: bir ürün en çok **2 seçenek grubu** kullanır (`OPTION_LIMIT_EXCEEDED`, `CatalogService.cs:2997`). Kategoride
seçenek rolünde başlık tanımlıysa yalnız onlardan seçilebilir (`OPTION_MAPPING_REQUIRED`, `CatalogService.cs:2999-3007`).
Tek kayıtta en çok 1000 varyant (`CatalogService.cs:1006`).

### 3.2 Zorunlu alanlar, Taslak ve kaydetme

**11 zorunlu alan** (sahip kararı, 10–11 Kas 2026; `required-fields.ts:10-22`):

| # | Etiket | Not |
|---|---|---|
| 1 | Ürün Adı | en çok 320 karakter (`CatalogService.cs:2946`) |
| 2 | Marka | yalnız etkin marka listelenir |
| 3 | Panel Kategorisi | yalnız etkin **yaprak** kategori (`CatalogService.cs:2947`) |
| 4 | Model Kodu | |
| 5 | Stok Kodu | **Barkodları Otomatik Oluştur** açıkken ya da kartta birden çok varyant varken sorulmaz (`required-fields.ts:33-39`) |
| 6 | Barkod | aynı istisna |
| 7 | Desi | 0'dan büyük, en çok 9.999, en çok iki ondalık; her yolda aynı sınır (`ProductDesi.cs`; Toplu Değer Ata ve Excel eskiden 9.999 / 10.000 diye ayrışırdı) |
| 8 | Garanti Süresi | 0 = garanti yok; boş = eksik |
| 9 | Teslimat Süresi | 1–30 gün |
| 10 | Liste Fiyatı | 0'dan büyük olmalı |
| 11 | Satış Fiyatı | 0'dan büyük olmalı |

Ayrıca **KDV Oranı** her kartta zorunludur (taslak hariç); düzenleyici "Zorunlu Alan Eksik" uyarısıyla durur, sunucu da
yeni kartta KDV'siz kaydı reddeder (`CatalogService.cs:1030`). KDV oranları: 0, 1, 10, 20 (`BulkProductFields.cs:23`).
Mesajlar: "Zorunlu alanları doldurun: …", alan altında "{Etiket} zorunludur." ya da "{Etiket} 0'dan büyük olmalı."
Eksik alanla açılan kart yine açılır, alan kırmızı çerçeveli kalır.

**Kaydetme düğmeleri** (`ProductEditorPages.tsx:2774-2830,3997`):

| Düğme | Nerede | Davranış |
|---|---|---|
| **Taslak Olarak Kaydet** | yeni kart ya da taslak | Zorunlu alanlar eksik olsa da kaydeder; hiçbir şey göndermez. |
| **Ürünü Oluştur** | yeni kart | Zorunlu alan eksikse kart **kendiliğinden taslak** olur ve eksikler bildirilir; tamsa satışa açık oluşturulur. |
| **Kaydet** | mevcut kart | Taslakta: eksik varsa taslak kalır, tamsa kartı **satışa açar** (alt şerit "Taslak ürün · Kaydet ile satışa açılır"). Satışa açık/kapalı kartta: eksik alan varsa kayıt **durur**, kart taslağa düşmez. |
| **Yayınlamaya Devam Et** | 1. adım | Kaydetmez, göndermez; kod ve zorunlu alan kontrolünden sonra 2. adıma (Yayınlama) geçer. |
| **Hızlı Güncelle** | mevcut, taslak olmayan kart | 3.13 |

**Taslak kuralları** (`ProductDraftPolicy.cs:5-24`):

- Taslak **hiçbir pazaryerine gönderilmez** ("Taslak ürün pazaryerine gönderilemez. Önce zorunlu alanları tamamlayıp ürünü
  Kaydet ile kaydedin.").
- Taslak **stok sayımlarına girmez** (Stokta Yok, Düşük Stok, Toplam Stok, Stok raporu).
- Yalnız yeni kart ya da zaten taslak olan kart taslak kaydedilebilir; satışa açık/kapalı kart taslağa alınamaz
  (`PRODUCT_DRAFT_NOT_ALLOWED`, `CatalogService.cs:1384-1385`).
- Taslak kategori zorunlu özelliklerini ve KDV'yi eksik taşıyabilir; kod biçimi ve benzersizliği yine denetlenir.

**11 zorunlu alan sunucuda da tutulur** (12 Kas 2026; `ProductRequiredFields.cs`, mesajlar düzenleyicininkiyle aynı):

- **Yeni kart** satışa açık ya da kapalı kaydedilirken alanlardan biri eksikse reddedilir (`PRODUCT_REQUIRED_FIELDS_MISSING`,
  422, alan altı mesajlarıyla); eksikli kart yalnız taslak olarak kalır — düzenleyicinin **Ürünü Oluştur**'u bu yüzden
  kartı taslağa çevirir (`CatalogService.cs:1042-1046`).
- **Mevcut kart** satışa açık/kapalı kalıyorsa, kaydı bir alanı boş bırakacaksa reddedilir; taslağa düşmez
  (`CatalogService.cs:1477-1488`). Garanti Süresi'nde sunucu "0" ile "boş"u ayıramaz (kart ikisini de "garanti yok" tutar);
  saklı kartta verilmiş sayılır.
- **Excel içe aktarma** taslak olmayan kartın **Desi**'sini ya da **Model Kodu**'nu silemez ("{Alan} zorunlu alandır; taslak
  olmayan üründe silinemez."; `ProductTransferService.cs:297-306`).
- **Toplu Satışa Aç** (sahip kararı, 13 Kas: "eksik olanı atlasın, bildirsin"): zorunlu alanı tam olan kartlar satışa açılır,
  eksikli kapalı kart **atlanır** ve adı, model koduyla ve eksik alanlarıyla bildirimde listelenir ("N ürün eksik alan
  nedeniyle satışa açılmadı: … Ürün düzenleme ekranından tamamlayıp tekrar deneyin."; bildirimdeki **Eksik Ürünleri Göster**
  bağlantısı kapalı kartları açar); atlanan kart seçili kalır, tekrar denenebilir (`CatalogService.cs:621-634`;
  `required-fields-skip.ts:26`). **Toplu Değer Ata › Satış Durumu** aynı kuralı uygular (`CatalogService.BulkField.cs:215`).
  Toplu Satışa Kapat zorunlu alana bakmaz.
- **Klasik içe aktarma** (`/imports`): zorunlu alanı eksik yeni ürün **taslak** açılır (`ImportJobProcessor.cs:181,229,438`).
  Dosyada **Garanti Süresi (Ay)** ve **Teslimat Süresi (Gün)** sütunları verilebilir (sahip kararı, 13 Kas): garanti 0–240 ay
  (0 = garanti yok), teslimat 1–30 gün tam sayı; geçersiz değer satırı hatalı yapar, boş hücre ya da sütunsuz dosya garanti 0 ve
  varsayılan teslimat günüyle açılır (`ImportFileReader.cs:281-284`; `ImportJobProcessor.cs:249-250`; şablon
  `imports/import-template.ts`).
- **Platformdan Ürün Aktar** yeni ürünü zorunlu alan denetimi olmadan **normal (satışa açık) kaydeder, taslak açmaz** (sahip
  kararı, 13 Kas); eksik alan sonradan düzenleyicide tamamlanır, kayıt satışta olduğu için düzenleyicide Kaydet eksik alan varken
  durur (`MarketplaceJobProcessor.cs:8457`).

### 3.3 Stok kodu, barkod, model kodu

- **Model kodu benzersiz**: yeni kart (taslak ve kopya dahil) katalogda başka kartın model kodunu alamaz: "Bu model kodu
  '{ürün}' ürününde kullanılıyor. Yeni renk veya beden eklemek için o ürünü düzenleyin." (`MODEL_CODE_IN_USE`,
  `ModelCodeUniquenessPolicy.cs:3-17`). Yalnız kullanıcının oluşturma yolu denetlenir; aile kaydı, içe aktarma ve
  güncelleme denetlenmez. En çok 160 karakter.
- **Stok kodu** her varyantta olmalı ve hesap içinde benzersiz (`SKU_CONFLICT_REVIEW_REQUIRED`, `CatalogService.cs:1044,1598`).
- **Barkod** ürün içinde ve katalogda benzersiz (`BARCODE_CONFLICT_REVIEW_REQUIRED`, `CatalogService.cs:1046-1048,1600-1602`).
- Karşılaştırmada aksanlar düşer, ı→I, büyük harf, diğer karakter dizileri tek tire (`platform-code-rules.ts:70-71`).
  Tabloda aynı kod iki satırda ise alan kırmızı yanar.

**Platform kod kuralları** (düzenleyici canlı denetler; ihlal varsa "Düzeltilene kadar Kaydet pasif kalır"; taslak kırmızı
kodla kaydedilebilir; `platform-code-rules.ts:12-37`):

| Platform | Barkod | Stok kodu |
|---|---|---|
| Trendyol, Hepsiburada | zorunlu; ≤40; yalnız harf, rakam, `.`, `-`, `_` | zorunlu; ≤100 |
| Pazarama | zorunlu; ≤500 | zorunlu; ≤100 |
| PttAVM | zorunlu; ≤250 | zorunlu; ≤100 |
| Shopify | isteğe bağlı; doluysa ≤40, aynı karakter kuralı | zorunlu; ≤100 |
| N11 | kural yok | zorunlu; ≤255 |

**Barkodları Otomatik Oluştur** (`variant-generation.ts:9-11,115-126`): model kodu büyük harfe çevrilir, Türkçe harfler
sadeleşir, boşluk "-" olur, yalnız A-Z, 0-9, ".", "-", "_" kalır; sonuna "-" ve iki haneli sıra eklenir (MZ070-01,
MZ070-02…). Kullanılmış numara atlanır; en çok 40 karakter. **Stok kodu = barkod.** Kapalıyken yeni satır boş kodla gelir.
Kolon menüleri: **Boş Barkodlara Yapıştır**, **Tüm Barkodları Panodan Değiştir**, **Eksik Stok Kodlarını Doldur**,
**Barkodları Stok Koduna Uygula**. Varyantlı kartta üstteki Stok Kodu/Barkod alanları salt okunurdur.

### 3.4 Ürünler listesi

Başlık "Ürünler"; üst düğmeler **İçe / Dışa Aktar** (dar ekranda "Excel"), **Platformdan Ürün Aktar** ("Ürün Aktar"),
**Yeni Ürün Ekle**. Özet kartları: Toplam Ürün, Stok Adeti, Aktif Ürün, Taslak Ürün, Stoksuz Ürün, Düşük Stok.
Filtreler: **Ürün Ara** (ad, stok kodu, barkod), **Stok Durumu**, **Sıralama**, **Platform Durumu** (her platform için
Aktif = tüm varyantlar eşleşti / Kısmi / eşleşme yok), **Temizle**, **Filtrele** (`ProductsPage.tsx:765-812`).

- **Satır = model**: liste satırları modele göre gruplanır. Satırın küçük resmi aile galerisinin ilk görselidir (asla beden
  tablosu değil) (`product-group-image.ts:3-5`).
- **Toplu İşlemler** (seçim yoksa pasif): Platformlara Yayınla · Platformlara Güncelle · Toplu Fiyat ve Stok Düzenle ·
  Toplu Fiyat Düzenle · Toplu Stok Düzenle · **Toplu Değer Ata** · Toplu Satışa Aç · Toplu Satışa Kapat · Seçili Ürünleri
  Sil ("Yerel katalogdan kalıcı olarak kaldır"). Silinen ürünlerin görselleri ve beden tabloları silme işleminden sonra
  dosya temizliğine verilir (`CatalogService.cs:877-881,980-983`).

### 3.5 Ürün düzenleyici

İki adım: **1 Ürün Bilgileri ve Varyantlar**, **2 Yayınlama** (`ProductEditorPages.tsx:3685`).

| Bölüm | İçerik |
|---|---|
| **Temel Ürün Bilgileri** | Ürün Adı, Satış Durumu (Satışa Açık / Satışa Kapalı), Marka, Panel Kategorisi, Model Kodu, Stok Kodu, Barkod, Desi (+ **Hesapla**: En × Boy × Yükseklik / 3000), Garanti Süresi (Ay), Teslimat Süresi (Gün), Açıklama |
| **Fiyat, Stok ve Vergi** | Liste Fiyatı, Satış Fiyatı, Maliyet (yalnız varyantsız/tek varyantlı kartta), Para Birimi (TRY/USD/EUR), KDV Oranı, KDV Dahil mi, Stok (varyantlıysa salt okunur **Toplam Stok**), Güvenlik Stoğu, **Beden Tablosu**, **Pazaryerlerine Özel Fiyat** |
| **Kategori Özellikleri** | Seçilen kategorinin özellikleri; Trendyol için **Web Color** ve **Varyant Renklerini Otomatik Aktar** |
| **Görseller** | Tek aile galerisi (3.6) |
| **Ürün Seçenekleri** | Seçenek grupları ve değerleri; **Varyantları Oluştur**, **Barkodları Otomatik Oluştur**, **Ürünleri Ekle** / "Seçenekler Güncel" |
| **Ürün Seçenek Grupları** | Varyant tablosu: Seçenek, Barkod, Stok Kodu, Stok, Satış Fiyatı, Maliyet, Liste Fiyatı; araç çubuğu: Oluşan Varyantları Temizle, Pazaryeri Kodları, Toplu Düzenle |

- Kategori değişince seçilen özellikler ve (yeni kartta) varyant satırları sıfırlanır.
- Varyantlı kartta 2. bölümün Liste/Satış Fiyatı **sonra eklenecek** satırların ilk fiyatıdır; var olan satırlar değişmez.
  Yeni satırın stoğu 0 başlar.
- Kolon başlığındaki kalem: "Toplu {Stok/Satış Fiyatı/Maliyet/Liste Fiyatı} Değiştir" — **Renk ve beden seçin** filtresi,
  eşitle/artır/azalt, **Uygula**; sonuç 0'ın altına inmez; kalıcı olması için Kaydet gerekir.
- Ürün Seçenekleri'nde yazılan değer grupta yoksa ekleme düğmesi çıkar; bu yalnız panel değeri ekler, pazaryeri eşlemesi
  yapmaz (`OptionValueAddButton.tsx:4-6`).
- Bir rengin bedenleri farklı görsellerle gidiyorsa "Trendyol uyarısı: … Aynı görselleri o rengin tüm bedenlerine atayın."

### 3.6 Görseller

**Tek galeri** (`family-gallery-save.ts:1-11`): modelin tüm renklerinin görselleri tek şeritte. Kaydet galeriyi renk renk
yazar:

- varyantlara atanmış görsel → o varyantların renklerine;
- beden tablosu → her renge;
- bu oturumda eklenen, atanmamış yeni görsel → her renge;
- diğerleri bugün hangi renklerdeyse orada kalır; atama olmadan hiçbir şey taşınmaz.
- Kutucuk altındaki ad görselin gideceği renktir ("Siyah", "Tüm Renkler"). Kutucuktaki × kayıtlı görseli **modelin tüm
  renklerinden hemen** kaldırır; aile görselinin ×'i Kaydet'te kaldırır ve **Geri Al** çıkar.
- Başlık düğmeleri: **Beden Tablosu Seç**, **İşareti Kaldır**, **Tümünü Temizle**. Yükleme: JPEG ya da PNG, sürükleyerek
  sıralama.

**Varyant görselleri**: satırdaki görsel simgesi **Varyant Görsellerini Seç** penceresini açar; sayı, varyantın
pazaryerine gideceği listedir. Atamasız varyant kartın galerisiyle gider (yalnız başka renklere atanmış görseller hariç).
**Seçeneklere Görsel Ata** penceresi bir seçenek değerini (ör. Renk: Bordo) taşıyan tüm satırlara aynı görselleri atar;
alt düğme **Bu Değerin Görsellerini Kaldır**.

**Yeni varyantın ön seçimi** (sahip, 12 Kas 2026; `family-gallery-save.ts:152-158`): sonradan eklenen satır, kendi
**renginin** mevcut satırlarının **hepsinde** ortak olan görsellerle ve **bedeninin** başka renklerdeki satırlarının hepsinde
ortak olanlarla (en az iki renk varsa) seçili gelir. Bu bir kez, satır eklenirken olur; ad ya da görünüme göre eşleme
yapılmaz. Yeni renge hiç görsel atanmadıysa kaydette uyarı: "Yeni renge görsel atanmadı: …".

**Beden tablosu her zaman sondadır** (`size-chart-order.ts:1-3`; `SizeChartPlacement.cs:7-8,25-37,74-86`):

- Şeritte, her varyantın listesinde ve pazaryerine gidende son görseldir; öne alınamaz.
- Seçim pencerelerinde kutucuğu işaretli ve kilitlidir: "Otomatik eklenir (sonda)".
- Pazaryerinin görsel sınırı aşılırsa beden tablosu yine gider, ondan önceki görselin yerine.
- Kendi beden tablosu olmayan renk kartı modelin beden tablosunu alır (arşivlenmemiş, beden tablosu olan **en eski** kart).

### 3.7 Beden Tablosu Oluştur (yapay zekâ)

Düğme **Beden Tablosu** (Güvenlik Stoğu yanında), pencere **Beden Tablosu Oluştur** (`SizeChartDialog.tsx:17-25,105-110`).

Ön koşullar: kart kaydedilmiş ("Önce ürünü kaydedin.") ve **Beden** seçeneği var; Claude API anahtarı kayıtlı ("… Ayarlar ›
Yapay Zekâ bölümünden hesap sahibi ya da yönetici girmeli."); beceri yüklü (`SizeChartService.cs:67-73`); ürün görseli var
(ikon ondan seçilir, `SIZE_CHART_NO_IMAGE`).

Akış:

1. Ölçü tablosu: 1–8 kolon (cm), kolon adı ≤24 karakter, en çok 30 beden, her hücre 0 < değer ≤ 500, en çok bir ondalık;
   değer yuvarlanmaz, fazla ondalık reddedilir (`SizeChartRules.cs:12-41`). **Kolon Ekle**.
2. **Oluştur**: ölçüler çağrıdan önce saklanır. Yüklü beceri dosyasıyla Claude bir **SVG** çizer ve panel bunu **1200×1800
   PNG**'ye çevirir (`SizeChartRasterizer.cs:6-9`); beceri yüklenmemişse panelle gelen kopya tabloyu aynen çizer ve
   **1200×1800 JPEG** üretir (`SizeChartService.cs:76-77,205`). Süre 30–180 sn.
3. Önizleme; **Yeniden Oluştur** ya da **Kullan**. Kullan görseli son görsel olarak ekler; karta yalnız **Kaydet** ile bağlanır.
   **Kaydet pazaryerine bir şey göndermez**; pencere metni de böyle der. Pazaryerine ayrı bir gönderim gerekir (Hızlı Güncelle /
   Platformlarda Güncelle › Görseller ya da Yayınla).
4. Token ve maliyet pencerede gösterilir; başarısız ya da vazgeçilen çağrı da ücret yazar (7.3).

Hata kodları: `SIZE_CHART_NO_SIZES` (Beden seçeneği yok), `SIZE_CHART_INVALID` (ölçü kuralı), `AI_SKILL_MISSING` (beceri
yüklenmemiş), `AI_IMAGE_INVALID` (çizim SVG değil), `SIZE_CHART_WRONG_SIZE` (1200×1800 değil), `SIZE_CHART_NOT_ON_PRODUCT`
(`SizeChartService.cs:83-173`). **Beden Tablosu Seç** ile elle yüklenmiş bir görsel de beden tablosu olarak işaretlenebilir.
Canlıda katalog boş olduğu için üretim canlıda doğrulanmadı (12 Kas'ta 3 başarısız çağrı, 7.3).

### 3.8 Toplu Değer Ata

Ürünler › Toplu İşlemler › **Toplu Değer Ata** (`BulkFieldDialog.tsx`). Alanlar: KDV Oranı, KDV Dahil mi, Desi, Garanti
Süresi (Ay), Teslimat Süresi (Gün), Satış Durumu, Marka, Kategori, Güvenlik Stoğu, Açıklama (`BulkProductFields.cs:9-21`).
Yalnız Sahip ve Yönetici kullanabilir.

Kurallar (`CatalogService.BulkField.cs:7-13`):

- Model alanları seçilen modellerin **tüm renk kartlarına** yazılır; Marka aynı model kodunun başka markadaki kartına da.
- **Desi** her varyanta; **Güvenlik Stoğu** her varyantın her pazaryeri teklifine yazılır.
- Yalnız seçilen alan yazılır; kartın diğer alanları denetlenmez.
- **Pazaryerine hiçbir şey gönderilmez.** Değişen kartların bağlantıları "yerel değişiklik var" işaretlenir; gönderim için
  **Platformlara Güncelle** gerekir.
- Arada başka kayıtla değişen model adıyla bildirilir ve atlanır; diğerleri yazılır.
- Sınırlar: istekte 1–500 kayıt; istemci modelleri bölmeden 500'lük parçalarla gönderir. Garanti ≤240, desi ≤9999 (Excel ve
  ürün düzenleyiciyle aynı tek sınır, `ProductDesi.cs`),
  güvenlik stoğu ≤100.000, açıklama ≤60.000 karakter (`BulkProductFields.cs:23-27`).
- Taslak buradan satışa açılamaz. Garanti "0 = garanti yok" boş olarak saklanır. Satış Durumu'nu Satışa Açık yapmak,
  zorunlu alanı eksik kapalı kartı açmaz: kart adıyla bildirilir ve bırakılır (`CatalogService.BulkField.cs:211`).

### 3.9 Kategoriler, özellikler, markalar, seçenekler (panel tarafı)

Hepsi **Eşleştirmeler** sayfasında yönetilir.

| Varlık | Oluşturma | Kaldırma | Kurallar |
|---|---|---|---|
| **Kategori** | Kategori Eşleme › "1. Yeni Kategori Ekle" › **Kategori Ekle**; ekran yalnız **kök** kategori ekler | **Silme yok**; "Kategoriyi kaldır" pasif yapar | ad 1–160; aynı üstte aynı ad `CATEGORY_DUPLICATE`; pasif aynı ad yeniden etkinleşir (`CatalogService.cs:26-71`). Ürüne yalnız etkin yaprak kategori. Kategori başına ≤2 seçenek grubu (`CatalogService.cs:407-409`) |
| **Marka** | Marka Eşleme › "1. Yeni Marka Ekle" › **Marka Ekle**; ayrıca Platformdan Ürün Aktar, klasik içe aktarma ve Excel'in **Marka** sütunu ada göre bulur ya da açar (tek kural, `PanelBrandByName.cs`) | pasif | ad 1–160; "Bu marka zaten var." (`CatalogService.cs:79-107`). Üç yolda da aynı ad aynı markaya iner ("Gümüş Moda", "GUMUS-MODA" tek marka); kapalı marka yeniden açılmaz, yanına ikincisi de açılmaz |
| **Seçenek** (Renk, Beden) | "3. Ürün Seçenekleri" › Yeni Seçenek Başlığı | pasif | ad Renk/Beden/Size/Web Color ise seçenek sayılır |
| **Özellik** | "4. Kategori Özellikleri ve Değerleri" › Yeni Özellik Başlığı | pasif; değerleri de pasifleşir | değer varsa liste, yoksa metin; yeniden adlandırma yalnız özellikte (`CatalogService.cs:109-322`) |
| **Değer** | virgülle çoklu ekleme | pasif | `ATTRIBUTE_VALUE_DUPLICATE`; A–Z/Z–A sıralama |

Pasif kategoriye bağlı ürün ya da eşleme için engel yok. **Kategoriyi kaldır** kategoriyi yerinde pasife alır ve kendi
üstünü korur (istek `parentId`'yi olduğu gibi yollar; alt kategori köke taşınmaz) (`category-deactivation.ts`).

### 3.10 Eşleştirmeler sayfası

Başlık "Eşleştirmeler". İki sekme: **Kategori Eşleme** ve **Marka Eşleme**. Ayrı "Özellik Eşleme" ya da "Değer Eşleme"
sekmesi yoktur; ikisi de kategori satırındaki **Kategori Özellikleri** penceresinde yapılır.

**Kategori Eşleme** bölümleri (Panel ve Platform seçicileri aynı hizadadır: iki etiket tek 18 px satırda, iki 36 px seçici aynı
yükseklikte başlar, bağlantı simgesi seçicilerin ortasındadır; bilgi simgesi etikette satır içi 16 px'tir ve aynı kural
Marka Eşleme'de de geçerlidir; 13 Kas, `mapping-workspace.css`):\n
1. **Yeni Kategori Ekle**.
2. **Yeni Kategori Eşleştirme**: **Senkronize Et** kategori listesini yeniler; platform sekmeleri (bağlantı yoksa
   "{Platform} bağlantısı gerekli"); Panel Kategorisi ↔ {Platform} Kategorisi (Shopify'da **Ürün Türü**); "Önerilen
   {Platform} kategorileri" en çok 3, "Benzerlik n/100" — tıklamak **yalnız seçer**, kaydetmez; **Eşleştirmeyi
   Kaydet/Güncelle**. Yalnız etkin yaprak panel kategorisi eşlenir.
3. **Ürün Seçenekleri**. 4. **Kategori Özellikleri ve Değerleri**.
5. **Kayıtlı Kategori Eşleştirmeleri**: Panel Kategorisi | Platform | Platform Kategorisi | Durum | İşlemler; sayfada 20
   satır. Durum: **Aktif**, **Kontrol Bekliyor**, **Zorunlu Alan Eksik**. **Aktif** yalnız zorunlu **alan** eşlemesine
   bakar, değer eşlemesine bakmaz; "yayına hazır" demek değildir (`ReferenceDataService.cs:246-291`).

**Dışa Aktar / İçe Aktar**: kategori, seçenek, özellik ve eşlemeleri JSON yedeği olarak `ravencia-eslestirme-yedegi.json`
dosyasına yazar ve geri yükler. İçe aktarma penceresinin seçenekleri: Kategoriler, Ürün Seçenekleri, Ürün Özellikleri,
Eşleşmeler; mevcut kayıtlar silinmez (`MappingPages.tsx:419`).

**Kategori Özellikleri penceresi** (başlık "Kategori Eşleştirmeleri", ilerleme "Zorunlu Alanlar x / y"):

| Düğme | Ne yapar |
|---|---|
| **Başlıkları Eşle** | Başlıkları ad, eş anlamlı ya da diğer pazaryeri köprüsüyle eşler; birden çok aday varsa atlar. |
| **Tüm Değerleri Eşle** | Eşlenmiş başlıklarda aynı adlı değerleri eşler. |
| **Platform Varsayılanları [n]** | Alan boş kaldığında gidecek sabit değer (aşağıda). Trendyol, Hepsiburada, N11 ve Pazarama'da görünür (N11'de kilitli "Marka" alanı listelenmez; `MappingPages.tsx:1013`). |
| **Gözden Geçirilecek [n]** | Çakışmalar (aşağıda). |
| **Seçenekleri Eşitle** | Yalnız Trendyol; ürün aktarım işini kuyruğa alır. |
| **Pazaryerinden Güncelle** | Kategori alan listesini yeniler. |

Alan grupları "Zorunlu" / "İsteğe Bağlı"; durum Tamam / Değerleri Eksik / Eşlenmedi; altta **Sonraki Eksik**, **Kaydet**.
**Panel Özelliği** seçicisinin grupları: "Ürün Bilgileri" (Model Kodu, Stok Kodu, Barkod, Ürün Adı), "Panel Seçenekleri",
"Panel Özellikleri". Değer editöründe: **Yalnızca Eksikler**, **Birebir Eşle**, **Önerileri Getir**, **Hızlı Değer**, **Boş
Değerleri Oluştur**, **Listeyi Yenile**, **Diğer Kategorilerde de Kullan**, **Onay Bekleyen Öneriler › Tümünü Onayla**.

**Marka Eşleme**: 1. **Yeni Marka Ekle**; 2. **Yeni Marka Eşleme** (Panel Markası ↔ {Platform} Markası, **Eşleştir**,
**Senkronize Et**, **Eşleştirmeyi Kaydet**) ve altında **Eşlenmemiş {Platform} Markaları**: ürün aktarımı markaları
kendisi eşler; burada yalnız **aktarımın eşleyemediği** markalar listelenir (eşlendiği panel markası kapalı, birden fazla
panel markasına eşli ya da aynı adlı panel markası başka bir markaya eşli); birini seçip panel markasını belirleyin ve
kaydedin (3.11); 3. **Kayıtlı Marka Eşleştirmeleri**. Marka seçici yalnız sunucuda arar
("Marka adı veya marka no ara..."); **otomatik seçim ve öneri yoktur** (`brand-reference.tsx:1-27`).

**Platform farkları:**

| Platform | Kategori | Alan/Değer | Marka |
|---|---|---|---|
| Trendyol | var | var; Web Color ve Renk ayrı | **eşleme zorunlu** (yoksa `BRAND_MAPPING_REQUIRED`) |
| Pazarama | var | var | **eşleme zorunlu** |
| Hepsiburada | var | var; bir panel değeri birden çok HB değerine bağlanabilir (çakışma paneli bunu çakışma saymaz) | eşleme yok; panel markasının adı gider |
| N11 | var | var; "Marka" alanı kilitli, ürünün markası gider | eşleme yok |
| PttAVM | yalnız kategori ağacı | yok | eşleme yok; marka adı isteğe bağlı gider |
| Shopify | Ürün Türü | yok | panel markası adıyla gönderilir |

**Eşleme şablonları** (ayrı ekran yok; **Şablon** rozeti ve **Diğer Kategorilerde de Kullan** anahtarı):

- Bağlantı geneli iki tablo: alan şablonu ve değer şablonu. Elle kaydedilen her özellik/değer eşlemesi varsayılan olarak
  şablonu günceller (`CatalogContracts.cs:402-404`).
- **Doldurma** yeni kategori eşlemesi kaydedilince ve kategorinin alan/değer listesi gelince çalışır; yalnız bir panel
  kategorisine eşlenmiş kategoriler doldurulur (`MappingTemplateFiller.cs:43-50`).
- **Asla ezmez**: kategoride o alanın eşlemesi varsa, panel değeri zaten eşliyse, kullanıcının sildiği eşleme (silinmiş
  olarak hatırlanır), pasif panel özelliği, kategorinin güncel listesinde olmayan değer.
- Marka ve kategori eşlemesi için şablon yoktur. Tam Sıfırlama ve Bağlantıyı Sil şablonları da siler (2.7).

**Platform Varsayılanları** (`AttributeDefaultsPanel.tsx:93-112`): "Ürüne girilen değer her zaman önce gelir; varsayılan
yalnızca alan boşsa kullanılır." Bağlantı geneli, her kategoride geçerli. Kayıt tek kaynak taşır: liste değeri, serbest metin
ya da ürün alanı (Model Kodu / Stok Kodu / Barkod / Ürün Adı). Zorunlu alan eşlenmemiş ama varsayılanı varsa yayın durmaz.
Liste değeri ancak kategorinin güncel listesinde etkinse sayılır; ürün alanı kaynağı boşsa yayın `PRODUCT_FIELD_EMPTY` ile
durur (`ProductPublicationComposer.cs:95-102,389-470`). N11 ve Pazarama yayını da varsayılan kullanır; artık ekrandan
ayarlanır.

**Gözden Geçirilecek Eşlemeler** (çakışmalar; Hepsiburada'nın bilinçli çoklu değer eşlemesi çakışma sayılmaz, kural
`MappingConflictRules.cs`): "Aynı Kategoride Birden Fazla Değer" — **Bunu Tut** diğerlerini siler;
"Kategoriler Arasında Farklı Değer" — **Hepsinde Bunu Kullan** (seçilen değer o kategorinin listesinde yoksa o kategori
atlanır; seçilen değer şablon olur) (`mapping-conflict-unify.ts:32-57`).

**Referans listeleri indirme**: `{PLATFORM}_REFERENCE_SYNC` işiyle salt okunur: kategoriler, markalar, kategori alanları,
alan değerleri. Zamanlayıcı günde bir çalışır; son 24 saatte alınmış liste yeniden okunmaz. **Marka listesi yalnız
Trendyol ve Pazarama'da** çekilir ve **tam liste** alınır, ada göre arama yapılmaz (`ConnectionReferencePlan.cs:20`). Alan ve
değer listeleri yalnız eşlenmiş kategoriler için okunur.

### 3.11 Platformdan Ürün Aktar

Pencere **Platformdan Ürün Aktarımı** (`ProductsPage.tsx:814-830`). Desteklenen bağlantılar: Trendyol, Shopify,
Hepsiburada, N11, Pazarama, PttAVM; durum `ACTIVE` ya da `VERIFIED`. Yöntem: **Toplu Aktarım** (varsayılan) / **Tek Ürün
Aktarımı** (Trendyol'da ürün linki ya da model kodu, HB'de ürün ID, Pazarama/PttAVM'de barkod, N11'de model kodu).

**Modlar** (`product-import-options.ts:1`):

| Ekran adı | Dokunduğu | Dokunmadığı |
|---|---|---|
| **Tam Katalog Taraması** (varsayılan) | Trendyol'da tüm katalog okunur. Yeni ürünler tam açılır. Mevcut üründe içerik yazılmaz; yalnız boş desi doldurulur, yeni bedenler eklenmez ve "Eklenmeyen yeni beden" diye raporlanır (`MarketplaceJobProcessor.cs:8509,8556,8664-8675`). Pencerenin kip altı açıklaması da bunu söyler ("içeriği, fiyatı ve stoğu değişmez"); yalnız Hepsiburada seçiliyse yeni ürün de açmaz (`product-import-options.ts:93-100`). | mevcut ürünün içeriği, fiyatı, stoğu |
| **Yeni Ürünleri Aktar** | Panelde olmayan ürünleri tümüyle açar (varyant, seçenek, özellik, kategori, marka, görsel, fiyat, stok, bağ). Mevcut ürüne **yalnız yeni bedenleri** ekler. | mevcut ürünün adı, açıklaması, markası, kategorisi, görselleri |
| **Mevcut Ürünleri Güncelle** | Yalnız panelde eşleşen ürünler; asla yeni ürün açmaz ("Panelde eşleşen ürün yok; yalnız mevcut ürünler güncelleniyordu."). Alt kutulara göre yazar. Seçenek yalnız seçili bağlantılardan biri fiyat/stok yazan bir pazaryeriyse (Trendyol, Shopify) sunulur; yalnız Hepsiburada, N11, Pazarama ya da PttAVM seçiliyse gizlenir, çünkü bunlarda yazılacak bir şey yoktur (`product-import-options.ts:32-42,67`). | kutusu işaretlenmemiş her şey |
| **Ürün Eşleme** | Yalnız pazaryeri **bağlarını** kurar; tam okur. N11/Pazarama/PttAVM'de teklifi olmayan varyantlara uzak fiyattan teklif açar. Arşiv ürünleri, her kipte olduğu gibi, **Arşiv Ürünlerini Dahil Et** işaretliyse okunur (sahip kararı, 12 Kas). | ürün, marka, kategori, içerik, görsel, fiyat, stok |

**Ürün Eşleme nasıl eşler** (`MarketplaceJobProcessor.cs:7765-7855`): önce barkod, sonra stok kodu, ikisi bulamazsa model
kodu + renk + beden. Tek varyanta inmeyen model kodu asla seçilmez. N11/Pazarama'da çapraz bakılır (uzak stok kodu ↔ panel
barkodu); Shopify barkod önceliklidir; PttAVM'nin kendi eşleyicisi vardır. Sonuç: Eşlenen · Zaten eşli · Panelde karşılığı
yok · Çakışma.

**Gelişmiş Seçenekler** (`product-import-options.ts:30-37`):

| Etiket | Varsayılan | Etki |
|---|---|---|
| **Mevcut Ürün Bilgilerini Güncelle** | kapalı | Açık: ad, açıklama, kategori, özellikler, seçenekler, varyant kodları ve görseller pazaryerindekiyle yazılır. **Gönderilmemiş panel değişiklikleri de ezilir.** Yeni beden/renkler doğru renk kartına eklenir; kullanıcının görsel atamaları ve sıralaması korunur. Satış durumu hiç yazılmaz. |
| **Fiyatları Güncelle** | kapalı | panel satış ve liste fiyatı, fiyat geçmişi |
| **Stokları Güncelle** | kapalı | panel stoğu (`MARKETPLACE_STOCK_IMPORT`). Onay metni: "Değişen stoklar, otomatik gönderimi açık olan diğer pazaryerlerine de gönderilir." |
| **Arşiv Ürünlerini Dahil Et** (Shopify'da "Arşiv ve Taslak …") | **açık** | kapalıyken arşiv varyantları atılır |
| **Onay Bekleyen Ürünleri Dahil Et** | kapalı | Trendyol onay sürecindeki ürünler; N11/Pazarama/PttAVM'de her zaman dahil |

- Yalnız Fiyat ve/veya Stok işaretli, içerik kutusu kapalıysa **yalnız fiyat/stok** yazılır; kategori, özellik, görsel,
  içerik, desi, marka, bağ yazılmaz (`MarketplaceJobProcessor.cs:9016-9059`).
- Fiyat ve stok gerçekte yalnız **Trendyol ve Shopify** için uygulanır; sunucu HB, N11, Pazarama, PttAVM'de bu seçenekleri
  siler (`MarketplaceSalesService.cs:1537-1539`). Pencere bunu artık baştan söyler: yalnız bu platformlar seçiliyse ilgili
  kutuları hiç göstermez, karışık seçimde "Yalnız Trendyol ve Shopify'da uygulanır." notunu yazar; gözlem kipindeki platform
  için "panelde var olan ürünün bilgisi, fiyatı ve stoğu yazılmaz" notu çıkar (`product-import-options.ts:62-90`).
- Trendyol'da Mevcut Ürünleri Güncelle modunda hiçbir kutu işaretli değilse **Aktarımı Başlat** pasif olur.
- Bir bağlantıda aynı anda tek aktarım çalışır (`PRODUCT_SYNC_ALREADY_RUNNING`).
- **Yerel düzenleme koruması**: panelde gönderilmemiş değişiklik varsa içerik korunur ve bağ `LOCAL_CHANGES_PENDING` olur;
  bu korumayı yalnız Mevcut Ürün Bilgilerini Güncelle aşar (`SynchronizationPolicies.cs:473-478`).

**Aktarımda kategori, özellik, seçenek, marka (yalnız Trendyol tam yazar):**

- **Kategori**: önce kullanıcının kategori eşlemesi; yoksa ada/yola göre kök panel kategorisi; yoksa yeni kök yaprak
  kategori ve doğrulanmış eşleme açılır (`MarketplaceJobProcessor.cs:7135-7178`).
- **Özellik**: aynı adlı etkin panel özelliği kullanılır, yoksa açılır; değerler Trendyol listesinden yazılır. "Web Color"
  değerleri panel değeri olarak eklenmez.
- **Seçenek**: Renk/Beden panel seçeneği ve değerleri açılır ya da yeniden kullanılır.
- **Marka** (sahip kararı 12 Kas 2026; 28 Ekim'deki "yalnız kullanıcı eşlemesini oku" kuralının yerine; canlı 12 Kas
  19:10Z, `ac24f253c`): aktarım ilanın markasını **kategori gibi kendisi kurar**, pazaryerine ayrıca soru sormadan, yalnız
  ilanın taşıdığından (`ImportedBrandRule.cs:5-10`; `MarketplaceJobProcessor.cs:9125-9212`):
  1. bu bağlantıda ilanın marka numarasına bir eşleme varsa o panel markası (doğrulanmamışsa doğrulanır);
  2. yoksa **aynı adlı panel markası** alınır ve eşlenir;
  3. o da yoksa ilanın adıyla **yeni panel markası açılır** ve eşlenir (doğrulanmış, kaynağı "aktarım").
  - Eşleme yalnız marka numarası taşıyan ilanda yazılır (Trendyol). Hepsiburada, N11, Pazarama, PttAVM ve Shopify ilanları
    yalnız marka adı taşır (PttAVM hiç taşımaz): aktarımın açtığı üründe marka panelde ada göre bulunur ya da açılır,
    **eşleme yazılmaz**.
  - Kapatılmış panel markası yeniden açılmaz; bir pazaryeri markası iki panel markasına eşliyse ya da aynı adlı panel
    markası başka bir markaya eşliyse ürün markasız kalır ve marka **Eşlenmemiş {Platform} Markaları** listesine düşer
    ("…aktarımda eşlenemedi: … Eşleştirme Ayarları › Marka Eşleme'de kontrol edin.", `ImportedBrandRule.cs:19-24`).
  - Mevcut ürün markasını yalnız aktarım ürünü yazıyorsa değiştirir; yerel değişikliği korunan, **Mevcut Ürün
    Bilgilerini Güncelle** kapalı, yalnız beden ekleyen ya da yalnız gözlem yapan çalıştırma markaya dokunmaz. Yalnız
    fiyat/stok ve **Ürün Eşleme** markaya hiç dokunmaz.
- Shopify, HB, N11, Pazarama, PttAVM **yalnız gözlem** modundadır: mevcut ürünün içeriğine, markasına ve kategorisine
  dokunmaz, yalnız eşleşen panel ürününe bağlanır (`MarketplaceJobProcessor.cs:6776,8284-8289,8314-8411`).
- İş türleri: `PRODUCT_SYNC`, `SHOPIFY_PRODUCT_SYNC`, `HEPSIBURADA_PRODUCT_SYNC`; her seçili bağlantıya ayrı iş. İlerleme
  kartı **Devam Eden Aktarımlar** (Alınan / İşlenen / Atlanan / Hatalı; **Durdur**). Bir ürün grubu hata verirse
  `PRODUCT_IMPORT_FAILED` yazılır ve devam edilir; hepsi başarısızsa iş incelemeye düşer.
- (canlı) `ops.operational_issues` içinde 22 Ekim'den kalma 261 `PRODUCT_IMPORT_FAILED` kaydı var.

**Klasik içe aktarma** (`/imports`, CSV/XLSX; `ImportPages.tsx`) bu akış değildir ama marka kuralı aynıdır: dosyadaki marka
adı panel markasına `PanelBrandByName` ile (Platformdan Ürün Aktar'ın ada göre adımıyla ve Excel'in Marka sütunuyla aynı kod)
çözülür ve gerekirse açılır; kapalı marka açılmaz, uyarı "markasız ve taslak olarak oluşturulur" der
(`ImportJobProcessor.cs`; `PanelBrandByName.cs:7-18`). Zorunlu alanı eksik ürünün durumu için 3.2.

### 3.12 Excel ile içe / dışa aktarma

**İçe / Dışa Aktar** → "Ürünleri İçe / Dışa Aktar" (`ProductTransferDialog.tsx:175`).

**Dışa aktarma** (**Excel'e Aktar**): kapsam **Tüm Varyantlar**, **Maliyeti Eksik Olanlar**, **Desisi Eksik Olanlar**.
1 satır = 1 varyant; arşivlenmemiş ürün ve varyantlar. Kolonlar: Ürün Adı, Varyant, Stok Kodu, Barkod, Model Kodu, Marka,
Kategori, Durum, Panel Satış Fiyatı, Panel Liste Fiyatı, KDV Oranı, Stok (ana depo), Maliyet (₺), Desi; her mağaza için
"<Mağaza> Satış Fiyatı" ve "<Mağaza> Liste Fiyatı" (`ProductTransferService.cs:82-103`). İkinci sayfa "Açıklama".

**İçe aktarma** (.xlsx ya da .csv, en çok 10 MiB, 20.000 satır):

- **Panele Aktarılacak Alanlar** (kolon profilleri); varsayılan yalnız **Maliyet + Desi**. Gruplar: Ürün Bilgileri (Ürün Adı,
  **Marka**, Barkod, Model Kodu), Fiyat ve Vergi (Panel Fiyatı, KDV Oranı), Stok ve Lojistik (Stok — ana depoya `MANUAL_ADJUSTMENT`
  "Excel"; Desi), Maliyet, Mağaza Fiyatları.
- **Eşleştirme anahtarı**: önce Stok Kodu, yoksa Barkod. Barkod alanı seçiliyse satır **yalnız** stok koduyla eşleşir. Stok
  kodu asla yazılmaz. Kolon sırası önemsizdir.
- **Boş hücre = değişiklik yok.** "-", "–", "—" ya da "sil" yalnız Maliyet, Desi ve Model Kodu'nu siler (taslak olmayan kartta Desi ve Model Kodu silinemez, 3.2). **Maliyet 0 =
  mevcut maliyeti siler** (sahip kararı). Desi 0 olamaz ve en çok 9.999'dur.
- **Marka** sütunu (başlık "MARKA", Ravencia'nın indirdiği dosyada da var): marka modelindir, yani aynı model kodlu bütün renk
  kartlarına yazılır ve silinemez. Ad `PanelBrandByName` ile bulunur ya da açılır (3.9); aynı modelin satırlarında farklı marka
  varsa satır hatalı sayılır; paneldeki kapalı marka "Eşleştirmeler › Marka Eşleme'den açın" hatası verir. Marka değişince
  ürünün pazaryeri bağları "yerel değişiklik" işaretlenir, pazaryerine bir şey gitmez (`ProductTransferService.cs`;
  `ProductTransferFileParser.cs`).
- **Yeni ürün açmaz**: eşleşmeyen satır "Eşleşmedi" olur. Eşleşmeyen, belirsiz ve hatalı satırlar kaydedilmez; diğerleri
  **Değişiklikleri Kaydet (n)** ile hepsi ya da hiçbiri yazılır. Önizlemeden sonra kayıt değiştiyse "…dosyayı yeniden
  yükleyin." Önizleme ilk 200 satırı gösterir; Kaydet hepsini yazar.
- Ad, barkod ya da model kodu değişirse ürünün pazaryeri bağları "yerel değişiklik" işaretlenir; pazaryerine bir şey gitmez.
- **Mağaza Fiyatları** değişen etkin teklifler için fiyat gönderim işi açar — ama yalnız bağlantının otomatik fiyat **ve**
  stok yazması açıksa; bugün hepsi kapalı olduğu için fiilen gitmez (`ProductTransferService.cs:446`).
- Kolon profilleri bir **işleyiş ayarıdır**, hiçbir sıfırlamada silinmez (10).

### 3.13 Yayınlama ve güncelleme

**Yayınla** (2. adım): mağaza kartları seçilir; düğme **Yayınla** (yeni kartta **Ürünü Oluştur**). Sıra: ürünü kaydeder →
seçili mağazalara kanal fiyatı yazar → her renk kartı için yayın işi açar; zaten yayında ya da yayını sürmekte olan kart
atlanır (pazaryerinin kabul edip onayını beklediği "Onay Bekliyor" kartı da sürüyor sayılır, yeniden gönderilmez;
  `publication-status.ts`) (`product-publication-submit.ts:219-270`; `CatalogService.cs:2034-2163`).

- Hazırlık kontrolleri: Temel Ürün Verileri, Görsel Kalitesi, Varyant Bilgileri, Kategori ve Seçenekler, Varyant Görselleri,
  Web Color (Trendyol). Zorunlu eksiği olan kart seçilemez (**Zorunlu Eksik**); "Kategori Eşle" ve "Şimdi eşle" (**Eksik
  Değer Eşlemeleri**) buradan açılır. Başlık uzunluğu, barkod biçimi, KDV gibi kurallar yalnız sunucuda denetlenir ve
  gönderimden sonra hata olarak döner.
- Yayın kartı adımları: **Hazırlık → Gönderim → Onay → Yayında**. Pazaryeri onayı 7 gün yoklanır: ilk 1 saat 15 dk, ilk 6 saat
  30 dk, ilk 48 saat 2 saat, sonra 6 saatte bir. Süre dolarsa `PRODUCT_APPROVAL_DEADLINE_EXPIRED` ile elle kontrole düşer
  ve zile bildirim gelir (`ProductApprovalReconciliationPolicy.cs:9-55`).
- Toplu: Ürünler › **Platformlara Yayınla**; tek seferde en çok 1000 kart; eksik pazaryeri fiyatı genel fiyattan doldurulur.

**Hızlı Güncelle ve Platformlarda Güncelle:**

- **Hızlı Güncelle**: pencere açmadan, ayar okundaki **Platformlarda Güncelle** penceresinde en son **Kaydet**lenen alan ve
  mağaza seçimini gönderir. Ayar yoksa yalnız **Fiyat + Stok** gider. **Seçim hesap başına sunucuda saklanır**
  (`GET/PUT /settings/platform-update`; doğrulanır, İşletme de kaydedebilir); her cihaz aynı seçimi gönderir. Tarayıcıda
  kalmış eski yerel seçim bir kez sunucuya taşınır (`use-platform-update-choice.ts`; `PlatformUpdateChoiceSchema.cs`).
- Pencere alanları: Ürün Bilgileri (Ürün Adı, Açıklama, Görseller, Kategori Özellikleri, Garanti Süresi), Kargo ve Teslimat
  (Desi, Teslimat Süresi), Satış Bilgileri (Fiyat, Stok, KDV Oranı). **Kaydet** seçimi saklar, **Gönder** saklar ve gönderir.
  Gönder'e basmadan önce pencere kapalı **Dış Yazma Ana Anahtarı**'nı ya da PttAVM'nin kapalı **Manuel Dış Yazmalar**
  iznini uyarı olarak gösterir (Kaydet yine seçimi saklar); elle başlatılan fiyat/stok gönderiminin otomatik yazma kapalıyken
  de gittiğini pencere, Hızlı Güncelle ipucu ve yayın adımı söyler. Shopify'da seçilen alanlar gönderimi daraltmaz, kartın
  tamamı gider ve pencere bunu yazar.
- İkisi de önce formu kaydeder, sonra her renk kartı için ürün bilgisi işi ve fiyat/stok işi açar. Pazaryerinde olmayan renk
  gönderilmez: "…renk bu mağazada yok…; önce Yayınla ile gönderilmeleri gerekir."
- Toplu: Ürünler › **Platformlara Güncelle**: kaydetmez, teklif yazmaz; yalnız mağazada yayında/eşleşmiş ürünleri gönderir;
  taslak ve zorunlu eksikli ürünler tutulur.
- **Otomatik fiyat/stok kapalıyken de** bu düğmeler fiyat ve stoğu gönderir; yalnız Dış Yazma Ana Anahtarı'na bakarlar
  (`SynchronizationPolicies.cs:645-663`; `InventoryService.cs:405-468`).

| Platform | Gönderilebilen | Not |
|---|---|---|
| Trendyol | Ad, Açıklama, Görseller, Kategori Özellikleri, Desi, KDV, Teslimat; Fiyat, Stok | Garanti gitmez; onaysız üründe tüm kart gider |
| Hepsiburada | tek paket: Ad, Açıklama, Görseller, Özellikler, Garanti, Desi, KDV; Teslimat ayrı; fiyat yalnız satış | hbSku bağı yoksa `HEPSIBURADA_HB_SKU_REQUIRED` |
| Shopify | seçim ne olursa olsun tüm model kartı tek istekte | ayrı fiyat/stok işi yok |
| Pazarama | yalnız KDV + Fiyat/Stok | içerik güncellenmez |
| N11 | Açıklama, Kategori Özellikleri, KDV, Teslimat; Fiyat + Stok | ad, görsel, marka, kategori, barkod, desi API ile değişmez |
| PttAVM | Ad, Açıklama, Görseller, Garanti, Desi, Teslimat, KDV; Fiyat + Stok | özellik gitmez; **Manuel Dış Yazmalar** izni gerekir |

(`platform-update-fields.ts:80-146`; `MarketplaceContracts.cs:521-565`)

### 3.14 Ürün Hareketleri (ürün etkinlik logu)

Yer: İşlem Takibi › Loglar › **Ürün Hareketleri** (diğer sekme **Pazaryeri Gönderimleri**) (`OperationLogs.tsx:12-19`).

- Kolonlar: Zaman, Ürün, Değişiklik, Kaynak, Platform, Kullanıcı. İlk sayfa 30 sn'de bir yenilenir.
- Tür: **Stok, Fiyat, Durum, İade, Ürün Bilgisi**. Hareket Türü: Satış, Sipariş İptali / Değişikliği, Stok Yetersiz, Stok Düzenleme,
  Açılış Stoğu, Pazaryeri Stoğu, İade Stoğa Eklendi, İade Stoğa Eklenmedi, Mağaza Fiyatı, Panel Fiyatı, Satış Durumu.
  "İlk Fiyatları Göster" varsayılan kapalı (`product-activity.ts:53-58,158-215`).
- Kaynak: Sipariş, İade, Elle, Ürün Düzenleme, Toplu Düzenleme, Fiyat Düzenleme, Excel, Pazaryeri İçe Aktarımı, Toplu Yayın,
  İçe Aktarım, Hızlı Güncelle, Platformlarda Güncelle (iki son kaynak mağaza fiyatını artık "Fiyat Düzenleme" değil kendi
  adıyla yazar; 12 Kas'tan önceki satırlar ikisini ayıramaz ve Platformlarda Güncelle sayılır).
- Arama (stok kodu, barkod, model kodu, ürün adı) artık 1000 varyantta kesilmez; eşleşen bütün varyantlar taranır
  (`ProductActivityService.cs`, `search_variants`).
- Veri ayrı bir tablodan değil; stok defteri, iade stok kararları, kanal fiyat geçmişi ve durum/panel fiyatı değişiklikleri
  (4 Kas 2026'dan itibaren) birleştirilir (`ProductActivityService.cs:9-17`).
- **Zamana dayalı silme yok**; kayıtlar ürün silinince, bağlantı verisi silinince ya da panel sıfırlamasında gider.
- **Ürün Bilgisi** (beşinci tür): maliyet, desi, ürün adı, barkod, model kodu, stok kodu, KDV (oran ve KDV dahil mi birlikte),
  marka, kategori, garanti, teslimat süresi ve açıklama değişiklikleri eski → yeni metniyle görünür. Düzenleyici, Toplu Değer
  Ata, Toplu KDV Ata ve Excel içe aktarma 12 Kas 2026'dan itibaren kaydeder; Hareket Türü süzgeci alan başına bir giriş sunar
  (`ProductFieldChangeRecorder.cs`; `product-activity.ts:154-167`). Açıklama metninin ilk 120 karakteri tutulur.
- Üstteki beş tür anahtarı (Stok, Fiyat, Durum, İade, Ürün Bilgisi) özet çubuğudur (uygun olup olmadığı sahip kararı bekliyor, 9.1; `product-activity.ts:58-62`).

---

## 4. Siparişler, İadeler, Faturalar, Stok ve Ürün Soruları

> (canlı, 12 Kas 2026) Bugün 17:27Z'de `CONNECTION_FULL_RESET` başarıyla çalıştı; bütün bağlantılar `DISABLED` ya da
> `DELETED`. Sipariş, iade, fatura, stok kalemi, stok defteri ve soru tabloları **boş**. Davranışlar koddan anlatılıyor.

Sık geçen dosyalar: `MarketplaceSalesService.cs` (sipariş/iade servisi), `OrderModels.cs`, `ReturnModels.cs`,
`InvoicingBillingService.cs`, `status-labels.ts` (ekran etiketleri), `OrdersPage.tsx`, `ReturnPages.tsx`,
`invoicing/pages.tsx`.

### 4.1 Sipariş ve paket

- Panelde bir **sipariş** bir ya da birden çok **paket** taşır. Sipariş durumu paketlerin durumlarından türetilir; sekme
  sayaçları da paket bazında sayılır (`OrderModels.cs:338-358`; `MarketplaceSalesService.cs:537-541`).
- **Paket durumları**: New, Processing, OnHold, ReadyToShip, Shipped, Delivered, PartiallyCancelled, Cancelled,
  Undelivered, ReturnInTransit, Returned, ManualReview, PaymentPending (`OrderModels.cs:3-18`).
- **Birleştirme kuralı**: bir paket ManualReview ise sipariş ManualReview olur. Yoksa iş bekleyen paketlerden **en erken
  aşamadaki** kazanır (bölünmüş siparişte biri teslim edilmiş, öteki yeniyse sipariş "Yeni" görünür). Ardından
  PaymentPending, ardından hâlâ hareket eden paketlerden en ileri olanı. Hepsi iade edilmişse Returned, değilse Cancelled.
- **Geçişler**: pazaryeri ara adım atlayabilir (NEW → SHIPPED); ileri doğru geçiş her zaman kabul edilir. Cancelled ve
  Returned son durumlardır (`OrderModels.cs:126-174`).
- **Yerini başkasına bırakmış paketler** artık sayılmaz: bölünen ana paket, yeniden gönderimin asıl paketi, panelin bozduğu
  Hepsiburada paketi, Trendyol'un ödeme beklerken verdiği "0" numaralı yer tutucu paket (`OrderModels.cs:445-489`).

**Platform durum eşlemeleri:**

| Platform | Eşleme | Kaynak |
|---|---|---|
| Trendyol | `Awaiting`/`Verified` → PaymentPending (ödeme, askı değil). `UnPacked` → Cancelled (bölünen ana paket). `AtCollectionPoint` → Shipped. `UnDeliveredAndReturned` → Returned. | `OrderModels.cs:404-423` |
| Genel tablo (Trendyol, N11, Pazarama, PttAVM) | `Created` → New; `Picking` → Processing; `Invoiced`/`Packaged`/`ReadyToShip` → ReadyToShip; `Shipped`/`InTransit` → Shipped; `Cancelled`/`UnSupplied` → Cancelled; `CancelRequested` → OnHold. **Tanınmayan her değer → ManualReview.** | `OrderModels.cs:313-330` |
| Pazarama | Ürün durum kodu önce ara isme çevrilir: 3 Created, 12 Picking, 18 CancelRequested (askıya düşer), 6/13 Cancelled, 5/16/19 Shipped, 11/7/9 Delivered, 14 Undelivered, 8/10 Returned. Başka kod → ManualReview. | `PazaramaOrderMapper.cs:163-175` |
| PttAVM | `kargo_yapilmasi_bekleniyor` Created; `havale_onayi_bekleniyor`/`onay_surecinde` Awaiting; `gonderilmis` Shipped; `gondericisine_teslim_edildi` Returned; `tamamlandi` Delivered; `iptal`/`odeme_gecersiz` Cancelled; `iade` Returned. | `PttAvmOrderMapper.cs:127-142` |
| Hepsiburada | `Open`/`Unpacked` New; `Packaged`/`Invoiced` ReadyToShip; `Undelivered` "Teslim Edilemedi" (5 Kas'tan beri; önceden "Askıda"). Paketsiz siparişte durum okunamazsa **ManualReview**, asla "Yeni" değil. Paket düzeyinde `Unpacked` = bozulan paket = Cancelled. | `OrderModels.cs:248-279,392-402` |
| Shopify | `UNFULFILLED` New; `FULFILLMENT_FAILED`/`UNKNOWN` ManualReview. Shopify'da sipariş durumu panelden elle de değiştirilebilir. | `OrderModels.cs:381-390` |

**Hepsiburada'ya özel:** bir aydan eski, paketi olmayan ve durumu doğrulanmamış Hepsiburada siparişleri listede ve
sayaçlarda gizlenir; ekranda "Paket bilgisi doğrulanmadı" yazar (`OrderModels.cs:185-207`; `OrdersPage.tsx:536`).

### 4.2 Siparişler sayfası

**Sekmeler** (`order-status-tabs.ts:1-18`):

| Sekme | Neyi listeler |
|---|---|
| Tümü | Her sipariş |
| Yeni | New paketi olan siparişler ve paketsiz yeni Hepsiburada siparişleri |
| İşleme Alınanlar | Processing ve ReadyToShip paketler |
| Kargoda | Shipped paketler (teslim edilemeyen paket yalnız **Teslim Edilemedi**'de; `MarketplaceSalesService.cs:548`) |
| Teslim Edildi | Delivered |
| İptal | Tamamen iptal edilenler, iptal edilmiş bölünmüş paketler (Shopify hariç) ve iptal adedi olan satırlar |
| Yeniden Gönderimler | Trendyol'un "yeniden gönderim" işaretli paketleri; filtre "Kargoya Verilmeyenler" |
| Askıda | OnHold paketler ve askıdaki paketsiz Hepsiburada siparişleri |
| Kısmi İptal | Tamamen iptal edilmemiş ama satırında iptal adedi olan her sipariş; teslim edilmişler dahil (1 Kas kararı) |
| Teslim Edilemedi | Undelivered |
| İnceleme Gerekli | ManualReview paketler ve durumu okunamayan paketsiz siparişler |
| **Bekleyen İşler** (sekme satırında görünmez) | Depo kuyruğu: NEW, PROCESSING, READY_TO_SHIP, PARTIALLY_CANCELLED, MANUAL_REVIEW; yalnız etkin (`ACTIVE`/`VERIFIED`/`CONNECTED`) bağlantılarınkiler. Menü rozeti ve Genel Bakış kartı açar (`DashboardReadService.cs:48-54`). |
| **Geciken** | Bekleyen İşler içinden kargoya verme süresi (`ShipmentDueAt`) **geçmiş** olanlar (`DashboardReadService.cs:70-73`) |

**İade Edildi, İadede, Ödeme Bekleniyor ve Doğrulanmadı sekmeleri gelmeyecek** (sahip kararı, 13 Kas 2026): ödeme bekleyen
(Trendyol) ve iadedeki siparişler ve sayıları **Tümü**'nde kalır; sunucu `RETURNED`, `RETURN_IN_TRANSIT`, `PAYMENT_PENDING`,
`UNVERIFIED` sayaçlarını yine sayar ama sekme satırı bunları listelemez (`order-status-tabs.ts:3-19`;
`MarketplaceSalesService.cs:237`). Satırdaki kırmızı süre uyarısı süreye **24 saatten az kalmış** (ya da geçmiş) siparişi gösterir;
üzerine gelince "Süre Doluyor" yazar. "Geciken" yalnız süresi **geçmiş** siparişe denir (`order-delivery.ts:44-48`).
Bir sipariş durumu sekmede, satırdaki halkada, dışa aktarmada ve Shopify durum penceresinde **aynı adla** yazılır: Askıda,
İnceleme Gerekli, Ödeme Bekleniyor, Doğrulanmadı (`order-status-tabs.ts:38-50`).

**Özet kartları** (hepsi "N Paket" sayar; `OrderMetricCards.tsx:1-17,188-214`):

- **Yeni Sipariş** "N Paket | M Adet Ürün" (adet = sipariş − iptal); üzerine gelince mağazalar ve kargo firmaları.
- **İşleme Alınan**; **Geciken** (sayı varsa kırmızı ve yanıp söner; bu sayfada tıklanmaz).
- **Mikro İhracat Sipariş**: İstanbul günüyle bugün gelen ve kargoya verilmemiş mikro ihracat siparişleri.
- **İptal**: bugün tamamen ya da kısmen iptal edilenler; kısmi varsa ek rozet "N Kısmi İptal | M Adet Ürün".
- **Yeniden Gönderim**: panelin bulduğu ve kargoya verilmemiş yeniden gönderimler + sahibin elle girdiği **hatırlatıcı
  sayı**. Hatırlatıcı köşedeki dişliden girilir ("Yeniden Gönderim Hatırlatıcısı", alan "Bekleyen Yeniden Gönderim",
  **Kaydet** / **Sıfırla**, 0–100.000). Sunucuda hesap başına saklanır. Gerekçe: Trendyol yeniden gönderim paketini her
  zaman döndürmüyor (`resend-reminder.ts:1-16`).

**Filtreler** (`OrdersPage.tsx:982-990`): Kargo; Fatura durumu (Fatura Bekliyor, Fatura İletiliyor, Fatura İletilemedi,
İnceleme Gerekli, Faturası Mevcut, Fatura Oluşturulamadı, Fatura Sonucu Belirsiz, Fatura İptal); Fatura Türü (Bireysel,
Kurumsal, Kurumsal · Vergi No Yok); Bölge (Türkiye, Mikro İhracat); Kısmi İptali Olanlar; Etiketi Yazdırılanlar /
Yazdırılmayanlar. Arama: sipariş numarası, ürün adı, stok kodu, barkod. **Rakamlardan oluşan arama müşteri kaydında
aranmaz** (kimlik ve telefon numarası güvenliği; `MarketplaceSalesService.cs:478-496`).

**Alıcı tipi** ("Bireysel", "Kurumsal", "Kurumsal · Vergi No Yok"): faturanın kendi kuralıyla hesaplanır ve siparişe
kaydedilir; filtre ve etiket aynı değeri okur (`OrderBuyerTypes.cs:9-15`). Pazaryerinin açıkça bildirdiği tür önce gelir
(Trendyol `commercial`; N11/Pazarama `invoiceType`; PttAVM `FaturaTip`; Hepsiburada bireysel alıcıya `11111111111`).
Vergi numarası olmayan ya da sağlama hanesi tutmayan kurumsal alıcı "Kurumsal · Vergi No Yok" olur; alıcı bilgisi
okunamazsa bireysel sayılır (`TrendyolEFaturamCanonicalPayload.cs:337-381`).

**Satır işlemleri** (`OrdersPage.tsx:542-580`):

- **İşleme Al**: yalnız NEW siparişte ve yalnız Trendyol, N11, Pazarama, Hepsiburada'da görünür; PttAVM ve Shopify'da
  düğme yoktur (`order-status-tabs.ts:52-59`; `OrdersPage.tsx:546,579`). Trendyol, N11 ve Pazarama'da pazaryerine "Picking"
  gönderir (`MarketplaceSalesService.cs:1667-1705`). Ödeme bekleyen paket işleme alınamaz ("Trendyol ödeme onayı vermeden sipariş
  işleme alınamaz.", `MarketplaceEndpoints.cs:326-327`); paket bulunamazsa sipariş önce anında yenilenir
  (`MarketplaceEndpoints.cs:328-358`). Şart: dış yazma ana anahtarı ve **Kargo ve Etiket İşlemleri** izni açık.
  Hepsiburada'da aynı düğme **Paket Oluştur**'u çalıştırır. Sunucu PttAVM'de yine de reddeder
  (`SHIPMENT_ACTION_UNSUPPORTED`, `MarketplaceSalesService.cs:1674`).
- **Paket Oluştur**: yalnız Hepsiburada, sipariş henüz kargoya verilmemişse.
- **Başka Kargo Firması İle Gönder**: yalnız Trendyol, paket kargoya verilmeden önce; ödeme bekleyen, iptal ya da iade
  sürecindeki siparişte çıkmaz (`OrdersPage.tsx:299-303`).
- **A4 Etiketi Yazdır / Sticker Etiketi Yazdır**: yalnız NEW, PROCESSING, READY_TO_SHIP ve paket varsa. Mikro ihracat
  siparişinde fatura kesilmeden etiket yazdırılamaz. İlk yazdırma zamanı sunucuya kaydedilir ve her bilgisayarda
  "Yazdırıldı" görünür (`MarketplaceEndpoints.cs:104`). Etiket düzeni: Sistem Ayarları › Kargo Ayarları (7.3).
- **Kargo Takip**: SHIPPED ya da UNDELIVERED siparişte, takip numarası varsa.
- **İptal Et** her zaman pasif: "Sipariş iptali pazar yerinin panelinden yapılır."
- **Fatura menüsü**: fatura bekleniyorsa "Fatura Oluştur", "Fatura Bilgileri", "Fatura Yükle" (Shopify'da ayrıca "Fatura
  Durumunu Değiştir"); fatura varsa "Faturayı Gör", "Fatura Bilgileri" ve "Fatura İptal Et". **Fatura İptal Et** panelde kesilmiş faturanın
  detayını (`/invoices/{id}`) açar; e-Arşiv iptali orada istenir (4.4). Panelde kesilmemiş faturada (`invoiceId` yok) pasiftir
  ve ipucu bunu söyler (`OrdersPage.tsx:574`).
- Boş alan metinleri: Paket No "Bekleniyor" (HB'de "Henüz paketlenmedi"); Takip No "Takip no bekleniyor".

### 4.3 İadeler

**Durumlar** (`ReturnModels.cs:3-48`; etiketler `status-labels.ts`):

| İç durum | Gelen ham durumlar | Ekranda |
|---|---|---|
| Requested | NEWREQUEST, takip linki olmayan CREATED | Talep Oluşturuldu |
| InTransit | takip linki olan CREATED | Taşımada |
| ActionRequired | WAITINGINACTION, AWAITINGACTION, AWAITINGPREAPPROVAL, INANALYSIS, WAITINGFRAUDCHECK, Shopify REQUESTED | Aksiyon Bekliyor |
| Approved | ACCEPTED/APPROVED, Shopify OPEN | Onaylandı |
| Rejected | REJECTED, Shopify DECLINED | Reddedildi |
| Disputed | UNRESOLVED, INDISPUTE | İhtilaflı |
| Completed | COMPLETED/REFUNDED, Shopify CLOSED | Tamamlandı |
| AwaitingShipment | müşteri iadeyi henüz göndermemiş | Kargo Bekliyor (`status-labels.ts:95`) |
| Unknown | tanınmayan her ham durum; Onayla/Reddet çıkmaz, sorun kaydı açılır | Bilinmiyor |
| (görünüm) | Talebin satırlarından bir kısmı onaylanmış, bir kısmı reddedilmiş | **Kısmen Onaylandı** |

"Kısmen Onaylandı" **iade talebinin** durumudur, faturanın değil. N11 ve Pazarama her ürün için ayrı talep açtığı için
kısmi onay sipariş düzeyinde de hesaplanır (`ReturnDecisionScope.cs:14-31`).

**İadeler sayfası** (`ReturnPages.tsx`):

- Sekmeler: Tüm İadeler, Talep Oluşturulan, Kargoya Verilen, Aksiyon Bekleyen, Onaylanan, Reddedilen, Analiz, İhtilaflı,
  Askıda İadeler (`ReturnPages.tsx:387`). Onaylanan = Approved, Completed, Kısmen Onaylandı; Reddedilen = Rejected **ve
  iptal**; tanınmayan her durum "Analiz"e düşer.
- Kartlar: Aksiyon Bekleyen, Kargolanmayı Bekleyen Red, Bugün Onaylanan, Bugün Reddedilen ("bugün" İstanbul 00:00'dan).
- Genel Bakış'taki "Bekleyen İadeler" yalnız **Aksiyon Bekliyor** talepleri sayar.
- **Listeye girmeyenler**: pazaryerinin iptal ettiği talepler, etkin olmayan bağlantıların talepleri, Hepsiburada'nın iade
  geçmişi penceresinden eski Hepsiburada talepleri (`OrderQueueQueries.cs:25-40`).
- **İade Senkronizasyonu** düğmesi Trendyol, Hepsiburada, N11, Pazarama, PttAVM ve Shopify bağlantılarını kapsar
  (`return-sync.ts:4-8`). Zamanlanmış iade okuması canlıda 10 dakikada birdir; ayrıca kısa/orta/günlük
  uzlaştırmalar var (2.3).

**Karar: onay ve ret**

- **Kapsam**: Trendyol'da satır bazında; Hepsiburada ve Shopify'da talep bütün olarak (satır seçmek reddedilir)
  (`ReturnDecisionScope.cs:5-12`).
- Düğmeler: **Onayla** ve **Reddet**; Hepsiburada ön onay aşamasında ayrıca **İnceleme İçin Geri Gönder**. Gönder düğmesi
  "İadeyi Onayla", "Ret Kararını Gönder" ya da "İnceleme Talebi Oluştur" yazar; önce özet penceresi açılır. Uyarı:
  "Gönderilen ret geri alınamaz".
- **Ret formu**: ret nedeni pazaryerinin kendi listesinden; açıklama zorunlu, en çok 500 karakter.
- **Kanıt** ("Kanıt (PDF/JPEG/PNG, En Fazla 10 MiB)"): Hepsiburada ve N11'de istenmez; diğerlerinde ret nedeni kanıt
  istiyorsa en az bir dosya şart. Neden listesi okunamazsa yalnız Trendyol'un 1651, 451, 2101 nedenleri kanıtsız geçer.
  Pazarama: en çok 3 fotoğraf; 10 ve 11 dışındaki nedenler fotoğraf ister.
- **N11 reddi**: 41 numaralı neden ("İade Ürün Mağazaya Ulaşmadı") dışında "Geri Gönderim Kargo Firması" ve "Geri Gönderim
  Takip Numarası" zorunlu.
- **Gönderimi engelleyenler**: dış yazma ana anahtarı kapalı (`EXTERNAL_WRITES_DISABLED`); **İade Kararları** izni kapalı;
  Hepsiburada'da iade yazma yeteneği doğrulanmamış; Hepsiburada'nın "MissingInvoice" nedenli talebi; aynı satırlar için
  önceki karar sonuçlanmamış (`RETURN_DECISION_IN_PROGRESS`) (`MarketplaceSalesService.cs:2585-2597`).
- **Toplu karar** ("Toplu İade Onayı" / "Toplu İade Reddi"): yalnız Aksiyon Bekleyen sekmesinden; **en çok 50 iade** seçilebilir (fazlasında düğmeler kapanır ve kaç iadenin seçimi
  kaldırılacağı yazılır; `return-bulk-approve.ts`); Hepsiburada ön onay talepleri girmez; toplu retde tek pazaryeri seçilmeli; kanıt isteyen nedenler listelenmez (kalmazsa "Bu iadeleri tek tek
  reddedin"); her talep ayrı istek.

**Stoğa geri alma** — iki ayrı yol:

1. **Onay penceresi**: ürün başına **Stoğa Ekle** ve "N Adet Stoğa Ekle". Ekleme **bekletilir**: pazaryeri onayı
   kesinleşince uygulanır, talep reddedilir ya da iptal olursa düşer; bu sırada rozet "Onay Kesinleşince Stoğa Eklenecek"
   (`ReturnPendingStock.cs:100-118`). Ürün yerel stok kaydıyla eşleşmiyorsa uyarı çıkar.
2. **İade detayında stok kabulü** ("İade Paketi Ürünleri ve Stok Kabulü"): **Satılabilir — Stoğa Ekle** eldeki stoğu
   artırır (`RETURN_PASS`); **Karantina — Stoğa Ekleme**, **Hasarlı — Stoğa Ekleme**, **Teslim Alınmadı** yalnız kayıt
   tutar. Detaydaki "Stok otomatik artmaz…" uyarısı bu yol içindir.

Koşullar: stok yalnız onaylanmış satıra eklenir; toplam iade adedini aşamaz; konum her zaman ana depo (`MAIN`), kayıt yoksa
`INVENTORY_MAPPING_REQUIRED` (`MarketplaceSalesService.cs:2853-2866`). Stoğa eklenen adet pazaryerine yalnız otomatik stok
yazması açıksa gider; bugün kapalı.

**Kısmi iade, değişim, Hepsiburada para iadesi**

- **Değişim iade sayılmaz**; açık talep onaylanana kadar da iade sayılmaz. Değişim talebi yalnız para iadesiyle
  (`REFUNDED`) kapanırsa iade sayılır (`OrderLineReturnPolicy.cs:3-11`).
- **Hepsiburada'da "Para İadesi / Değişim" seçimi zorunludur**; müşterinin isteği talepten okunup ön seçilir ("Müşteri
  İsteği: …"); bilinmiyorsa "Müşteri isteği bilinmiyor; göndermeden önce seçin." (`ReturnFinalizedWithChoice.tsx:16`).
- **Değişimle kapanan Hepsiburada talebi**: daha önce iletilmiş **aynı fatura linki** Hepsiburada'ya kendiliğinden yeniden
  gönderilir; yeni fatura kesilmez, E-Faturam'a gidilmez. Paket kesin bilinmiyorsa ya da fatura iletim anahtarı kapalıysa
  hiçbir şey gönderilmez, sahibe not bırakılır (`HepsiburadaExchangeInvoiceResend.cs:10-16`).
- **Pazaryeri talebi iptal ederse**: karar, kanıt ya da stok hareketi yoksa kayıt silinir; varsa "iptal" olarak saklanır ve
  `RETURN_CLAIM_CANCELLED_WITH_HISTORY` sorunu açılır ("…Stoğa eklenen adetleri kontrol edin."); kapatılan sorun yeniden
  açılmaz (`CancelledReturnClaimCleanup.cs:8-40`).
- **İade etiketi**: yalnız reddedilmiş talepte ve müşteriye geri gönderim paketi yoksa (`return-label-visibility.ts:1-3`).
- Pazaryerine özel: N11 her talep tek ürün, n11depom iadeleri de gelir; Pazarama'da "incelemeye gönder" yok; **PttAVM'de
  iade servisi yok**, panelden karar gönderilemez.

### 4.4 Faturalar

**Temel kural: panel kendiliğinden fatura kesmez.**

- Fatura politikası `AutoSubmit = false`, tetikleyici "elle onaylandı" yazılır (`InvoicingBillingService.cs:2176`). (canlı:
  `billing.invoice_policies` 2 satır, ikisinde de `AutoSubmit=f`.)
- Kesim yalnız önizlemedeki **Onayla ve Faturayı Kes** (çoklu: "Onayla ve Faturayı Kes (n)") ile yapılır
  (`InvoiceWorkspacePreviewModal.tsx:157`); yalnız Sahip ve Yönetici. Canlı sağlayıcıda korumalı işler açık onay ister;
  yoksa "Dış mali işlem için açık onay zorunludur." (`InvoicingEndpoints.cs:664-684`).
- Yarım saatlik **Fatura Süresi Taraması** (`INVOICE_DUE_SCAN`) fatura **kesmez**: vadesi geçen faturaya uyarı açar,
  düzeltme gerekenleri işaretler, kesilmiş faturaların iletim tekrarlarını kuyruğa alır
  (`InvoicingJobProcessor.cs:2204-2223`).
- **Kendiliğinden olanlar** (yeni mali belge üretmez):
  1. **Kesilmiş faturanın linkinin pazaryerine iletimi** (2 Kas kararı; `InvoiceMarketplaceAutoDeliveryPolicy.cs:5-58`).
     Şartlar: link hazır, genel ve fatura yazma anahtarları açık, bağlantı aktif ve canlı ortamda, bağlantıda dış yazma açık.
     Hiç kuyruğa alınmamış iletim yalnız kesimden sonraki 14 gün içinde kendiliğinden kurtarılır.
  2. **İletimin otomatik yeniden denenmesi**: 15 dk, 1 sa, 6 sa, 24 sa; en çok 5 deneme; 14 günlük pencere. Pazaryerinin
     **reddettiği** iletim otomatik denenmez (`InvoiceDeliveryAutoRetryPolicy.cs:15-44`).
  3. Hepsiburada değişiminde aynı linkin yeniden iletimi (4.3).
- Bu yüzden iki kelime ayrı kullanılır: **fatura kesimi** (E-Faturam'da mali belge; hep sahip onayıyla) ve **fatura
  iletimi** (kesilmiş faturanın linkini pazaryerine bildirmek; otomatik).

**Faturalar sayfası** (`invoicing/pages.tsx`):

- Sekmeler: **Tümü** (gizlenmemiş her şey; tamamen iade edilmiş sipariş yalnız burada), **Faturalandırılmamışlar** (fatura
  oluşturulabilir olanlar ve reddedilen, belirsiz, kontrol bekleyen, pazaryerine aktarılamayan ya da inceleme gereken
  faturalar; pazaryerinde fatura görülmüş ya da elle "Faturası Yüklendi" işaretlenmiş olanlar çıkmaz), **Faturalandırılmışlar**,
  **Süresi Yaklaşanlar** (fatura bekleyen ve teslimin üzerinden **4 gün** geçmiş paketler; son kesim tarihi teslim + **7
  gün**), **Gizlenenler** (`InvoicingBillingService.cs:662-714`; `DashboardModels.cs:5-6`).
- Toplu işlemler: "Toplu Fatura Oluştur (n)", "Fatura Durumunu Güncelle (n)", "Siparişleri Gizle" / "Görünür Yap".
- **Fatura rozetleri** (`status-labels.ts:116-153`): Fatura Bekliyor · Faturası Mevcut · Fatura İletiliyor · Fatura
  İletilemedi · Fatura Sonucu Belirsiz (kesim sonucu bilinmiyor) · E-Faturam Cevap Bekleniyor · İncelenecek / İnceleme
  Gerekli · Fatura Düzeltme Gerekli · İade Edildi, Fatura Kesilmez · Fatura Oluşturulamadı · Fatura İptal. Mikro ihracatta
  "Fatura" yerine "İhracat Faturası" yazılır.
- **Fatura durumları** (iç): Draft, Validating, ValidationFailed, Ready, Submitting, UnknownResult, Submitted, Accepted,
  Rejected, MarketplacePending, MarketplaceFailed, Completed, CancellationPending, Cancelled, CancellationRejected,
  AdjustmentRequired, ManualReview, CancelledLocal (`InvoiceModels.cs:3-23`). ManualReview'dan tek çıkış, sahibin
  düğmesiyle yeniden gönderimdir.
- **E-Faturam durum kodları**: 10, 20, 30, 40, 50, 100, 200 → bekliyor; 205 → kabul; 29, 105, 405 → ret; 305 → iptal;
  diğer her kod → inceleme (`TrendyolEFaturamStatusCatalog.cs:7-12`).

**e-Arşiv mi e-Fatura mı** (`EInvoiceRecipientPolicy.cs`; `EInvoiceRecipientResolver.cs:23-30`):

- Bireysel alıcıya sorgusuz **e-Arşiv** kesilir. Kurumsal alıcıda ya da pazaryeri alıcıyı mükellef bildirdiyse **mükellef
  sorgusu** yapılır.
- Mükellef → **e-Fatura** (posta kutusu etiketi seçilir; seçilemezse `EINVOICE_TARGET_ALIAS_UNRESOLVED`). Mükellef değil →
  e-Arşiv (pazaryeri mükellef demişse çelişki: `EINVOICE_TAXPAYER_CONFLICT`). **Bilinmiyor ya da sorgu başarısız → fatura
  durur** ("Türü bilinmeyen fatura kesilmedi…", `EINVOICE_RECIPIENT_REVIEW_REQUIRED`). "Mükellef değil" yalnız sağlayıcının
  belgelenmiş "bulunamadı" yanıtından çıkar.
- Sorgu sonucu 24 saat saklanır ("bilinmiyor" saklanmaz); fatura kesen istek her zaman taze sorgu yapar; kayıtlı tür
  uyuşmazsa `EINVOICE_TAXPAYER_STATUS_CHANGED`.
- Diğer engeller: mikro ihracat ya da TRY dışı para (`INVOICE_EXPORT_FLOW_REQUIRED`), okunamayan ürün satırı
  (`ORDER_LINE_UNREADABLE`), tamamen iade edilmiş sipariş.
- **Son kontrol** (sağlayıcıya gitmeden hemen önce; `InvoicePreIssueCheck.cs:7-102`): sipariş ya da paket iptal, iade,
  teslim edilmemiş ya da ödeme bekliyor; KDV bilinmiyor; satır, adet, fiyat ya da toplam onaylanandan farklıysa kesim durur
  ve **yeniden sahip onayı** istenir. Panel kalan tutarla kendi başına fatura kesmez.

**KDV oranı** (`InvoiceIssuePolicy.cs:90-145`): 1) pazaryerinin satıra bildirdiği oran (Trendyol/N11 `vatRate`, Pazarama
`product.vatRate`, PttAVM `KdvOrani`, Shopify `taxLines.rate`); 2) yoksa ürün kartındaki KDV; 3) ikisi de yoksa **fatura
durur** ("KDV Oranı Bilinmiyor"). Oran asla 0 varsayılmaz; tahmini, 0 olan ya da eski okumadan kalmış oran da "bilinmiyor".

**Fatura linki iletimi**: dosya değil, **link** iletilir: panelin kalıcı adresi
`/api/v1/public/invoice-documents/{token}/content` (uzantı isteyenler için `/invoice.pdf` takma adı). Linkin süresi dolmaz;
yalnız iptal edilen faturanın linki kaldırılınca kapanır. Link bir anahtar gibidir (oturum açmadan faturayı gösterir); bu
yüzden Salt Okunur role listelerde verilmez (`InvoiceDocumentLink.cs:10-29`; `InvoiceLinkVisibility.cs:5-7`).

| Pazaryeri | İletim | Sonra doğrulama |
|---|---|---|
| Trendyol | `seller-invoice-links`, yalnız https | pazaryerinin fatura durumu |
| Hepsiburada | fatura PUT (İstanbul saati); satırlarda fatura bilgisi yoksa gönderilmez, inceleme | paket yeniden okunur |
| N11 | `SaveLinkSellerInvoice`; yeni link eskisinin yerine | yok (`UNVERIFIED`) |
| Pazarama | `order/invoice-link`, sipariş başına tek link | yok (`UNVERIFIED`) |
| PttAVM | `SaveInvoince` | siparişteki `IsInvoice`; çelişkili yanıt `PTTAVM_INVOICE_RESULT_UNCLEAR` |
| Shopify | sipariş metafield'ı; fatura durumu elle: "Fatura Durumunu Değiştir" | — |

- Pazaryerinin fatura durumu: Unknown, NotInvoiced, Received, Invoiced, Rejected. N11, PttAVM ve Pazarama yüklenmiş
  faturayı göremez: **Bilinmiyor** sayılır, asla "Fatura Kesilmedi" değil. Fatura numarası ya da linkin bulunması tek başına
  kanıt değildir (`OrderModels.cs:20-66`).
- **Faturası Yüklendi** (elle işaret; "Fatura Bekliyor" / "Faturası Yüklendi", not "Elle İşaretlendi"): yalnız panel kaydı
  güncellenir; mali fatura kesilmez, link iletilmez.
- İletim Kanıtı etiketleri: "Platforma gönderildi", "Platformda doğrulandı", "Yeniden denenecek", "Platform aktarımı
  başarısız", "Aktarım sonucu bekleniyor", "İptal edilen faturanın bağlantısı pazaryerinden kaldırılmalı".

**Tekrar Gönder ve elle onay düğmeleri** (`invoicing/pages.tsx:703-709`):

| Düğme | Ne yapar |
|---|---|
| **Tekrar Gönder** | Yeni fatura kesmez. Gerekirse belgeyi E-Faturam'dan yeniden alır ve pazaryerine iletir; pazaryerine ulaşmış olabilecek bir iletimi tekrar göndermez. Görünme şartı: Shopify değil, faturası var, iletim tekrarı var, şu an yürümüyor. Sahibin denemesi otomatik deneme sayacını sıfırlar. |
| **Pazaryerinde Gördüm, Tamamla** | Faturayı tamamlandı işaretler; bir daha gönderilmez. |
| **İletildi Say** | Yalnız panel kaydını tamamlar; dışarıya bir şey gitmez. |
| **Portalda İptal Ettim, Yeni Fatura Kes** | Faturayı panelde iptal kapatır; aynı siparişe yeni fatura kesilebilir. Portalda iptal edilmemiş fatura için kullanılmamalı. |
| **Sağlayıcıda Fatura Yok, Yeniden Gönder** | Faturayı yeniden gönderilebilir yapar. Portalda fatura varsa **mükerrer fatura** oluşur. |

**İptal ve link kaldırma**

- **E-Arşiv iptali** (**E-Arşiv İptal İsteği Gönder** / **E-Arşiv İptalini Yeniden İste**): Accepted, Completed,
  AdjustmentRequired, CancellationRejected durumlarında istenebilir. Ağ hatası, kota ya da sunucu hatasında 5 dk sonra
  yeniden denenir; ret (400/422) "İptal Reddedildi" olur ve yeniden istenebilir (`InvoiceCancellationOutcomePolicy.cs:11-30`).
- **Pazaryeri Fatura Linkleri** paneli: iptal edilen faturanın linki kaldırılmadan aynı pakete yeni fatura iletilemez.
  Trendyol'da link belgelenmiş silme uç noktasıyla kaldırılır; Hepsiburada'da satıcı panelinden elle kaldırılır, panel
  doğrular; diğerlerinde yalnız talimat gösterilir. Her işlem açık onay ister.

**İade ve fatura**

- **İade faturası panelde kesilmez** (6 Kas kuralı): asıl faturaya bağlı istek reddedilir — "Panel iade ya da düzeltme faturası kesmez (sahip kararı, 6 Kasım 2026)…"
  (`INVOICE_ADJUSTMENT_FLOW_UNAVAILABLE`, `InvoicingBillingService.cs:1193`; `InvoiceIssuePolicy.cs:39`). Belge türü sabit
  `SATIS`.
- **Henüz faturalanmamış siparişte iade** (`InvoiceSaleReturns.cs:6-130`): fatura yalnız müşteride kalan adetler için
  kesilir; tamamı iade edildiyse kesilmez; açık talepteki adetler faturaya girer; değişim iade sayılmaz.
- **Faturalanmış siparişte iade** (`InvoiceReturnAdjustmentPolicy.cs:6-20`): **kısmi iade** — fatura yerinde kalır, iletim
  sürer, ayrıca "İade Belgesi Gerekli" işi açılır; **tam iade** — fatura "Fatura Düzeltme Gerekli" olur ve iletim durur.

**Belge indirme**: siparişte **Faturayı Gör** ve Faturalar'da **Mali Sağlayıcı Yerel PDF'sini Aç** son belgeyi açar. Belge
E-Faturam'dan güvenli indiriciyle alınır: en çok 20 MB, 3 yönlendirme; içeriğine bakılarak PDF olduğu denetlenir (PDF
değilse "henüz hazır değil" sayılıp yeniden denenir); özel/yerel IP adresleri engellenir
(`SafeRemoteDocumentDownloader.cs:9-154`). Pazaryerindeki fatura bağlantısı yalnız durum "faturalandı" ve adres güvenli
https ise açılır.

### 4.5 Stok

- **Model** (`InventoryModels.cs`): her varyantın **eldeki stoğu** (`OnHand`), ayrılmış (`Reserved`), kullanılabilir
  (`Available`) ve pazaryerinde son görülen miktarı; her hareket **stok defterine** bir satır yazar.
- **Depo**: depo modeli var ama okuma ve yazmanın hepsi tek **ana depo** (`MAIN`) konumunda yapılır; ekranda "Depo" etiketi
  yok. (canlı: `inventory_locations` = 1 satır, `MAIN`.) Depo tanımları ve eşleşmeleri işleyiş ayarıdır, sıfırlamada silinmez.
- **Rezervasyon yok** (sahip kararı, 30 Eki). Eski rezervasyonlar serbest bırakılıp eksik adet olarak yazıldı. (canlı:
  `stock_reservations` 0 satır.)

**Sipariş düşümü** (`SynchronizationPolicies.cs:362-395`):

- Sipariş **okunduğu anda** adet eldeki stoktan düşülür. Bağlantı izin vermedikçe stok sıfırın altına inmez.
- Stoğun karşılayamadığı adet **eksik adet** (`ORDER_STOCK_SHORTFALL`) olarak kaydedilir; yalnız bilgi amaçlıdır ve
  **sonradan da düşülmez** (Ürün Hareketleri de artık "stok gelince düşülür" demez). İptalde önce eksik adet geri alınır, sonra düşülmüş adetler stoğa döner.
- `STOCK_ORDER_CONSUMPTION_BLOCKED` sorunu satır stoktan tam düşülemediğinde açılır: "Sipariş stoğu kısmen düşürüldü: … N
  adet stoktan düşülemedi. Paneldeki stok sayısını kontrol edin. Rezervasyon tutulmaz; stok sıfırın altına inmedi (bilgi
  amaçlı)." Kapanma yolları: eksik adedin iptalle sıfırlanması (`MarketplaceJobProcessor.cs:12006-12007`) ya da o kalemin stoğunun
  **elle** düzeltilmesi, belirlenmesi veya dosyadan yüklenmesi; elle sayım sahibin sayımıdır, uyarıyı kapatır
  (`StockShortfallNotices.cs:17`; `InventoryService.cs:74,142`; `ProductTransferService.cs:511`).
  (canlı: 371 kayıt, hepsi çözülmüş; son 11 Kas 08:30Z.)

**Hareket türleri**: eldeki stoğu değiştirenler — sipariş, elle düzeltme, pazaryeri stoğu aktarımı, açılış stoğu, iade
stoğa ekleme; yalnız iz bırakanlar — eksik adet, sipariş baz çizgisi, kargo ilerlemesi, rezervasyon bırakma, iade
karantina/hasarlı/teslim alınmadı (`ProductActivityContracts.cs:132-144`). Ekranda: İşlem Takibi › Loglar › Ürün
Hareketleri (3.14).

**Pazaryerine giden stok ve Güvenlik Stoğu** (`InventoryModels.cs:17-31`):

- Gönderilen = max(0, eldeki stok − Güvenlik Stoğu) (rezervasyon tutulmadığı için).
- **Güvenlik Stoğu** teklif başına tutulur; bağlantının varsayılan değeri yeni tekliflere kopyalanır. Ürün düzenleyicide ve
  Toplu Değer Ata'da değişir, sınır 0–100.000.
- Trendyol: barkod başına en çok 20.000; aynı istek 15 dk içinde tekrarlanamaz (`TrendyolStockRules.cs:7-18`).

**Otomatik fiyat/stok yazması kapalı** (sahip tercihi): bir bağlantıda üç şart birlikte gerekir — Dış Yazma Ana Anahtarı,
Fiyat Gönderimleri, Stok Gönderimleri (`AutomaticPriceStockWrites.cs:21-46`). Kapalı bağlantıda stok dağıtım işi kuyruğa
hiç girmez ve Stok Uzlaştırması çalışmaz (`StockProjectionOutbox.cs:27-34`). (canlı: 6 bağlantının hepsinde kapalı.)
Açılırsa kapalıyken yapılan değişiklikler geriye dönük gönderilmez. Elle gönderim (Hızlı Güncelle, Platformlarda Güncelle)
bu politikalara bakmaz (`InventoryService.cs:467`).

### 4.6 Ürün Soruları

- Desteklenen: Trendyol, Hepsiburada, N11, Pazarama. **PttAVM ve Shopify için soru okuyucusu yok** (`QuestionsPage.tsx:119`).
- Görünümler: "Ürün Soruları"; "Sipariş Soruları" (yalnız Hepsiburada ve Pazarama); "Hazır Cevaplar". Filtreler: Cevap
  Bekleyenler, Cevaplananlar, Süresi Dolanlar, Diğer Durumlar, Tümü; mağaza ve sıralama seçimi.
- Durum etiketleri: Cevap Bekliyor · Cevap Gönderildi · Doğrulanıyor · Cevap Onay Bekliyor · Cevaplandı · Süresi Doldu ·
  Reddedildi · İnceleniyor (`QuestionsPage.tsx:83-93`).
- Uyarılar: N11'de bir soruya yalnız bir kez cevap verilir ve sonradan değiştirilemez; Pazarama'da cevap önce Pazarama
  onayına düşer, reddedilebilir.
- Eşitleme 2 dakikada bir; uzun süre yeni soru gelmeyen bağlantıda seyrekleşir. İş, İşlem Takibi'nde **Ürün/Yayın**
  sekmesinde görünür (`job-categories.ts:37`).

---

## 5. Genel Bakış, Raporlar, Hakediş ve Promosyon

> (canlı, 12 Kas 2026 18:45Z) Sipariş, ürün, finans ve ödeme tabloları boş (aynı günün sıfırlaması). Rakam kuralları
> koddan okundu, canlı tutarlarla karşılaştırılamadı. Hesabın saat dilimi `Europe/Istanbul`.

Sık geçen dosyalar: `DashboardReadService.cs` (ana sayfa), `ReportsService.cs`, `ReportBuilder.cs`, `ReportCosts.cs`,
`PayoutsService.cs` (Hakediş), `DashboardPage.tsx`, `ReportsPage.tsx`.

### 5.1 Ortak kurallar (Genel Bakış ve Raporlar)

| Kural | Ayrıntı | Kaynak |
|---|---|---|
| Gün sınırı | Hesabın saat diliminde gece yarısından gece yarısına. Sipariş **sipariş zamanına** göre güne düşer; kargo, teslim, ödeme tarihi değil. | `DashboardReadService.cs:355-394`; `ReportsService.cs:392-401` |
| Dönem ve önceki dönem | Seçilen günler + hemen önceki **aynı sayıda tam gün**. Tek gün seçilir ve gün bitmediyse ("Bugün") önceki dönem dün **aynı saate kadar**. Çok günlü dönem bugünü içeriyorsa bugün tam gün sayılır. | `DashboardReadService.cs:562-597` |
| Sınırlar | En çok 366 gün; gelecekte başlayan dönem reddedilir; bitiş bugüne çekilir. | `DashboardReadService.cs:576-587` |
| Hangi mağazalar | Silinmemiş, gizlenmemiş **ve** canlı ortamdaki bağlantılar. Pasif ya da kimlik bekleyen mağazanın geçmiş satışı sayılmaya devam eder. Test mağazaları hiç sayılmaz. | `DashboardReadService.cs:347-353,543`; `ReportsService.cs:73-85` |
| İptal | İptal edilen sipariş ciroya ve sipariş sayısına girmez; iptal edilmiş paketler ve satırların iptal adedi tutardan düşülür. | `DashboardReadService.cs:282-313,539` |
| Satış tutarı | İptal edilmemiş güncel paketlerin müşteri ödediği tutarı (paketlerde tutar yoksa siparişin tutarı) **+ pazaryerinin kendi indirimi** (pazaryeri satıcıya öder). Liste fiyatını aşamaz. Satıcının kendi indirimi düşülmüş kalır. | `DashboardReadService.cs:365-374,532-536`; `ReportsService.cs:403-434` |
| İade tutarı | Geri gönderilen paketlerin tutarı ile satırlardan hesaplanan iade payının (iade adedi ya da **kabul edilmiş** iade talebi adedi) **büyüğü**; sipariş durumu "iade edildi" ise tamamı. **Değişim talebi yalnız para iade edilince** sayılır. | `DashboardReadService.cs:376-408,545-547` |
| Yurt dışı iade istisnası | Trendyol TR dışı vitrin siparişinin iadesi, Trendyol cari hesabında "İade" kaydı yoksa **hiç düşülmez** (Trendyol bunun yerine haftalık yurt dışı iade bedeli keser). | `DashboardReadService.cs:550-559` |
| Para birimi | Raporlar yalnız TRY siparişleri tutara katar (diğerleri ayrı sayılır). Ana sayfa TRY varsa TRY'yi, yoksa en büyük para birimini toplar; diğerlerini "Diğer para birimleri" notunda ayrı gösterir. | `ReportBuilder.cs:435-437,501`; `dashboard-performance.ts:61-100` |

### 5.2 Genel Bakış (ana sayfa)

Başlık "Genel Bakış", üst satırda "Operasyon Merkezi". Açılış dönemi **Bugün** (saat saat) (`DashboardPage.tsx:152-153,296`).

**5.2.1 Hangi kısım nereden gelir**

| Kısım | Kaynak | Yenilenme |
|---|---|---|
| Bekleyen Siparişler | **Canlı sorgu** (Siparişler › Bekleyen İşler ve menü rozetiyle aynı) | 15 sn'de bir, sekme ekrandayken. 6 Kas'ta özet tablodan okumayı bıraktı (rozet 13, kart 12 farkı). |
| Geciken Siparişler, Bekleyen İadeler, Entegrasyon Durumu iş sayıları | **Pano özet tablosu** (`dashboard.snapshot`) | Worker 15 sn'de bir bakar; değişiklikten en erken 30 sn sonra, değişiklik olmasa da 5 dk'da bir yeniden kurar; panel 60 sn'de bir okur (`DashboardSnapshotRefresher.cs:15-16,48`) |
| Faturalar | Canlı (fatura çalışma alanı özeti) | 60 sn |
| İptal | Canlı (bugünün iptalleri) | 60 sn |
| Düşük Stok, Aktif Ürün | Canlı (ürün özeti) | 60 sn |
| Performans (Satış, Toplam Sipariş, Ortalama Sepet, grafik, Kanallara Göre Ciro) | **Canlı sorgu**, özet tablo değil | 60 sn; diğer hazır dönemler arka planda önceden okunur |

Özet tablodaki eski `TodayOrders`, `MonthOrders` gibi sütunlar 12 Kas'ta **tablodan kaldırıldı** (göç
`20261112200733_DropDeadDashboardSnapshotColumns`; `DashboardModels.cs`); hiçbir ekran rakamı bunlardan gelmezdi.

**5.2.2 Operasyon kartları** (üzerine gelince — dokunmatikte "?" ile — mağaza dökümü; bilinmeyen değer "—", asla 0 değil):

| Kart | Sayı | Kural |
|---|---|---|
| **Bekleyen Siparişler** · "N Paket \| M Adet Ürün" | Depoda hazırlanacak/gönderilecek siparişler | NEW, PROCESSING, READY_TO_SHIP, PARTIALLY_CANCELLED, MANUAL_REVIEW; etkin bağlantılar. Hepsiburada'da bir aydan eski, paketsiz ve hâlâ açık görünen sipariş düşülür. Adet = sipariş − iptal (iade düşülmez). (`DashboardModels.cs:11,20-21`; `DashboardReadService.cs:48-61`) |
| **Geciken Siparişler** | Bekleyenlerden kargoya verme son zamanı geçmiş olanlar | Son zaman boşsa gecikmiş sayılmaz (`DashboardReadService.cs:70-73`) |
| **Bekleyen İadeler** | Satıcı kararı bekleyen iade talepleri | Yalnız Aksiyon Bekliyor; talep edildi/kargoda olanlar dahil değil |
| **Faturalar** · "N Bekleyen \| M Yaklaşan" | N: faturası kesilmemiş; M: teslimden **4 gün** geçmiş ve hâlâ fatura bekleyen | `DashboardModels.cs:5`; `InvoicingBillingService.cs:237-239` |
| **İptal** · "N Sipariş \| K Kısmi İptal" | **Bugün** (İstanbul) iptal edilen siparişler | Satırın iptal zamanı bugünse (yoksa panelde görüldüğü zaman). Sipariş bir kez sayılır. İptal zamanı bilinmeyenler dökümde "Zamanı Bilinmiyor" satırında, karttaki sayıya girmez (`DashboardReadService.cs:135-177`) |
| **Düşük Stok** | Ürün sayısı | Bir renk grubunda **varyantların en az yarısının** (yukarı yuvarlanır) stoğu ≤ 0 olan ürün. Adet eşiği yok. Kart pencere açar (`ProductStockPolicy.cs:10-18`) |

**5.2.3 Performans paneli.** Dönemler: Bugün, Dün, Bu Hafta (pazartesiden), Bu Ay, Geçen Ay, Son 30 Gün, Bu Yıl, Özel.
"Kanallara Göre Ciro"dan bir mağaza seçilirse tüm panel o mağazaya süzülür (`dashboard-performance.ts:24-28`).

| Kart | Formül |
|---|---|
| **Satış** | Dönemde satılan her şey: **iadeler dahil, iptaller hariç**. Raporlar'daki **Toplam Ciro** ile aynı tutar olması amaçlanır (`DashboardReadService.cs:209-222`). |
| **Toplam Sipariş** · "Sipariş \| N Adet Ürün" | Tutarı > 0 olan iptal edilmemiş siparişler, **iade edilenler dahil**. Adet = sipariş − iptal − iade adetleri (`DashboardReadService.cs:216,226-238`). |
| **Ortalama Sepet** | Satış ÷ Toplam Sipariş (ikisi de iadeler dahil). |
| **Aktif Ürün** | Katalogdaki aktif ürün; dönemden ve mağazadan bağımsız. Altında "Stoksuz ürün", "Düşük stok", "Toplam". |

- **Günlük / Saatlik Ortalama** = dönem toplamı ÷ seride gün (saat) sayısı; süren gün/saat de böler ("Bu Ay" ayın 12'sinde
  12'ye, "Bugün" 14:20'de 15 saate).
- **Aralık çubuğu** (min – max ve nokta): en zayıf ve en güçlü **bitmiş** gün/saat; süren gün çubuğa girmez. Ortalama ise
  süren günü içerir; ikisi farklı küme üzerindedir.
- **Yüzde değişim** önceki döneme göre, bir ondalık; önceki dönem 0 ise "— önceki dönemde veri yok".
- Grafik: tek günlük dönem saat saat (önceki günün aynı saatiyle), çok günlük gün gün. İpucu: Sipariş, Ürün (Adet), Kargo
  (Paket), Ciro ve "Ciroya Dahil İade".
- **Kanallara Göre Ciro**: mağaza başına Satış; aynı pazaryerinin iki mağazası iki satırdır; kaldırılmış mağaza "Kaldırılmış
  mağaza" adıyla görünür.
- Notlar: "Diğer para birimleri: …" (ana para birimine eklenmez); "N siparişte okunamayan ürün satırı var…" (adet, PttAVM'de
  tutar da eksik olabilir).
- **Raporu İndir**: ekrandaki dönem ve mağaza için gün × para birimi satırlı **CSV** (noktalı virgül, UTF-8 BOM). Sütunlar:
  Tarih, Para Birimi, Ciro (İadeler Dahil), Sipariş (İadeler Dahil), Ürün Adedi, Paket, İade Tutarı, İade Sipariş.

**5.2.4 Entegrasyon Durumu.** Mağaza başına son 7 günün Sipariş, Ürün, Stok, Fiyat, İade, Fatura akışı (Güncel / Gecikiyor /
Hata Var / İşlem Yok) ve Webhook. Alt satır: "Son 24 saatte N istek · Açıkta kalan: N tamamlanamayan · N karar bekleyen iş".
5 dk'da bir okunur (`IntegrationStatusPanel.tsx:31-109`).

**5.2.5 Genel Bakış ile Raporlar neden farklı sayı gösterebilir**

1. **Toplam Sipariş (ana sayfa) ≠ Sipariş (Raporlar)**: ana sayfa yalnız tutarı > 0 ve ana para birimindeki siparişleri sayar;
   Raporlar tutarsız ve döviz siparişler dahil hepsini (`ReportBuilder.cs:499-501`). İki ekranın ipucu da bunu söyler
   (`DashboardPage.tsx` Toplam Sipariş; `ReportsPage.tsx` Sipariş). **Sahip kararı (13 Kas 2026): değişmedi**; döviz siparişi
   yok ve tutarsız sipariş geçmişi sıfırlama yüzünden kontrol edilemedi, sayılar bilerek ayrı kalır.
2. **Satış = Toplam Ciro** yalnız şu durumda kesin tutar: aynı dönem, Raporlar'da platform süzgeci yok, ana sayfada mağaza
   seçili değil, TRY dışı satış yok. Ana sayfa başlık tutarı 0 olan siparişi almaz; Raporlar alır. Satış kartının ipucu bu
   üç koşulu yazar.
3. **Dönem düğmeleri aynı günle hesaplanır**: ana sayfa da Raporlar da İstanbul günüyle (`report-format.ts:9`;
   `date-range.ts` ortak aralıklar); tarayıcının saat dilimi sonucu değiştirmez.
4. Varsayılan dönem: ana sayfa **Bugün**, Raporlar **Son 30 Gün** (`ReportsPage.tsx:49,84`).

### 5.3 Raporlar

Sekmeler: Genel Bakış, Kârlılık (yanında kâr marjı %), Hakediş, Sipariş Kârlılığı, Ürünler, Stok, Operasyon (yanında
"İptal %"), Müşteriler, Coğrafya. Dönem ve Platform süzgeci tüm sekmelere uygulanır; **Hakediş dönemden bağımsızdır**.
Önbellek: bugüne değen dönem 60 sn, kapanmış dönem 1 saate kadar (o günlerin kaydı değişirse hemen düşer)
(`ReportCache.cs:7-17`).

**5.3.1 Raporlar › Genel Bakış (6 kart)**

| Kart | Formül |
|---|---|
| **Toplam Ciro** | Σ satış tutarı (TRY); iptaller yok, **iadeler düşülmez**. Alt satır "Net … · iade …" (`ReportsPage.tsx:576-578`) |
| **Net Kâr** / **Tahmini Net Kâr** / **Tahmini Zarar** | 5.3.2'deki Net Kâr; "Sipariş başına" = Net Kâr ÷ tutarı olan sipariş. Maliyet hiç girilmemişse "Maliyet girilmemiş", müşteride kalan adetlerin %95'inden azının maliyeti girili ise **"Maliyet Eksik"**; %95–%100 arasında kâr gösterilir ve **Tahmini** yazar (`ReportsPage.tsx:432-439,465`) |
| **Kâr Marjı** | Net Kâr ÷ Toplam Ciro; alt satır "Reklam öncesi" = (Net Kâr + Trendyol reklamı) ÷ Toplam Ciro |
| **Maliyete Göre Kâr** | Net Kâr ÷ Ürün Maliyeti |
| **Sipariş** | İptal edilmemiş sipariş sayısı (tutarsızlar dahil); alt satır iptal sayısı · sepet |
| **Satılan Adet** | Sipariş − iptal adedi (iade düşülmez); alt satır "Net" = müşteride kalan; iade adedi |

Oranlar: **iptal oranı** = iptal adedi ÷ (satılan + iptal adedi); **iade oranı** = iade adedi ÷ aynı payda (Trendyol'un satış
raporu gibi). İptal uyarı eşiği %2 (`ReportBuilder.cs:337,495-499`).

**5.3.2 Kârlılık sekmesi.** Üstte **beş kart**: Toplam Ciro, Maliyeti Olan Ciro, Net Kâr, Net Kâr / Satış Fiyatı Oranı, Net
Kâr / Ürün Maliyeti Oranı. Altında Kâr Hesabı, Komisyon ve Trendyol Kargo, Platformlara Göre Net Ciro, Reklam ve Diğer
Kesintiler, platform tablosu. **Brüt Kâr kartı yoktur**: 9 Kas'ta eklenip aynı gün kaldırıldı (`e479e8998`). Koddaki ölü "Tahmini Brüt Kâr" adımı da
12 Kas'ta silindi; döküm yalnız Net Kâr'ı gösterir (`ReportsPage.tsx:1610`).

| Ekrandaki ad | Tanım | İade | Kaynak |
|---|---|---|---|
| **Toplam Ciro** = Kâr Hesabı'nda **Satış Tutarı** | Satıcı indirimi düşülmüş, pazaryeri indirimi eklenmiş satış tutarı (TRY) | dahil | `ReportBuilder.cs:492` |
| **İade Edilen Tutar** | 5.1'deki iade tutarı | — | `ReportBuilder.cs:437-440` |
| **Net Ciro** | Toplam Ciro − İade Edilen Tutar | düşülmüş | `ReportBuilder.cs:439,500` |
| **Maliyeti Olan Ciro** | Maliyeti girili (>0) satırların satış tutarı, Toplam Ciro gibi (iadeler düşülmeden; `CostedSales`) | dahil | `ReportBuilder.cs:522,542` |
| **Ürün Maliyeti** | Σ maliyet × **müşteride kalan adet**; yalnız maliyeti girili satırlar | iade adedi hariç | `ReportBuilder.cs:523-528` |
| **Komisyon** | Trendyol: hesap kesiminde gerçek, kesinleşmeyende satırdaki orandan tahmin. HB, N11, Pazarama: finans kaydı okunan siparişte gerçek, diğerlerinde tahmin. Oran yoksa mağazanın dönem ortalaması; o da yoksa **bilinmiyor** (0 sayılmaz, "N siparişin komisyonu bilinmiyor"). Shopify: komisyon yok. | kalan adet üzerinden | `ReportBuilder.cs:455-475`; `ReportCosts.cs:500-516` |
| **Kargo** | Trendyol: kargo faturası gelende gerçek; gelmeyende mağazanın son 60 gün tarifesi (en sık faturalanan tutar, Eki 2026: 98,34 TL), iadede dönüş kargosu da. Diğerleri: 2026 sözleşme tarifeleri (desi ve kargo firmasına göre, KDV dahil; desi yoksa 1 desi; iadede iki yön). Shopify: sipariş başı 120 TL. | dönüş kargosu eklenir | `ReportCosts.cs:24-35,271-291`; `ReportPlatformCosts.cs:22-86` |
| **Kesintiler** (Hizmet Bedeli, Ödeme Komisyonu, Stopaj) | Trendyol: ödeme günü faturası siparişlere paylaştırılır; yoksa tahmin — hizmet bedeli 13,19 TL (Azerbaycan 10,99 KDV'siz), yurt dışında +%6 uluslararası hizmet bedeli, stopaj KDV'siz satışın %1'i. HB 10,99+KDV; N11 %1,67+KDV; Pazarama 6,90+KDV; PttAVM %1,5; Shopify iyzico %4,29+0,25 TL ve %2. | — | `ReportCosts.cs:83-102`; `ReportPlatformCosts.cs:15-79` |
| **Masraflar Sonrası** | Toplam Ciro − (İade + Komisyon + Kargo + Kesintiler) | — | `ReportsPage.tsx:443-444` |
| **Net KDV** | Yurt içi satışların KDV'si − ürün maliyetinin KDV'si − pazaryeri giderlerinin %20 KDV'si; eksiyse devreden KDV'dir, kârı artırır | kalan satış | `ReportCosts.cs:400-411` |
| **Reklam ve Diğer Kesintiler** | Siparişe bağlı olmayan Trendyol kesintileri: Reklam, Ceza, Erken Ödeme Kesintisi, Diğer Kesinti; işlem tarihine göre | — | `ReportsPage.tsx:705-720` |
| **Net Kâr** = **Tahmini Net Kâr** | Masraflar Sonrası − Net KDV − Ürün Maliyeti − Reklam ve Diğer Kesintiler. **Müşteride kalan adetlerin en az %95'inin maliyeti girili değilse Net Kâr gösterilmez** ("Maliyet Eksik"; hiçbiri girili değilse "Maliyet girilmemiş"); **%95 ve üstünde** kâr gösterilir, eksik adetlerin maliyeti satış payına göre tahmin edilir ve kart **Tahmini Net Kâr** yazar. Aynı koşul Kâr Köprüsü'nde Ürün Maliyeti adımını ve Stok'taki aylık kâr oranını da kapatır. Eksiyse kart adı **Tahmini Zarar**. | — | `ReportsPage.tsx:432-439,465`; `ReportProfitRate.cs:16-23,73` |
| **Net Kâr / Satış Fiyatı Oranı** = Genel Bakış'taki **Kâr Marjı** | Net Kâr ÷ Toplam Ciro | — | `ReportsPage.tsx:1471-1473` |
| **Net Kâr / Ürün Maliyeti Oranı** = **Maliyete Göre Kâr** | Net Kâr ÷ Ürün Maliyeti | — | `ReportsPage.tsx:1474-1476` |

- Trendyol cari hesabı hiç okunamadıysa hesap daraltılır: yalnız İade + "Tahmini Komisyon" düşülür; kargo, hizmet bedeli,
  stopaj, reklam, ceza düşülmez ve açıklamada yazılır.
- Masraf Kalemleri (halka) ile Kâr Köprüsü aynı rakamları kullanır; her kategori bir kez sayılır.
- İki dönemden biri Trendyol cari hesabını okumuş, diğeri okumamışsa kârlar karşılaştırılmaz ("—").
- **Net Kâr maliyet kapsamına bağlıdır** (sahip kararı, 13 Kas 2026; önceki "tek adet bile eksikse gizli" kural kalktı):
  kesintiler tüm satışlardan düşüldüğü için maliyetin küçük bir kısmıyla hesaplanan kâr olduğundan yüksek çıkardı. Müşteride kalan
  adetlerin **en az %95'inin maliyeti girili** ise kâr hesaplanır; **eksik adetlerin maliyeti satış payına göre tahmin edilir**
  (girili maliyetin, o adetlerin cirosuna oranı × maliyetsiz adetlerin cirosu; ciro bilgisi yoksa adet başına ortalama maliyet) ve
  kart, ipucu ve Ürün Maliyeti satırı **"Tahmini"** yazar, kaç adedin eksik olduğunu söyler. **%95'in altında** kart **"Maliyet
  Eksik"** yazar ve kâr gösterilmez (`ReportProfitRate.cs:16-23`; aynı kural ekranda `report-format.ts:19-23`;
  `ReportsPage.tsx:432-439,458-465`). Sunucuda kâr oranı da aynı kuralı kullanır (`ReportProfitRate.cs:73`).
- **Maliyet KDV dahil kabul edilir**: Net KDV maliyetin içindeki KDV'yi düşer. Maliyet KDV hariç girilirse Net Kâr
  olduğundan yüksek çıkar; Ürün Maliyeti, Net KDV ve Promosyon ipuçları bu varsayımı yazar (`ReportCosts.cs:409,528`;
  `ReportsPage.tsx:602,613`).
- İade edilen ürünün maliyeti düşülmez (stoğa döndüğü varsayılır); hasarlı iade için ayrı kalem yok.
- **Sipariş Kârlılığı** sekmesindeki kart **Sipariş Kâr Marjı** adını taşır: yalnız kârı bilinen siparişlerin kârı ÷ onların
  tutarı, reklam ve cezalar hariç; ipucu Genel Bakış'taki Kâr Marjı'ndan farkını yazar (`OrderProfitability.tsx:46-48`).

**5.3.3 Maliyetin kaynağı**

- Tek kaynak: ürün kartında varyantın **Maliyet** alanı. 0 ya da boş = **girilmemiş** (0 TL maliyet değil)
  (`OrderLineCostSnapshot.cs:6-15`).
- Sipariş satırı varyanta ilk bağlandığında o anki maliyeti ve desiyi satıra **dondurur**; sonradan değişen maliyet geçmiş
  satışı değiştirmez. Satırda değer yoksa varyantın **güncel** maliyeti kullanılır; yani maliyeti hiç girilmemiş eski satış,
  maliyet bugün girilince geriye dönük o maliyetle kâra girer.
- Promosyon ve komisyon tarifesi kârları dondurulmuş değil, **güncel** maliyeti kullanır.

**5.3.4 Hakediş sekmesi (Ödeme Özeti dahil)**

Kapsam: **Trendyol, Hepsiburada, N11, Pazarama**; PttAVM ve Shopify desteklenmez. Kaynak: pazaryerlerinin muhasebe kayıtları
ve ödeme emirleri. Dönem süzgecine bağlı değildir; geçmiş ödemeler son **90 gün** (`PayoutsService.cs:13-16,41-46,256`).

| Kart | Formül |
|---|---|
| **Toplam Güncel Bakiye** | Henüz ödenmemiş **tüm** kayıtların net toplamı (vadesi geçmişler dahil). **Komisyon faturaları sayılmaz** (komisyon zaten satıcı gelirinden düşülmüş). Trendyol'un "Toplam Güncel Bakiye"siyle aynı toplam (4 Kas 2026'da 295.540,76 TL'ye kuruşu kuruşuna). Henüz kesilmemiş kesinti faturaları dahil değil (`PayoutsService.cs:303-323,465-468`). |
| **Teslim Bekleyen Sipariş** | NEW, PROCESSING, READY_TO_SHIP, SHIPPED durumundaki TRY siparişlerin net tutarı; bakiyede yok, teslimden sonra girer. Kısmi iptal ve inceleme durumları dahil değil. |
| **Sıradaki Ödeme** | Gelecek ilk ödeme günü ki toplamı > 0; ≤0 olan gün atlanır. Vadesi geçmiş/tarihsiz bekleyenler girmez. |
| **Son Ödeme** | Pazaryerinin bildirdiği son pozitif ödeme. |
| **Vadesi Geçmiş Bekleyen** | Vadesi geçmiş ama bir ödemeye bağlanmamış bakiye; eksiyse bekleyen kesinti. "Ödenmeyecek" demek değil. |
| **Erken Ödeme** | Son 90 günde alınan erken ödemeler (Trendyol `CashAdvance`). |

Kayıt durumu: **Ödendi**, **Tahmini** (vadesi gelmemiş), **Vadesi Geçti** (ödeme henüz okunmamış olabilir); liste süzgecinde
ayrıca **Bekleyen Kesinti**.

**Ödeme günü kuralı**:

| Pazaryeri | Kural |
|---|---|
| Trendyol Türkiye-Körfez ve Azerbaycan | **Pazartesi** ödemesi o haftanın Pzt–Çar vadelerini, **perşembe** ödemesi Per–Paz vadelerini öder. Satış kaydı vadesinden önceki ilk Pzt/Per'e, kesinti sonrakine düşer. Salı/Cuma "Erken Ödeme" günüdür. Resmî tatil kaydırır; gün tahminidir. |
| Trendyol Avrupa | Ayın **4. pazartesisi** (22–28'i arası); o güne kadar vadesi gelen her şey. |
| Hepsiburada | Her **salı**: vadeden sonraki ilk salı. |
| N11 | **Perşembe**; bekleyen kayıt havale gününden sonraki ilk perşembeye. |
| Pazarama | Aktarım tarihinde; tarihi geçmiş ve ödenmemiş kayıt "Vadesi Geçti". |

Trendyol gelecek ödeme tutarı = kayıtlar − **ödemeden önceki akşam kesilecek** tahmini kesintiler (kargo, iade kargosu, hizmet
bedeli 13,19, uluslararası hizmet bedeli %6, stopaj %1). **Toplam Güncel Bakiye bu tahmini içermez** (`PayoutsService.cs:564-593`).
**Ödeme Özeti** ayrı sekme değil, Hakediş sekmesindeki listedir: pazaryeri kartları, süzgeçler (Pazaryeri, Bölge, Durum,
tarih, tutar, Sıralama), "Gelecek Ödemeler" / "Geçmiş Ödemeler". Geçmiş ödemede okunan kayıt toplamı ödeme tutarından
farklıysa fark yazılır; kayıtlar ödemeden bir gün sonra okunduğu için o gün "henüz okunmadı" yazar. Trendyol tahmini ile
Trendyol'un kendi ekranı arasında açıklanamayan küçük farklar kaldı (11 Kas: 26 Kas ödemesinde −2.761,83 TL; desi/kargo
kademesi farkı; canlıda yeniden ölçülemedi).

**5.3.5 Reklam raporu** (Raporlar'da, Promosyon'da değil). Raporlar › Genel Bakış'taki "Reklam · Trendyol" kartı ve "ROAS
(kampanya)" satırı **Trendyol Reklam Raporu** penceresini açar. Satıcı Trendyol Reklam Yönetimi'nden indirdiği "Ürün
Reklamları Raporum" .xlsx dosyasını yükler; **her yükleme ayrı saklanır, mağaza başına en yenisi gösterilir**. Rakamlar
kampanyaların **başından rapor anına kadarki** toplamlardır; seçili dönemin rakamı değildir ve Net Kâr'dan düşülen
"Trendyol Reklamı" ile **toplanmaz**. 7 günden eski rapor "eski" işaretlenir (`AdReport.tsx:9-91`; `AdReportService.cs:10-11`).
Kârlılıktaki **Trendyol Reklamı** cari hesaptaki reklam bedelidir; Reklam Payı = bu tutar ÷ ciro.

### 5.4 Promosyon

Sayfa **Promosyon** (üstte "Analiz"). Yalnız **Trendyol** mağazaları; silinmiş ve test bağlantıları listelenmez. Sekmeler
(`?tab=`): **Ürün Komisyon Tarifesi**, **Plus Komisyon Tarifesi**, **Avantajlı Ürün Etiketi**, **Flash Ürünler**
(`PromotionsPage.tsx:19-27`). **Pazaryerine gönderim yoktur**: hiçbir sekme Trendyol'a fiyat, teklif ya da seçim yazmaz
(`CommissionTariffEndpoints.cs:13-30`).

**Ürün Komisyon Tarifesi**

- Trendyol satıcı panelinden indirilen .xlsx (en çok 10 MiB) yüklenir; geçerli günler seçilir. Her satır için bantlar, her
  bandın komisyonu ve kalan kâr, en çok kâr bırakan bant gösterilir.
- **Yeni dosya eskisini değiştirir**: günleri yeni dosyayla **çakışan** ve **tüm günleri geçmiş** dosyalar aynı kayıtta
  silinir (satırları, seçimleri, dosyasıyla). Yürürlükte olup yeni dosyanın kapsamadığı günleri olan dosya bitene kadar
  kalır. Yükleme başarısızsa eskiler korunur. Ekranda: "Yeni dosya yüklenince önceki dosya ve satırları silinir; yürürlükteki
  başka haftanın tarifesi bitene kadar kalır." (`CommissionTariffService.cs:127-130,203-204`).
- **Excel İndir**: seçimleri Trendyol'un kendi tarife dosyasına yazar ("YENİ TSF (FİYAT GÜNCELLE)" ve "Tarife Seçimi");
  dosyayı Trendyol'a **satıcı kendisi** yükler. İki dönemli dosyada aynı fiyat "7 Günlük Fiyat" olarak yazılır.
- Kâr = fiyat − komisyon − maliyet (güncel) − mağazanın kargo tarifesi − 13,19 TL hizmet bedeli − %1 stopaj − Net KDV.
  Maliyet, KDV ya da kargo tarifesinden biri yoksa kâr **bilinmiyor**. "Kâr Oranı" = kâr ÷ maliyet (`UnitProfit.cs:10-24`).
  Panelin ipucu bunu aynen söyler (`PromotionOffersPanel.tsx`).
- Raporlardaki komisyon tarifeden değil, sipariş ve finans kayıtlarından gelir (ipucu da böyle yazar,
  `CommissionTariffsPanel.tsx`). Satışın tarifedeki komisyonunu bulan `CommissionTariffLookup` sınıfı **silindi**
  (sahip kararı, 13 Kas 2026: siparişin kendi komisyonu var; `8cd9ab12b`, `1b2be7dbd`); kodda artık yoktur.

**Plus Komisyon Tarifesi, Avantajlı Ürün Etiketi, Flash Ürünler**

- Her biri kendi .xlsx dosyasıyla çalışır (10 MiB). Yanlış sekmeye başka türün dosyası yüklenirse doğru türe kaydedilir ve
  söylenir.
- **Yeni dosya eskisini koşulsuz değiştirir** (aynı mağaza ve tür için; teklif haftalıktır). "Dosyayı Sil" ile elle de
  silinir (`PromotionOfferService.cs:44-46`).
- Hesap: teklif fiyatındaki komisyon ve kalan kâr, bugünkü fiyatla kalanın yanında. Plus: Plus üst fiyat limitinde önerilen
  komisyon. Avantajlı Ürün Etiketi: her yıldızın üst fiyatı, yürürlükteki Ürün Komisyon Tarifesi bandıyla (tarife yoksa oran
  bilinmez). Flash: tarifeye dahil üründe flaşın başladığı haftanın bandı. Mevcut komisyon sırası: son Plus dosyası → son
  flash dosyası → son 120 günde o barkodun sipariş satırındaki oran. Varyant maliyetleri farklıysa kâr aralık olarak gösterilir.
- **Excel'e aktarma yok**; Excel İndir yalnız Ürün Komisyon Tarifesi'nde.

### 5.5 Saat dilimi özeti

| Yer | Saat |
|---|---|
| Genel Bakış ve Raporlar günleri | Hesabın saat dilimi (canlı Europe/Istanbul), sipariş zamanına göre |
| Raporlar dönem düğmeleri | İstanbul günü (5.2.5) |
| Hakediş vade/ödeme günleri | Her zaman İstanbul |
| Trendyol sevk son zamanı | Gerçek UTC anı (23:59 İstanbul = 20:59Z) |
| Reklam raporu zamanı | Dosya adındaki İstanbul saati |
| Hepsiburada yazdığı ve sorguladığı saatler | İstanbul yerel |
| Veritabanı | Her zaman UTC |

---

## 6. Altyapı, Arka Plan İşleri ve İşlem Takibi

### 6.1 Bileşenler

| Bileşen | Ne yapar | Kaynak |
|---|---|---|
| **Sunucu** | Hostinger KVM 4, `187.126.24.163`, gece gündüz açık. Kök disk 193 GB (canlı: %49 dolu, 12 Kas). | canlı |
| **Caddy** (`marketplacehub-caddy-1`) | HTTPS ucu (`panel.ravencia.com`), TLS, sıkıştırma, güvenlik başlıkları. `/health/live` herkese açık; `/health/ready` ve `/health/queues` yalnız sunucunun kendi ağından. Webhook ve herkese açık fatura bağlantıları erişim günlüğüne yazılmaz. | `deploy/caddy/Caddyfile.production:80-86` |
| **API** (`marketplacehub-api-1`) | Panelin arka ucu; webhook alır; ekranları anında yeniler (SignalR); saatlik kalıntı temizliği burada çalışır. | `OperationsRealtimeBroadcaster.cs:7-16`; `PurgedFileRetentionService.cs:7-12` |
| **Worker** (`marketplacehub-worker-1`) | İşleri kiralar ve çalıştırır; zamanlayıcı, kira toplayıcı, saklama döngüleri. | `Worker.cs:47-67` |
| **PostgreSQL 18.6** (`marketplacehub-postgres-1`) | Tek veritabanı; en çok 150 bağlantı. İş kilitleri ve kota kovaları da burada. | `deploy/compose/compose.yaml:81` |
| **migrate** / **backup** | Her deploy'da bir kez çalışan şema göçü konteyneri; yalnız yedek alırken çalışan konteyner. | `deploy/scripts/fast-deploy.sh:179-207`; `compose.yaml:207-227` |

Kaynak sınırları: API ve Worker 1,5 GB bellek / 2 CPU; PostgreSQL 6 GB / 3 CPU. Konteynerler kendiliğinden yeniden başlar.
Worker'ın veritabanı kalp atışı 120 sn'den eski kalırsa Worker kendini durdurur ve Docker yeniden başlatır; bu bilinçli bir
kendini kurtarmadır (`Worker.cs:723-737`).

### 6.2 Deploy ve ship akışı

Sıra: **commit → `codex/<konu>` dalı → Validate yeşil → `main` → Deploy → sunucuda fast-deploy → canlı.**

1. **Ship betiği** (`.claude/skills/ship/ship.sh`): commit'i `codex/<konu>`'ya iter, Validate'i bekler, `main`'i yalnız
   `git merge-base --is-ancestor origin/main <sha>` doğruysa ileri sarar. Bütün çalışma ağaçları için tek kilit (60 dakikadan
   eski kilit devralınır). Force-push yok.
2. **Validate** (`.github/workflows/ci.yml`): `main` ve `codex/**` itişlerinde; işler: plan, ops, backend-format,
   backend-build, backend, backend-postgres, image-app, image-edge, frontend, gate.
3. **Deploy** (`.github/workflows/deploy.yml`): yalnız `main`'in yeşil Validate'inden sonra (ya da elle). Aynı anda tek
   deploy. Sunucuya yalnız sha gönderir; sonunda dışarıdan `/health/live` dener.
4. **Sunucu girişi** (`deploy/scripts/ci-entrypoint.sh`): sha `origin/main` üzerinde değilse reddeder; son başarılı deploy bu
   sha'yı zaten içeriyorsa bir şey yapmaz; geri alınmış ("hold") sha'yı ve atalarını deploy etmez.
5. **fast-deploy.sh**: imajlar sunucuda derlenir → **taze, doğrulanmış yedek** alınır → şema göçü değiştiyse yeni göç yedekten
   açılmış geçici kopyada **prova edilir** (20 dk; başarısızsa canlıya dokunulmaz; disk %80 üstündeyse durur) → göç canlıda
   çalışır (eski API/Worker hizmete devam eder) → yeni api/worker/caddy başlatılır ve doğrulanır → açılmazsa ve göç
   değişmediyse **önceki imajlara otomatik dönülür**, sha "hold" olur, alarm gider → son iki sürümün imajı tutulur → panel alarm
   ayarları sunucuyla eşitlenir (`fast-deploy.sh:58-294`).
6. **Canlı sayılma**: api, worker ve caddy'nin aynı sha'yı gösterdiği doğrulanmadan "canlı" denmez.

Deploy anında sağlık izleme bir tur "Unhealthy (run 1)" yazabilir; alarm ancak iki ardışık başarısız turdan sonra gider.

### 6.3 İş kuyruğu (`integration.jobs`)

Her işin yönü: **Okuma** (platformdan okur), **Yazma** (platforma gönderir), **Bildirim** (platform itti), **Panel İçi**
(dışarıya çağrı yok) (`job-presentation.ts:7-14`).

**Durumlar** (`OperationalModels.cs:3`). Her durumun **tek adı** vardır; rozet, Durum süzgeci, ürün yayın iş penceresi ve
bağlantı akış penceresinin sayıları aynı adı yazar (`job-status-labels.ts:1-21`):

| Ekranda (rozet, süzgeç, pencereler) | Sabit | Anlamı |
|---|---|---|
| Bekliyor | `PENDING` | Kuyrukta; zamanı gelince bir Worker yuvası kiralar. |
| Çalışıyor | `LEASED` | Bir Worker kiraladı; kira 2 dk, 30 sn'de bir yenilenir (`JobRetryPolicy.cs:17-25`). |
| Yeniden Denenecek | `RETRY_SCHEDULED` | Hata, erteleme ya da bekleme kontrolü sonrası belirli zamanda yeniden çalışacak. |
| Engellendi | `BLOCKED` | Kalıcı ret (yetki, doğrulama, yazma kapalı); kendiliğinden denenmez. |
| İnceleme Bekliyor | `MANUAL_REVIEW` | Sonuç belirsiz ya da beklenmeyen veri; insan kararı gerekir. |
| Başarılı | `SUCCEEDED` | Bitti ("yapılacak bir şey yoktu" notuyla da olabilir). |
| Deneme Limiti Doldu | `DEAD` | Deneme hakkı bitti ya da açıkça ölü işaretlendi (eskiden bazı yerlerde "Hata" ya da "Ölen İş" yazılırdı). |
| İptal Edildi | `CANCELLED` | Kullanıcı iptal etti. |

Durum çipleri: **Başarılı**, **Çalışıyor**, **Bekliyor** (Bekliyor + Yeniden Denenecek), **Hata** (Engellendi +
İnceleme Bekliyor + Deneme Limiti Doldu), **İptal** (`operations/pages.tsx:602-607`). Deneme satırında ayrıca "bekliyor" sonucu vardır:
pazaryeri kabul ettiği işi hâlâ işliyor; panelde "Kontrol Edildi · İşleniyor / Onay Bekleniyor" (`job-waiting.ts:24-26`).

**Deneme ve bekleme** (`JobRetryPolicy.cs:7-68`; `JobContracts.cs:45-62`; `JobLeaseService.cs`):

- Varsayılan **en çok 6 deneme**; aralar 5 sn, 15 sn, 1 dk, 2 dk, 5 dk (+ %0–20 yayılma).
- Pazaryeri "şu kadar bekle" (Retry-After) dediyse en çok 1 saate kadar uyulur.
- Fatura iletimi ayrı takvim: 30 sn, 2 dk, 10 dk, 30 dk.
- Hata sınıfına göre: **kota (429)** → hak yenmez, bağlantı + kilit grubu birlikte bekletilir; **ağ / sunucu hatası** → normal
  yeniden deneme; **iç hata / sözleşme ihlali** → İnceleme Bekliyor; **kimlik reddi** → Engellendi + bağlantının aynı
  gruptaki işleri 5 dk bekletilir; diğerleri → Engellendi.
- **Erteleme** (kilit dolu vb.): hak geri verilir; 24 saatten uzun ertelenen iş hakkını geri alamaz.
- **Dilim** (uzun iş bir parçayı bitirdi): hak geri verilir, ~2 sn sonra devam eder.
- **Bekleme kontrolü** (pazaryeri hâlâ işliyor): iş 1 günden gençse (ürün onayında 8 gün) hak yenmez.
- **Yazma çakışması**: aynı kayda eşzamanlı güncelleme ilk 3 kez hak yemeden ertelenir (`JobConflictPolicy.cs:19-43`).
- Hak bitince durum Hata olur (`MAX_ATTEMPTS_EXHAUSTED`).

**Kira ve kira toplayıcı**: kiralama tek sorgudur; öncelik sırası, aynı bağlantı + kilit grubunda çalışan iş yoksa, bağlantının
kota beklemesi bitmişse ve **Paneli Tamamen Sıfırla** sürmüyorsa (sürerken hesabın başka hiçbir işi başlamaz,
`JobLeaseService.cs:74-81`). Aynı öncelikte daha az işi çalışan hesap öne geçer. Kira toplayıcı 30 sn'de bir süresi geçmiş
kiraları geri alır (`LEASE_EXPIRED`). İş tipi zaman aşımları: ürün kataloğu okumaları 60 dk, paket fatura kontrolü 15 dk,
Trendyol kargo bilgisi 30 dk, diğer sipariş okumaları 10 dk, geri kalanı 60 dk (`JobTypeRegistry.cs:82-91`). Worker kapanırken
(deploy) **yazma işleri** yarıda kesilmez (`JobTypeRegistry.cs:48-55`).

**Şeritler ve öncelik** (`WorkerLanes.cs:40-57`; `JobPriorityPolicy.cs:13-25`): öncelik küçük olan önce — kullanıcı eylemi
−3, toplu kullanıcı işi −2, ilk veri çekimi −1, anlık 0, yazmalar 1, etkileşimli 2, varsayılan 3, mutabakat 4, katalog 5,
kurtarma 6.

| Şerit | Yuva | Ne alır |
|---|---|---|
| user | 2 | Kullanıcının beklediği işler (öncelik ≤ −2) |
| initial-data-sync | 1 | İlk veri çekimi |
| order-status | 1 | Trendyol/Shopify açık sipariş durumu |
| returns-read | 2 | İade okumaları |
| product-catalog | 1 | Ürün/katalog okumaları, Günlük Yayın Doğrulaması |
| hepsiburada-status, hepsiburada-orders, pazarama-orders, pttavm-orders | 1'er | O pazaryerinin sipariş akışları |
| hot | 3 | Öncelik 0–2 diğer işler (webhook, yeni sipariş, yazmalar) |
| background | 2 | Öncelik ≥3 diğer işler |

Toplam 16 yuva, veritabanı havuzu 45. Yeni iş eklenince boştaki şeritler hemen uyanır (`Worker.cs:691-720`).

**Elle müdahale** (İşlem Takibi › iş ayrıntısı; yalnız Sahip ve Yönetici görür ve yapabilir; `operations/pages.tsx:341`):

- **Manuel Yeniden Dene**: yalnız Engellendi, İnceleme Bekliyor, Hata durumunda; **bir** ek hak verir. Fatura işlerinde
  faturanın durumu güvenli değilse reddedilir (`JobOperationsService.cs:227-254`).
- **İptal Et** (çalışan işte **Durdur**): Tamamlandı, Hata, İptal Edildi dışındaki her durumda (`JobOperationsService.cs:402-421`).

**Saklama** (Worker saatte bir; `JobRetentionService.cs:6-20`): zamanlanmış/webhook/doğrulama/elle okumalar başarılı ya da
iptalse 14 gün, diğerleri 30 gün; dış etki koruyan işler (ürün oluşturma, fiyat-stok, paket/iade işlemi, fatura kes/ilet/
iptal) **silinmez** (tekrar gönderimi önleyen anahtar budur); sürüm taşıyan stok/fiyat işleri 90 gün; webhook gelen kutusu
işlenmiş 30 gün, en geç 90 gün; kota kullanım satırları 2 gün. (canlı, 12 Kas: 1074 Tamamlandı, 1 İnceleme Bekliyor.)

### 6.4 Bütün iş tipleri

"Panel adı" İşlem Takibi'ndeki eylem adıdır (`job-presentation.ts:18-76`). Tetikleyici: **Zamanlayıcı** (akış), **Kullanıcı**
(panel düğmesi), **Webhook**, **Sistem** (Worker'ın kendi taraması ya da başka işin ardından).

| İş tipi | Panel adı | Yön | Tetikleyici |
|---|---|---|---|
| `*_CONNECTION_TEST` | Bağlantı Testi | Okuma | Kullanıcı; başarısız bağlantı kendiliğinden yeniden test edilir |
| `TRENDYOL_CAPABILITY_PROBE` / `EFATURAM_STAGE_CAPABILITY_PROBE` | Yetki Kontrolü / Test Ortamı Yetki Kontrolü | Okuma | Kullanıcı |
| `TRENDYOL_STAGE_TEST_ORDER` | Test Siparişi Oluşturma | Yazma | Kullanıcı (yalnız test ortamı; Hepsiburada testi de bu tipte) |
| `*_REFERENCE_SYNC` | Kategori ve Özellik Listesi Çekme | Okuma | Zamanlayıcı (Katalog Referansları), etkinleştirme, Kullanıcı |
| `CATALOG_STRUCTURE_IMPORT` | Ürün Yapısı Çekme | Okuma | Yalnız API; Kategori Eşleme'ye "Yapıyı Pazaryerinden Çek" düğmesi **eklenmeyecek** (sahip kararı, 13 Kas: yapı ürün çekiminde zaten geliyor; `MarketplaceSalesService.cs:1555-1565`) |
| `*_ORDER_SYNC` | Yeni Sipariş Çekme | Okuma | Zamanlayıcı (Siparişler ve Paket Durumları), Kullanıcı |
| `*_ORDER_RECOVERY_SYNC` | Sipariş Geçmişi Taraması | Okuma | Zamanlayıcı (Sipariş Geçmişi Kurtarma) |
| `ORDER_STATUS_SYNC`, `*_ORDER_STATUS_SYNC` | Açık Sipariş Durumu Kontrolü | Okuma | Zamanlayıcı; açık paket yoksa konmaz |
| `ORDER_RECONCILIATION`, `SHOPIFY_ORDER_RECONCILIATION` | Sipariş Uzlaştırması | Okuma | Zamanlayıcı (Sipariş Uzlaştırması; geriye 1 / 3 / 90 gün) |
| `*ORDER_INVOICE_RECONCILIATION` | Paket Fatura Durumu Kontrolü | Okuma | Zamanlayıcı (Paket Fatura Kontrolü) |
| `TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION` | Kargo Bilgisi Tamamlama | Okuma | Zamanlayıcı |
| `*_WEBHOOK_INGEST` | Anlık Bildirim İşleme | Bildirim | Webhook |
| `SHIPMENT_ACTION` | Paket İşlemi Gönderme | Yazma | Kullanıcı |
| `TRENDYOL_COMMON_LABEL` | Kargo Etiketi Oluşturma | Yazma | Kullanıcı |
| `PRODUCT_SYNC`, `SHOPIFY_/HEPSIBURADA_PRODUCT_SYNC` | Ürün Çekme | Okuma | Kullanıcı (zamanlanmış ürün çekimi kapalı) |
| `PRODUCT_CREATE` / `PRODUCT_UPDATE` / `PRODUCT_ARCHIVE` | Ürün Oluşturma / Güncelleme / Satışa Kapatma | Yazma | Kullanıcı |
| `PRODUCT_APPROVAL_RECONCILE` | Ürün Onay Durumu Kontrolü | Okuma | Sistem (gönderilen ürünün ardından) |
| `LISTING_VERIFY` | Günlük Yayın Doğrulaması | Okuma | Zamanlayıcı (03:00–06:00), Shopify ürün silme webhook'u |
| `LISTING_LOOKUP` | Varyant Pazaryerinde Aranıyor | Okuma | Sistem (çalıştırma başına en çok 300) |
| `PRICE_INVENTORY_SYNC` | Fiyat ve Stok Gönderme | Yazma | Sistem (otomatik yazma açıksa) ya da elle güncelleme |
| `STOCK_PROJECTION_DISPATCH` | Stok Gönderim Hazırlığı | Panel İçi | Sistem (stok değişimi) |
| `STOCK_RECONCILIATION` | Stok Uzlaştırması | Panel İçi | Zamanlayıcı — yalnız otomatik fiyat-stok yazması açık bağlantıda |
| `RETURN_SYNC`, `HEPSIBURADA_RETURN_SYNC` | Yeni İade Çekme | Okuma | Zamanlayıcı (İade Talepleri) |
| `RETURN_STATUS_SYNC` | Açık İade Durumu Kontrolü | Okuma | Zamanlayıcı; açık iade yoksa konmaz |
| `RETURN_RECONCILIATION` | İade Uzlaştırması | Okuma | Zamanlayıcı |
| `RETURN_ACTION` | İade Kararı Gönderme | Yazma | Kullanıcı |
| `EXTERNAL_EFFECT_RECONCILE` | Gönderim Sonucu Doğrulama | Okuma | Sistem (2 dk'da bir süpürme) |
| `MARKETPLACE_QUESTION_SYNC` | Müşteri Sorularını Çekme | Okuma | Zamanlayıcı |
| `TRENDYOL_FINANCE_SYNC` / `PAZARAMA_FINANCE_SYNC` / `N11_SETTLEMENT_SYNC` / `HEPSIBURADA_FINANCE_SYNC` | Cari Hesap Ekstresi / Sipariş Finans Hareketleri / Hakediş Kayıtları / Muhasebe Kayıtları Çekme | Okuma | Zamanlayıcı (Cari Hesap ve Masraflar; yalnız canlı mağaza) |
| `INVOICE_SUBMIT` | Fatura Oluşturma | Yazma | Kullanıcı (sahip/yönetici onayı) |
| `INVOICE_RECONCILE` / `INVOICE_DOCUMENT_FETCH` | Fatura Durumu Sorgulama / Fatura Belgesi Alma | Okuma | Sistem |
| `INVOICE_MARKETPLACE_DELIVERY` | Faturayı Pazaryerine İletme | Yazma | Sistem/Kullanıcı (kesimden sonra) |
| `MANUAL_ORDER_INVOICE_DELIVERY` | Faturayı Elle İletme | Yazma | Kullanıcı |
| `INVOICE_CANCELLATION` | Fatura İptali | Yazma | Kullanıcı |
| `INVOICE_DUE_SCAN` | Fatura Süresi Taraması | Panel İçi | Zamanlayıcı: faturası olan her hesapta 30 dk'da bir (fatura kesmez) |
| `IMPORT_PREVIEW` / `IMPORT_APPLY` | Dosya Önizleme / Dosyayı Uygulama | Panel İçi | Kullanıcı (İçe Aktarımlar) |
| `CONNECTION_FULL_RESET` | Tam Sıfırlama | Panel İçi | Kullanıcı |
| `PANEL_FACTORY_RESET` | (panel kataloğunda adı yok) | Panel İçi | Kullanıcı (Sistem Ayarları › Veri) |

### 6.5 Kilit grupları

Bir bağlantıda aynı **kilit grubundaki** iki iş aynı anda çalışmaz (`MarketplaceSyncExecutionLock.cs:89-165`):

| Grup | İşler |
|---|---|
| `orders` | Sipariş okumaları, sipariş mutabakatı, sipariş webhook'u, gönderim sonucu doğrulama, Shopify/HB fatura kontrolü |
| `order-invoices` | Trendyol paket fatura kontrolü |
| `order-cargo-info` | Trendyol kargo bilgisi |
| `shipment-actions` | Paket işlemi gönderme |
| `returns` | İade okuma/karar/mutabakat; iade webhook'ları |
| `stock` | Fiyat-stok işleri |
| `products` | Ürün okumaları/yazmaları, katalog, Günlük Yayın Doğrulaması |
| `listing-lookup` | Tekil varyant aramaları |
| `references` | Kategori/marka listeleri |
| `finance` | Dört finans okuması |
| `connection` | Diğerleri |

Grup doluysa iş hak yemeden ertelenir: webhook birkaç saniye, kullanıcı işi 10 sn, fatura kontrolü 3 dk, tekil sipariş
yenileme 30 sn (`MarketplaceSyncExecutionLock.cs:204-235`).

### 6.6 Kota

- **Paylaşılan kova**: API ve Worker aynı jeton kovasını kullanır; kota satıcı başına bir kez sayılır (`SharedRateLimiter.cs:7`).
- Dakika başına istek sayıları tutulur, 2 günden eskisi silinir.
- 429 ya da kimlik reddi gelince bağlantı + grup süre dolana kadar kiralanmaz (**bağlantı geri çekilmesi**).
- Pazaryeri kota gerekçeleri politikada yazılı (ör. Trendyol sipariş servisleri dakikada 30; ön plan payı 12, arka plan 18)
  (`SynchronizationPolicies.cs:1492-1496`). Pazaryeri limitleri: `/marketplace-docs`.
- Panel: İşlem Takibi › **API Sağlık** tablosu (Anahtar / Yetki, Geride Kalma, Hata Oranı (24 sa), Hız Sınırı (429), Kuyruk
  Yaşı, Webhook) (`ApiHealthSignalsPanel.tsx:6,13`).

### 6.7 Tekrarı önleyen yapılar

| Yapı | Ne işe yarar |
|---|---|
| **Outbox** (değişiklik olayı) | Veri değişince aynı işlemde bir "değişti" olayı yazılır; API saniyede bir okuyup açık panelleri yeniler. Yayınlanan olay 3 gün tutulur. Pazaryerine mesaj göndermez. |
| **Inbox** (webhook gelen kutusu) | Her webhook teslimatı bir kez yazılır; aynı teslimat ikinci kez gelirse yeni iş açılmaz. |
| **İş tekrar anahtarı** | Aynı işin iki kez kuyruğa girmesini ve dış etkinin iki kez uygulanmasını önler. |
| **İstek tekrar anahtarı** (`Idempotency-Key`) | Yazan isteklerde aynı anahtar + aynı istek aynı cevabı döner; yarım kalmış istek körlemesine tekrar çalışmaz. Kullanıcıya özel; çalışırken 24 saat, bitince 7 gün (`IdempotencyMiddleware.cs:73-130`). |
| **Dış etki kaydı** | Pazaryerine yapılan her yazma önce kaydedilir; sonucu belirsiz kalırsa 2 dk'da bir süpürme **Gönderim Sonucu Doğrulama** işi açar; doğrulanamazsa "çözülemedi" kalır (`Worker.cs:665-676`). |

### 6.8 Sağlık izleme, yedekler, güvenlik

**Sağlık uç noktaları**: `/health/live` (süreç ayakta), `/health/ready` (veritabanı), `/health/queues` (kuyruk; 5 dk'dan eski
süresi geçmiş kira ya da 10 dk'dan uzun bekleyen çalıştırılabilir iş varsa 503; kilit/kota yüzünden bekleyenler sayılmaz)
(`JobQueueHealthCheck.cs:8-43`).

**Healthwatch** (`deploy/scripts/healthwatch.sh`): 5 dk'da bir, gün boyu; açılıştan sonraki ilk 10 dk atlanır. Kontroller:
ready, queues, Worker sağlığı, disk ≥ %85, veritabanı bağlantıları ≥ %80, en yeni yedek 30 saatten eski. API iki ardışık turda
sağlıksız ve veritabanı sağlıklıysa API yeniden başlatılır (en çok 30 dk'da bir). Alarm iki ardışık başarısız turdan sonra,
sürdükçe saatte bir, düzelince bir "Recovered".

**Yedekler**

| Konu | Kural |
|---|---|
| Zamanlanmış yedek | Günde iki kez **11:59 ve 23:59 İstanbul**; kaçarsa açılışta telafi (`deploy/systemd/ravencia-backup.timer`) |
| Deploy yedeği | Her deploy'da taze, doğrulanmış set |
| İçerik | Veritabanı dökümü, yüklenen dosyalar + şifreleme anahtarları, SHA256SUMS, manifest; yazıldıktan sonra doğrulanır |
| Saklama | En yeni set her zaman; son 72 saatin en yeni 5 seti; 7 gün boyunca her yarım günün en yenisi; 4 hafta haftalık; 6 ay aylık (`deploy/backup/backup.sh:19-124`) |
| Disk koruması | Yeni set diski %85'i geçirecekse önce en eski haftalıklar silinir; %95 üstünde yedek reddedilir |
| Geri yükleme tatbikatı | Ayın ilk pazarı 22:00 İstanbul: en yeni seti yalıtılmış geçici konteynerlerde açar; canlıya dokunmaz |
| Sunucu dışı kopya | `deploy/backup/offsite-copy.sh`: en yeni seti sahibin açık anahtarıyla şifreleyip (ör. Google Drive'a) yükler. Zamanlayıcı birimleri depoda hazır (`deploy/systemd/ravencia-offsite-copy.service` ve `.timer`, 09:30 ve 21:30 UTC); `install-backup-timer.sh` bunları **yalnız** `deploy/secrets` altında açık anahtar ve uzak hedef dosyası doluysa kurar. Sahip bu iki dosyayı koymadığı için sunucuda **kurulu değil** (`docs/CI-CD-DEPLOY.md` "Sunucu dışı yedek"; sahip adımı) |
| Başarısızlık | Yedek ya da tatbikat düşerse alarm gider |

(canlı, 12 Kas 22:10Z) Zamanlayıcılar doğru; disk %49 (94 / 193 GB). Yedek klasöründe yalnız 5 set var (hepsi 12 Kas
20:26–21:58Z, 156–197 MB) — saklama kuralına göre önceki yarım günlerin, haftaların ve ayların setleri de durmalıydı;
silinmeleri 12 Kas 11:36–18:02Z arasında olmuş; sahip eski setleri kendisi silmiş olabileceğini bildirdi (13 Kas; konu kapandı). `backup.sh` yorumu artık
gerçek boyutu söyler (set 0,3–0,5 GB, disk 193 GB). Aylık bakım: ayın ilk pazarı 04:00 İstanbul, paket güncellemesi.

**Günlükler**: üretimde konteynerler journald'a yazar, deploy'larda kaybolmaz (önerilen 2 GB / 30 gün). Okuma yalnız
salt okunur kullanıcıyla (`/prod-logs`).

**Uygulama güvenliği**: roller ve izinler 7.5'te; oturum kuralları 1.4'te; iki adımlı doğrulama 7.1'de. Hız sınırları: giriş
dakikada 10, ikinci adım/şifre değişikliği dakikada 10, genel ağ başına dakikada 1800. Bir ağdan bir hesaba 5 yanlış → o ağ o
hesap için 15 dk durur, her yeni blokta iki katı, en çok 1 gün; daha önce başarıyla girilmiş ağ etkilenmez
(`SignInThrottle.cs:5-19`).

**CrowdSec** (saldırı engelleyici): sunucuda SSH ve Caddy günlüklerini izler. **Kurulumdan beri simülasyon modunda**: uyarı
kaydeder, hiçbir IP'yi engellemez. Sahibin IP'si dinamiktir (ev ve mobil hat), bu yüzden izin listesi **IP'ye göre değil, isteğin
şekline göre** kurulur: **panel beyaz listesi** (`ravencia/panel-whitelist`, ayrıştırıcı) panelin kendi API okumalarını,
SignalR'ı, derlenmiş dosyalarını, simgelerini ve sayfa adreslerini HTTP senaryolarından düşürür; `.env`, `.git`, `*.php`, `/wp-*`
gibi tarama yolları ve API'ye her `POST`/`PUT`/`DELETE` görünür kalır. 12 Kas 18:20Z'de sahibin tarayıcısının (TürkNet,
159.146.51.200) aldığı `http-probing` (simul)ban'ının nedeni, silinmiş bir ürünü yoklayan açık sayfanın 404'leriydi.
Beyaz liste **12 Kas 23:54Z'de simülasyon modunda kuruldu** (sahibin bildirimi); regex RE2 sınırına uyduruldu
(`deploy/crowdsec/ravencia-panel-whitelist.yaml`; `deploy/scripts/install-crowdsec.sh`; çevrimdışı test
`tests/ops/test-crowdsec-whitelist.sh`). **Sahip kararı (13 Kas):** `--enforce` **2–3 sessiz günden sonra** çalıştırılır; bu
sürede `cscli alerts list`'te sahibin IP'siyle yeni `http-*` uyarısı çıkmamalı, çıkarsa yol kuralı eklenir, IP değil. Enforce
yine **sahibin adımıdır**: `bash deploy/scripts/install-crowdsec.sh --enforce` simülasyonu kapatır, topluluk blok listesini açar
ve bir işaret dosyası bırakır; deploy bu betiği çalıştırmaz, geri dönüş yolu belgededir (`docs/CI-CD-DEPLOY.md` "CrowdSec").
(canlı, 13 Kas: `global simulation: enabled`; 159.146.51.200 için 12 Kas 18:20Z'den kalma (simul)ban kararı durur.)

### 6.9 Dosyalar ve saatlik kalıntı temizliği

- Yüklenen dosyalar (ürün görselleri, fatura belgeleri, etiketler) hesap klasöründe tutulur; klasör dışına çıkan yol
  reddedilir (`PrivateFileStorage.cs:14-60`).
- **Saatlik kalıntı temizliği** (API, açılıştan 2 dk sonra ve saatte bir; `PurgedFileRetentionService.cs:7-31`); her adım
  ayrı çalışır, biri düşerse diğerleri sürer:
  1. **Sahipsiz dosyalar**: bilinen türden, 24 saatten eski ve hiçbir kaydın göstermediği dosya silinmek üzere işaretlenir
     (`FileAssetReferences.cs:27,66-79`).
  2. **Silinmek üzere işaretli dosyalar** diskten ve satırıyla birlikte kaldırılır; 15 dk güvenlik payı, tur başına 200; silmeden
     önce başka sahibi olup olmadığı yeniden denetlenir (`PurgedFileRetention.cs:17-28`).
  3. **Değiştirilmiş eski anahtarlar** silinir.
  4. **Silinmiş bağlantıların** kalan ayarları ve satırı kaldırılır (2.7).
- Worker'ın saatlik iş temizliği **bildirim merkezinin** 7 günden eski satırlarını da siler (`JobRetentionService.cs:225`; `Worker.cs:588`; 6.10).
- Silinen ürünlerin görselleri ve beden tabloları, Tam Sıfırlama ve panel sıfırlamasının bıraktığı dosyalar da bu yolla gider.

### 6.10 İşlem Takibi sayfası

- Ana sekmeler: **İşler**, **Loglar**, **API Sağlık**, **Kuyruk ve Kota**, **Bildirimler** (`operations/pages.tsx:53,590-596`).
- **İşler kategori sekmeleri** (`job-categories.ts:33-41`): Tüm İşlemler; Sipariş/Kargo (sipariş, paket, kargo, etiket,
  webhook; fatura işleri hariç); Fiyat/Stok; Ürün/Yayın (ürün, katalog, içe aktarma, kategori, marka; soru eşitlemesi de
  burada); Faturalar (fatura, E-Faturam, finans, hakediş; paket fatura kontrolü ve Fatura Süresi Taraması da); İadeler; Sistem
  & Test (test, yetki kontrolü, tarama, sıfırlama — Ayarlar › Veri'nin `PANEL_FACTORY_RESET` işi de burada, "Panel Verisi
  Sıfırlama" adıyla — gönderim sonucu doğrulama).
- Süzgeçler: **Zaman Aralığı**, **Yön** (Okuma / Yazma / Bildirim / Panel İçi), durum çipleri ve ayrıntılı **Durum** (6.3).
  Filtreler ve sayfa adreste tutulur; geri tuşu ve paylaşılan bağlantı aynı görünümü açar (`jobs-url-state.ts:5-7`).
- **Loglar**: **Ürün Hareketleri** (stok, fiyat, durum, iade hareketleri; 3.14) ve **Pazaryeri Gönderimleri** (pazaryerine
  gönderilen fiyat ve stok satırları) (`OperationLogs.tsx:12-19`).
- Bazı düğmeler yalnız Sahip ve Yönetici'ye açılır (`operations/pages.tsx:425`).
- **Kuyruk ve Kota** (sahip kararı, 13 Kas 2026; salt okunur, 30 sn'de bir yenilenir): worker şeritlerinin doluluğu (çalışan iş / yuva,
  yuvayı bekleyen), seçilen pencerenin işleri duruma ve iş tipine göre, her mağazanın pazaryeri kotasının son saatteki kullanımı
  (`QueueQuotaView.tsx`; uçlar `/jobs/metrics`, `/jobs/metrics/lanes`, `/jobs/metrics/quota`, `/jobs/counts`:
  `JobEndpoints.cs:32-46`).
- **Bildirimler** (bildirim merkezi; sahip kararı, 13 Kas 2026): zilin gösterdiği **her bildirim** sunucuda **7 gün** saklanır;
  zilden temizlenenler "Zilden Kaldırıldı" etiketiyle, hesap geneli uyarılar "Herkese" etiketiyle kalır. Yeniden eskiye, sayfada
  50 kayıt; kategori, önem, tarih, durum (Okunmuş/Okunmamış) ve arama süzgeçleri vardır; ilk sayfa 30 sn'de bir yenilenir.
  Salt Okunur dahil her rol bildirimleri okur; yazma ve okundu işaretleme çalışan rollerindir. 7 günü geçen satırı Worker'ın
  saatlik temizliği siler (`NotificationCenter.tsx`; `NotificationEndpoints.cs:12-40`; `PanelNotificationContracts.cs:8`;
  `JobRetentionService.cs:225`; `RoleAuthorizationMiddleware.cs:116`).

---

## 7. Sistem Ayarları, Kullanıcılar, Bildirimler ve Veri Temizliği

**Sistem Ayarları** altı grup sekmesinden oluşur; bir grubun birden çok bölümü varsa grup içinde ikinci sekme satırı çıkar.
Açık bölüm adreste `?tab=` ile tutulur; bilinmeyen değer Authenticator'ı açar (`SystemSettingsPage.tsx:41-60`).

| Grup | Bölüm (`tab`) | Kim görür / değiştirir |
|---|---|---|
| Güvenlik ve Oturumlar | Authenticator (`auth`), Oturumlar (`sessions`), Alarm Bildirimleri (`alerts`) | Authenticator ve Oturumlar: herkes, kendi hesabı için. Alarm Bildirimleri: Sahip ve Yönetici |
| Kullanıcılar | Kullanıcılar (`users`) | Yalnız Sahip ve Yönetici (okuma dahil) |
| Veri | Veritabanı Temizliği (`database`) | Sahip ve Yönetici |
| Yapay Zekâ | Yapay Zekâ (`ai`) | Sahip ve Yönetici |
| Kargo Ayarları | Gönderici ve Yazdırma (`sender`), Etiket Ölçüleri (`dims`), Etiket Tasarımı (`designer`) | Okuma herkes; kaydetme Sahip ve Yönetici |
| Görünüm | Okunabilirlik ve Tema (`read`), Renk Paleti (`palette`), Kayıtlı Temalar (`themes`), Bildirimler (`notify`) | Okuma herkes; kaydetme Sahip ve Yönetici |

Ayarlar **hesap düzeyinde** saklanır (`SettingsEndpoints.cs:11-13,86-92`). Kaydetme sürüm kontrolüyle yapılır; başka bir
oturum araya kaydettiyse "Ayarlar başka bir oturumda değişti" (`SettingsEndpoints.cs:359-378`). (canlı: `ai` v4,
`alert-email` v1, `appearance` v64, `shipping-label` v4, `orders-resend-reminder` v3.)

### 7.1 Güvenlik ve Oturumlar

**Authenticator** (iki adımlı doğrulama, TOTP — telefon uygulamasının ürettiği 6 haneli kod): kurulum üç adım.

1. **Parola**: Mevcut Parola; yalnız bu kurulum için doğrulanır, saklanmaz.
2. **QR Kod**: Google Authenticator, Microsoft Authenticator ya da uyumlu uygulamayla taranır. Okunamazsa **Kurulum
   Anahtarını Elle Göster**. Ardından 6 haneli kod.
3. **Kurtarma Kodları**: **yalnız bir kez** gösterilir; pencere Esc ya da dışına tıklamayla kapanmaz, yalnız **Kodları
   Sakladım** ile. **Kopyala** panoya alır.

Doğrulama açıkken "Hesabınız Korunuyor" ve kalan kurtarma kodu sayısı görünür. **Panelde doğrulamayı kapatma düğmesi yok.**
Yanlış kod oturum içinde sayılır, 5. yanlışta oturum kapanır (`MfaAttemptPolicy.cs:5-11`). Yükseltilmiş rollerde doğrulama
zorunluluğu ayarı varsayılan **kapalı** (`ElevatedRoleMfaMiddleware.cs:5-31`). (canlı: tek kullanıcı, Sahip; TOTP kurulumu
tamamlanmamış.)

**Oturumlar**: aktif oturumlar ("Bu Cihaz" en üstte; son etkinlik ve bitiş zamanı İstanbul saatiyle) ve ayrı listede kapalı
oturumlar. İşlemler: **Oturumu Sonlandır**, **Diğer Tüm Oturumları Kapat**, **Kaydı Sil**, **Kapalı Oturumları Sil**; hepsi
onay penceresiyle. Sonlandırmak o cihazın bağlantısını keser; kaydı silmek yalnız geçmiş satırını kaldırır. (canlı: 2 aktif.)

**Alarm Bildirimleri** (`SystemSettingsPage.tsx:1180-1265`): sunucunun **sağlık izleme, yedek, deploy ve geri alma**
alarmları bir Gmail hesabından e-postayla gönderilir.

- **Gönderen Gmail Adresi**; **Uygulama Şifresi** (Google'ın 16 harflik uygulama şifresi, hesap şifresi değil; sunucu hiç geri
  göndermez, ekranda "Kayıtlı" ve **Değiştir**); **Alıcılar** (en çok 5).
- **Test E-postası Gönder** yalnız kaydedilmiş ayarlarla çalışır.
- Sunucu bu ayarların bir kopyasını tutar; panel çökse de alarm gider. Panel ayarı tek doğru kaynaktır; her alarmdan önce ve
  her deploy'da eşitlenir (`deploy/scripts/alert-settings-sync.sh`). Alarmı üretenler: sağlık izleme, başarısız yedek ya da
  tatbikat, başarısız/geri alınmış deploy (`deploy/scripts/send-alert.sh`). (canlı: ayar kayıtlı, 3 Kas.)

### 7.2 Veri › Veritabanı Temizliği


Bütün sayfa (kategori sayıları, pazaryerleri listesi ve sıfırlama) yalnız **Sahip ve Yönetici**'ye açıktır; diğer roller
"Veritabanı temizliğini yalnız hesap sahibi ve yöneticiler görür ve yapar." uyarısını görür ve sayfa başka bir şey sormaz
(`SystemSettingsPage.tsx` `forbidden`; `RoleAuthorizationMiddleware.cs:72,79`). Üç parça var. Kategori silme ve pazaryeri
sıfırlaması alt çubuktaki onay kelimesiyle çalışır: bazı veri türleri seçiliyse **Verileri sil**, **bütün veri türleri**
seçiliyse **PANELİ SIFIRLA**; Paneli Tamamen Sıfırla'nın kendi onay kelimesi de **PANELİ SIFIRLA**'dır.

**a) Kategori grupları** (19 kategori, `PanelFactoryResetInventory.cs:29-56`). Bağımlılık: Siparişler seçilince Faturalar da
seçilir; Ürünler seçilince Stok ve Fiyatlar da seçilir; bir kategori kaldırılınca ona bağlı olanlar da kalkar.

| Bölüm | Kategori | Silinenler (özet) |
|---|---|---|
| Satış ve Faturalar | Faturalar ve Belgeleri | Faturalar, satırları, gönderim denemeleri, pazaryerine teslim kayıtları, elle yüklenen fatura belgeleri, gizlenen paket listesi |
| | Siparişler, Paketler ve İadeler (Faturalar'ı da seçer) | Siparişler, satırlar, durum geçmişi, paketler, gönderi belgeleri, iade talepleri, kararları, kanıtları, iade stok işlemleri |
| | Müşteri Soruları | Sorular ve soru eşitleme durumu |
| | Soru Cevap Şablonları | Hazır cevaplar |
| | Finans, Hakediş ve Kargo Faturaları | Finans hareketleri, hakediş siparişleri, kargo faturası kalemleri, finans eşitleme durumu |
| Katalog | Stok ve Fiyatlar | Stok kalemleri ve hareketleri, rezervasyonlar, kanal teklifleri ve fiyatları, fiyat geçmişi |
| | Ürün Seçenekleri | Seçenek grupları, değerleri, varyant bağları |
| | Ürünler (Stok'u da seçer) | Ürünler, varyantlar, görseller, özellik atamaları, beden tabloları, pazaryeri bağları, ilan profilleri, içe aktarma oturumları. Sipariş satırları metnini korur, yalnız varyant bağı boşalır. |
| | Kategori Özellikleri | Özellik başlıkları ve değerleri, kategori gereksinimleri, özellik/değer eşlemeleri, eşleme şablonları |
| | Varsayılan Özellik Değerleri | Bağlantıların sabit özellik değerleri (Platform Varsayılanları; ör. Menşei) |
| | Kategori Listesi | Kategori ağacı ve eşlemeleri; ürünlerin kategori bağı boşalır |
| | Marka Listesi | Markalar ve marka eşlemeleri; ürünlerin marka bağı boşalır |
| Pazaryeri Kayıtları | Pazaryeri Referans Listeleri | İndirilen kategori, özellik ve marka listeleri |
| | Promosyon, Komisyon ve Reklam Dosyaları | Komisyon tarifeleri, promosyon dosyaları, reklam raporları |
| Sistem Kayıtları | İşlem Takibi | Bütün işler ve denemeleri (çalışan sıfırlama işi hariç), mutabakat, dış işlem kayıtları, **değişiklik olayları** (iç olaylar; eskiden Bildirimler'deydi, `PanelFactoryResetInventory.cs:156`), **eşitleme imleçleri** (sonraki eşitleme baştan başlar), bağlantı beklemeleri (`PanelFactoryResetService.cs:229-239`) |
| | Loglar | İşlem ve denetim logu (**hesaba bağlanamamış giriş denemeleri** dahil: kiracısız kayıtlar, kullanıcısı yalnız bu hesabın üyesiyse; kullanıcısı bilinmeyenler ancak tek hesap varsa), API istek kayıtları, **yapay zekâ kullanım kaydı**, ürün etkinlik logu, fiyat/stok gönderim logu, gelen webhook kayıtları |
| | Bildirimler | Sorun kayıtları ve alarmlar, **bildirim merkezi** (`ops.panel_notifications`); iş bitince **bu tarayıcıdaki** zil geçmişi de silinir (`PanelFactoryResetInventory.cs:50,173`) |
| | Raporlar ve Özet Tabloları | Pano özeti, günlük ciro, düşük stok, eşitleme durumu; sonra yeniden hesaplanır |
| | Eski Bağlantı Kayıtları ve İptal Edilmiş Anahtarlar | Silinmiş bağlantıların kalan ayar satırları ve satırın kendisi, iptal edilmiş anahtarlar (`PanelFactoryResetService.cs:241-279`). 12 Kas'tan beri bunları saatlik temizlik de kendiliğinden siler; kategori çoğunlukla boş gelir. |

**Nasıl çalışır** (`PanelFactoryResetService.cs:12-17,104-200`):

- Tek bir `PANEL_FACTORY_RESET` işi kuyruğa girer; önce hesabın bekleyen işlerini iptal eder, çalışanların bitmesini en çok 2
  dk bekler (bitmezse 30 sn sonra yeniden dener). Sürerken hesabın başka hiçbir işi başlamaz.
- Her bağlantının eşitleme kilitleri alınır; tablolar 20.000 satırlık partilerle, önce alt sonra üst tablolar silinir.
- Silinen kayıtların kullandığı ve başka hiçbir kaydın kullanmadığı dosyalar işaretlenir; saatlik temizlik siler.
- **Pazaryerine ve e-fatura sağlayıcısına hiçbir şey gönderilmez.**
- Sıfırlama **kendi izlerini kalan kayıt saymaz**: kendi işi ve denemesi, tek denetim satırı, çıkardığı olaylar ve yeniden
  kurulan boş pano özeti sayılan "kalan" kayıtlara girmez; kiracısız giriş kayıtları Loglar'la silinir
  (`PanelFactoryResetService.cs:166,248-256,258-270`).
- Sonuç denetim loguna tek satır yazılır. Yarıda kalan iş yeniden çalışınca kaldığı yerden devam eder. İlerleme sayfanın
  altındaki "Sıfırlama İlerlemesi" alanında.
- **Bütün veri türleri seçilirse** bu Paneli Tamamen Sıfırla'dır: bu oturum dışındaki **bütün oturumlar kapanır**, dosyaların
  tamamı silinir. Sayfa bunu onay kutusundan önce kalın uyarıyla söyler ("Bütün veri türleri seçili: bu, Paneli Tamamen Sıfırla
  demektir…") ve **PANELİ SIFIRLA** ister; sunucu bu durumda "Verileri sil" kelimesini reddeder
  (`PanelFactoryResetService.cs:63-65,112`; `panel-factory-reset.ts` `dataResetConfirmWord`).
- Listede olmayan bir tablo varsa sıfırlama durur (`PANEL_RESET_UNCLASSIFIED`); sistem tahmin yürütmez.

**b) Pazaryerleri (toplu Tam Sıfırlama).** Silinmemiş ve gizlenmemiş her pazaryeri bağlantısı listelenir; E-Faturam listede
yoktur (onun kayıtları faturalardır). Pasif bağlantının satırında "Bağlantı pasif." yazar. Seçilen bağlantının Tam
Sıfırlaması 2.7'deki listeyi siler (**eşleme şablonlarını ve Platform Varsayılanları'nı da**; kural 10.6); bağlantı ayarları korunur. Satırdaki sayı ürün bağlarını toplama katmaz. Bağlantının
Tam Sıfırlama sayıları ve ilerlemesi de yalnız Sahip ve Yönetici içindir (`RoleAuthorizationMiddleware.cs:72`). Sayfadaki
bilgi kutusu artık "Yukarıdaki veri türleri bütün pazaryerleri için birlikte silinir; yalnız bir pazaryeri için Pazaryerleri
bölümünü ya da bağlantının Verileri Sıfırla menüsünü kullanın" der.

- Önce pazaryerleri sıfırlanır, **sırayla**, her biri bitene kadar 2,5 sn'de bir izlenerek; **biri başarısız olursa** kalanlar
  ve kategori silme hiç başlamaz; sonra kategori silme başlar. Sayfadan çıkılırsa izleme durur, başlamış sunucu işi sürer
  (`marketplace-bulk-reset.ts:217-246`).
- (canlı: 12 Kas'ta 6 `CONNECTION_FULL_RESET` başarılı.)

**c) Paneli Tamamen Sıfırla** (sayfanın en altı, `PanelFactoryResetSection.tsx`):

- Onay kelimesi **PANELİ SIFIRLA**. Türkçe büyük harfe çevrilerek karşılaştırılır; baş/son ve çift boşluklar yok sayılır:
  "paneli sıfırla" kabul edilir, noktasız "PANELI SIFIRLA" reddedilir (`PanelFactoryResetService.cs:45-48`).
- 19 kategorinin tamamını seçmekle aynı iştir (sunucu ikisini ayırmaz). **Silinenler** listesi kategori sayılarını gösterir.
- **Kalanlar** listesi: "Hesap, kullanıcılar ve bu oturum", "Entegrasyon bağlantıları ve giriş bilgileri", "Sistem ayarları"
  (`PanelFactoryResetInventory.cs:85-90`).
- Uyarılar: **Geri alınamaz** notu sade metindir (yalnız uyarı simgesi, sarı bant yok; `SystemSettingsPage.tsx:249`; `PanelFactoryResetSection.tsx:97`; `settings-sade.css`); bu oturum açık kalır, diğer cihazlardaki oturumlar kapanır. **Açık bağlantılar sıfırlamadan sonra
  pazaryerinin izin verdiği tarih aralığındaki siparişleri yeniden çeker**; hiç veri gelmesin isteniyorsa önce bağlantılar
  kapatılmalı.

**d) İşleyiş ayarları veri değildir** (sahibin kuralı). Aşağıdakiler hiçbir sıfırlamada silinmez ve Kalanlar listesinde de
anılmaz (`PanelFactoryResetInventory.cs`):

| Ayar | Tablo |
|---|---|
| Stok kuralları | `inventory.connection_inventory_policies` |
| Depo tanımları ve depo eşleşmeleri | `inventory.inventory_locations`, `inventory.connection_location_mappings` |
| Kargo firması eşleşmeleri | `sales.cargo_provider_mappings` |
| Eşitleme kuralları (akışlar) | `integration.connection_sync_policies` |
| Yetenek ve webhook kaydı | bağlantı kayıtları |
| Fatura kuralları | `billing.invoice_policies` |
| Excel kolon profilleri | `catalog.import_column_profiles`, `catalog.import_column_mappings` |

İstisna: **silinmiş** bir bağlantının bu ayarları, bağlantıyla birlikte kalkar (Bağlantıyı Sil ve saatlik temizlik; 2.7). Açık
ve pasif bağlantıların ayarlarına dokunulmaz.

### 7.3 Yapay Zekâ ve Kargo Ayarları

**Yapay Zekâ** (`SystemSettingsPage.tsx:1267-1435`; `AiEndpoints.cs`):

- **Claude API Anahtarı**: hesabın kendi Anthropic anahtarı; şifreli saklanır, gösterilmez ("Kayıtlı" / **Değiştir**). Anahtar
  değişirse eski anahtarla yüklenen beceriler unutulur; **Beceriyi Yükle** ile yeniden yüklenmeli.
- **Varsayılan Model** (fiyat 1 milyon token başına girdi / çıktı): Claude Sonnet 5.5 (varsayılan) 2 $ / 10 $; Claude Opus 5.5
  4 $ / 20 $; Claude Haiku 5.5 0,10 $ / 0,50 $ (`ClaudeService.cs:18-25`). Her özellik bu tek modeli kullanır. (canlı:
  `claude-sonnet-5-5`.)
- **Bağlantıyı Sına**: kayıtlı anahtar ve modelle küçük bir istek gönderir.
- **Beceriler** (Claude'a yüklenen talimat paketi): şimdilik yalnız **Beden Tablosu** (3.7). **Beceriyi Yükle** / **Beceriyi
  Güncelle** (`.skill` ya da `.zip`, en çok 10 MB); **Panelle Gelen Sürümü Yükle** yalnız hiç yükleme yapılmadıysa. Panelle
  gelen kopya koddakinden eskiyse "Güncel Değil".
- Beden tablosu üretimini İşletme rolü de yapabilir; ayarları yalnız Sahip ve Yönetici görür.
- **Kullanım Kaydı** (son 50 çağrı): Tarih, Özellik (Bağlantı Sınaması, Beceri Yükleme, Beden Tablosu), Kişi, Konu, Token,
  Maliyet, Durum (Başarılı / Başarısız / İptal Edildi). **Maliyet tahminidir**: liste fiyatı × token; Anthropic'in ayrıca
  faturaladığı kod çalıştırma süresi dahil değildir. Başarısız çağrı da ücret yazar. "Loglar" kategorisiyle silinir. (canlı: 7
  çağrı; 12 Kas'ta 3 başarısız Beden Tablosu çağrısı toplam 0,2604 $.)

**Kargo Ayarları** (`SystemSettingsPage.tsx:1075-1172`):

- **Gönderici ve Yazdırma**: Gönderici Adı, Varsayılan Kargo Çıktısı (A4 / Sticker), Gönderici Adresi, A4 Sayfa Düzeni (1, 2
  ya da 4 etiket), **A4 Yazdırma Butonunu Göster** / **Sticker Yazdırma Butonunu Göster**.
- **Etiket Ölçüleri**: Sticker genişlik/yükseklik 40–300 mm; Bloklar Arası Boşluk 0–20 mm; **Müşteri İletişim Alanını Göster**.
- **Etiket Tasarımı**: sürükle-bırak tasarım alanı. Bloklar: Takip Barkodu, Teslimat Adresi, Paket / Sipariş Bilgileri, Paket
  Barkodu, Gönderici ve Kargo Bilgileri, Özel İçerik Bloğu. Blok ayarları: hizalama, başlık biçimi, satırda 1–3 alan, çerçeve,
  yazı boyutu, büyük harf, **Yazıyı Kutuya Sığdır**, barkod numarası, döndürme. Klavye: Tab ile blok seçilir, oklarla taşınır
  (Shift ile 5 kat). **Düzeni Toparla**; **Varsayılanı Yükle** (Sticker için Trendyol Express 100 × 100 mm'ye benzer).
  Taşma riskinde "Baskıda Sorun Çıkabilir". Kayıt düğmesi **Ayarları Kaydet** (hesaba yazılır).
- **Kenar durum**: **Şablon Kaydet** ile kaydedilen tasarım şablonları **yalnız bu tarayıcıda** tutulur; başka cihazda
  görünmez. Hesapta kargo ayarı yoksa tarayıcıda kalmış eski kopya bir kez sunucuya aktarılır; Sahip ya da Yönetici değilseniz
  bu aktarma **sessizce başarısız olur** (`shipping-settings.ts:33-52`).

### 7.4 Görünüm ve Bildirimler (zil)

**Görünüm** (`SystemSettingsPage.tsx:554-781`):

- **Okunabilirlik ve Tema**: Font Tipi (Inter, Sistem Yazı Tipi, Segoe UI, Arial); Yazı Boyutu (Çok Küçük … Ekstra Büyük);
  Tema (Ravencia — Aydınlık, Ravencia — Grafit, kayıtlı temalar). Değişiklikler önce **önizleme**dir; **Görünüm Ayarlarını
  Kaydet** gerekir, kaydetmeden çıkılırsa eski görünüm döner.
- **Renk Paleti**: 20 renk değişkeni; düzenlenen palet o an seçili temanınkidir.
- **Kayıtlı Temalar**: ad (en çok 60 karakter) + **Renk Temasını Kaydet** — **anında** hesaba yazılır. Varsayılan tema silinemez.
- **Bildirimler**: Başarılı, Bekliyor, Başarısız örnekleri; bu örnekler geçmişe yazılmaz.
- Font, boyut, renkler ve kayıtlı temalar **hesap düzeyindedir**; açık/koyu tema seçimi **tarayıcı başınadır**.

**Üç ayrı bildirim türü vardır, karıştırılmamalı:**

1. **Zil (Bildirim geçmişi)** — görünen liste tarayıcıda tutulur (`notifications.ts:134-139`); zilin gösterdiği her bildirimin, okundu ve temizlendi bilgisinin bir kopyası ayrıca sunucuya gider ve **7 gün** **İşlem Takibi › Bildirimler**'de durur (6.10); zilden Temizle yalnız zilden kaldırır:
   - Hesap ve kullanıcı başına ayrı; başka cihazda görünmez; en çok **20** kayıt; 5 dakika içinde tekrarlanan aynı bildirim
     tek kayıtta birleşir ("N×").
   - Yazılanlar: her hata; sonuç doğuran başarılar (gönderim, iade kararı, fatura, etiket, kargo, paket, stok, yayın, silme,
     oturum, eşitleme, aktarım). Form doğrulama, önizleme, kopyalama ve "kuyruğa alındı" gibi ilerleme mesajları yalnız ekranda
     görünür.
   - Panel: Tümü / Okunmamış / Hatalar; Bugün, Dün ve eski günler; **Tümünü Okundu Yap**, **Temizle**, her kayıtta **Okundu** ve
     **Ayrıntı**. Hedefe gitmek bildirimi okundu yapar (`NotificationPanel.tsx:80-109`).
   - **Sunucudan gelen dikkat bildirimleri** 5 dakikada bir okunur (`App.tsx:98-99`). Veritabanına yazılmaz, her okumada
     durumdan hesaplanır (`AttentionNoticeService.cs:8-10`). İki türdür: (1) aktif bir bağlantıda bir okuma **art arda 3 kez**
     başarısız olduğunda — **Entegrasyona Git**; (2) ürün onay kontrolünün süresi dolduğunda — **Ürüne Git** ya da **İşlem
     Takibine Git**. Neden ortadan kalkınca kendiliğinden kaybolur; temizlenen bildirim, sorun yeniden başlamadıkça çıkmaz.
2. **Operasyonel sorunlar** (sunucuda kayıtlı sorunlar; Açık / Kabul Edildi / Çözüldü): **zile düşmez**; ilgili ekranlarda
   görünür (webhook paneli, fiyat-stok izleme, iş ayrıntısındaki hata nedenleri). 30 gün görülmeyen açık sorun çözüldü sayılır;
   çözülmüş sorun 30 gün sonra silinir (`OperationalIssueRetentionService.cs:6-11`). (canlı, 12 Kas: 1311 açık, 9933 çözülmüş;
   başta `PRODUCT_WEBCOLOR_MAPPING_REQUIRED` 734, `ORDER_LINE_VARIANT_UNMATCHED` 550.)
3. **Alarm** — sunucunun e-postayla (ya da Telegram/Slack ile) gönderdiği operatör uyarısı (7.1). Zil ile bağlantısı yoktur.

### 7.5 Kullanıcılar, roller, izinler

- **Roller**: Sahip (`OWNER`), Yönetici (`ADMINISTRATOR`), İşletme (`OPERATIONS`), Muhasebe (`ACCOUNTING`), Salt Okunur
  (`READONLY`) (`RoleAuthorizationMiddleware.cs:143-151`).
- **Kullanıcı yönetimi: Ayarlar › Kullanıcılar** (sahip kararı, 13 Kas 2026; **yalnız Sahip ve Yönetici**, okuma dahil;
  `SystemSettingsPage.tsx:45,538`; `RoleAuthorizationMiddleware.cs:83`). Listede ad, e-posta, rol, iki adımlı doğrulama, son giriş
  ve durum görünür. İşlemler: **ekleme** (yeni hesap tek seferlik parola alır, ilk girişte değiştirilir; var olan hesap çalışma
  alanına katılır; e-posta daveti gönderilmez), rol değiştirme, parola sıfırlama (yeni tek kullanımlık parola, oturumlar kapanır),
  iki adımlı doğrulamayı sıfırlama, Pasife Al / Etkinleştir ve oturumları kapatma. Her değişiklik çağıranın parolasını (ve
  doğrulama açıksa kodunu) yeniden ister ve denetim loguna yazılır. Korumalar: kimse kendi hesabını burada değiştirmez, Yönetici
  Sahip'e dokunamaz ya da Sahip yapamaz, son etkin Sahip'in rolü düşürülemez ya da pasife alınamaz
  (`UserManagementEndpoints.cs:49-56,111,152,247,256`). Sahip hesabı ilk kurulumda zorunlu parola değişikliğiyle oluşturulur
  (`BootstrapService.cs:47-53`).

| İşlem | İzinli roller | Kaynak (`RoleAuthorizationMiddleware.cs`) |
|---|---|---|
| Her okuma | Bütün roller | `:51-56` |
| Fatura PDF ve belgelerini açma | Sahip, Yönetici, İşletme, Muhasebe | `:64` |
| Etiket ve gönderi belgeleri, içe aktarma hata dosyası | Sahip, Yönetici, İşletme | `:66-67` |
| Pazaryerini canlı sorgulayan okumalar | Salt Okunur hariç hepsi | `:68-72` |
| Alarm Bildirimleri, Yapay Zekâ, Kullanıcılar, Paneli Tamamen Sıfırla (okuma dahil) | Sahip, Yönetici | `:74-83` |
| Bildirim merkezi (okuma, okundu, zilden kaldırma) | Çalışan roller yazar; Salt Okunur okur | `:116` |
| Sipariş işleri (işleme alma, paket vb.) | Sahip, Yönetici, İşletme | `:107-108` |
| "Etiket yazdırıldı" kaydı | Sahip, Yönetici, İşletme, Muhasebe | `:107-108` |
| Ürün, katalog, stok, eşleştirme, iade ve soru işleri | Sahip, Yönetici, İşletme | `:109-110,129-130` |
| Toplu Değer Ata | Sahip, Yönetici | `:129-131` |
| Hızlı Güncelle seçiminin kaydı (`/settings/platform-update`) | Sahip, Yönetici, İşletme | `:132-134` |
| Fatura kesme, iptal, pazaryerine iletme, link kaldırma | Sahip, Yönetici (sahip kararı, 6 Kas) | `:92-99` |
| Fatura önizlemesini onaylama, elle yüklenen faturanın linkini gönderme | Sahip, Yönetici | `:103-104` |
| Diğer fatura işleri | Sahip, Yönetici, Muhasebe | `:90-104` |
| Bağlantı oluşturma, kimlik değiştirme, test siparişi, veri sıfırlama ve Tam Sıfırlama, webhook kaydı, dış yazma anahtarları | Sahip, Yönetici | `:111-123` |
| Diğer bağlantı değişiklikleri | İşletme de | `:111-123` |
| Eşlenmeyen her yazma (ayarlar ve İşlem Takibi'nde yeniden dene/iptal dahil) | Sahip, Yönetici | `:131-132` |

---

## 8. Terimler Sözlüğü

Ekranda geçen ad kalın yazılıdır. "Kaçınılacak" sütunu, aynı şey için gevşek ya da yanlış kullanılan adları verir. Birden
çok ekran adı olan terimlerde hepsi yazılmıştır; 12 Kas'taki ad birleştirmelerinden sonra çoğu terimin tek adı vardır.

**Panel, bağlantı, eşitleme**

| Terim | Anlamı | Kaçınılacak |
|---|---|---|
| **Bağlantı** | Bir platform + ortam + mağaza kaydı | "entegrasyon" (sayfanın adı), "hesap", tek başına "mağaza" |
| Pazaryeri | Trendyol, Hepsiburada, N11, Pazarama, PttAVM, Shopify | E-Faturam'a "pazaryeri" demek (o fatura sağlayıcısı) |
| **Canlı / Test Ortamı** | Gerçek mağaza / pazaryerinin deneme ortamı (Trendyol Stage, Hepsiburada SIT) | "prod", "sandbox" |
| **Bağlı ve Aktif** / **Bağlı, Aktifleştirilmedi** / **Pasif** / **Test Edilmedi** | Okur ve yazar / okur ama yazmaz / kapatılmış, hiçbir şey yapmaz / hiç test edilmemiş ya da kimliği değişmiş, okumaz | "Devre Dışı" demek (eski ad kalktı) |
| **Bağlantıyı Test Et** | Yazmayan okuma denemesi (menü, kart ve sekmede aynı ad) | "Bağlantıyı Kontrol Et", "Test Et" |
| **Verileri Çek** | Elle ilk çekim planı: referans listeleri + sipariş geçmişi + iadeler (ürün yok) | "ürünleri çek", "senkronize et" |
| **Akış** | Bir okuma işini ne sıklıkla üreteceğini söyleyen bağlantı ayarı (Veri Eşitleme sekmesi) | "iş" (akış iş üretir), "cron" |
| **Kontrol Sıklığı** | Akışın temel aralığı | "çalışma aralığı", "senkron süresi" |
| **Tekrar Payı** | Okuma penceresinin geriye taşması (kaçan kayıt güvenliği) | "örtüşme", "tolerans" |
| **Seyrekleşme** / **Seyrek Sıklık** | Sessizken aralığın büyümesi ve üst sınırı | "uyku modu", "durdurma" |
| İmleç | Bir okumanın kaldığı yer | "cursor" (ekranda "İmleç Durumu") |
| **Uzlaştırma** (akış ve iş adı) | Bir pencereyi baştan okuyup panelle karşılaştırma. Akış adı "Kısa/Orta/Günlük Sipariş Uzlaştırması", ürettiği işin adı "Sipariş Uzlaştırması" (eski "Mutabakat" adı kalktı). | Hakedişle karıştırmak |
| **Sipariş Geçmişi Kurtarma** | Kaçan siparişleri arayan okuma | "recovery" |
| Webhook (anlık bildirim) | Pazaryerinin panele anında attığı bildirim; doğruluk kaynağı yine okumadır | "anlık senkron"; zildeki bildirimle karıştırmak |
| **Dış Yazma Ana Anahtarı** | Bağlantı başına her türlü pazaryeri yazmasının ana kapısı | sunucu genel yazma bayrağıyla karıştırmak; "otomatik gönderim" |
| **Otomatik Dış Yazmalar** (Fiyat Gönderimleri + Stok Gönderimleri) | Panel değişince kendiliğinden fiyat/stok gönderimi; **kapalı** | "stok senkronu" |
| **Manuel Dış Yazmalar** | Panelden başlatılan kargo/etiket ve iade kararı izni (PttAVM'de fiyat-stok ve ürün gönderimi izni) | "elle senkron" |
| Vitrin | Trendyol'un ülke vitrini (TR, DE, SA…) | "mağaza", "kanal" |
| Yurt dışı vitrin siparişi | TR dışı vitrinin siparişi; adres İstanbul ihracat deposu, ülkesi vitrin | "İstanbul siparişi" |
| **Mikro İhracat** | Trendyol'un `micro=true` işaretli siparişi | Her yurt dışı vitrin siparişine demek |
| Referans listeleri (**Katalog Referansları**) | Pazaryerinin kategori, özellik, değer, marka listeleri | "katalog" (ürün kataloğuyla karışır) |

**Sıfırlama ve silme**

| Terim | Anlamı | Kaçınılacak |
|---|---|---|
| **Verileri Sıfırla** | Entegrasyonlar'da bağlantı menüsündeki silme penceresi (seçmeli ya da Tam Sıfırla) | Sistem Ayarları › Veri ile aynı sanmak |
| **Tam Sıfırlama** | Bir pazaryeri bağlantısının bütün kayıtlarını silen, ayarlarını koruyan iş (`CONNECTION_FULL_RESET`) | "Paneli Tamamen Sıfırla" yerine kullanmak; "Bağlantıyı Sil" ile eş saymak |
| **Bağlantıyı Sil** | Bağlantının verisini, ardından ayarlarını, anahtarlarını ve satırını kaldırır; kesilmiş mali fatura varsa reddedilir | "sıfırla" |
| **Veritabanı Temizliği** | Sistem Ayarları › Veri'deki kategori bazlı silme | "veri sıfırlama" |
| **Paneli Tamamen Sıfırla** | 19 kategorinin hepsini silen tek iş (`PANEL_FACTORY_RESET`) | "fabrika ayarı", "Tam Sıfırlama" |
| İşleyiş ayarı | Stok kuralları, depo ve depo eşleşmeleri, kargo eşleşmeleri, eşitleme kuralları, fatura kuralları, Excel kolon profilleri; hiçbir sıfırlamada silinmez | "veri" demek; Kalanlar listesinde aramak |

**Katalog**

| Terim | Anlamı | Kaçınılacak |
|---|---|---|
| Model | Aynı marka + model kodundaki renk kartlarının tümü | "ürün grubu", "ana ürün" |
| Renk kartı (ürün kartı) | Modelin bir rengi; sunucuda ve pazaryerinde ayrı ürün | renk kartına "varyant" demek |
| Varyant | Renk kartının bir bedeni/satış satırı | satıra "ürün" demek |
| Ortak alan | Ad, açıklama, marka, kategori, KDV, garanti, teslimat, özellikler; modelin tüm renklerine yazılır | "genel bilgi" |
| **Taslak** | Pazaryerine gitmeyen, stok sayımına girmeyen eksik kart | "pasif ürün", "Satışa Kapalı" |
| **Satışa Kapalı** | Arşivlenmiş kart | "taslak", "silinmiş" |
| **Stok Kodu** | Varyantın hesap içinde benzersiz kodu (SKU) | "ürün kodu", "model kodu" |
| **Model Kodu** | Modeli tanımlayan kod; yeni kartta benzersiz | "stok kodu", "ana barkod" |
| **Panel Kategorisi** | Panelin kendi etkin yaprak kategorisi | "Trendyol kategorisi" |
| **Kategori Eşleme** / **Marka Eşleme** | Eşleştirmeler sayfasının iki sekmesi | "Seçenek Eşitleme", "Eşleştirme Ayarları", "Markalar sekmesi" |
| **Kategori Özellikleri** (pencere) | Alan ve değer eşlemesinin yapıldığı yer | "Özellik Eşleme sekmesi" |
| **Platform Varsayılanları** | Alan boşsa gidecek bağlantı geneli sabit değer. Veri temizliğinde kategori adı **Varsayılan Özellik Değerleri**. | — (iki ad aynı şey) |
| Eşleme şablonu (**Şablon** rozeti) | Bağlantı geneli alan/değer kararı; yeni kategorileri doldurur, eşlemeyi ezmez | "otomatik eşleme" |
| Beden tablosu | Kartın daima son görseli olan ölçü görseli | — |
| **Tam Katalog Taraması** / **Yeni Ürünleri Aktar** / **Mevcut Ürünleri Güncelle** / **Ürün Eşleme** | Platformdan Ürün Aktar modları | "full sync" |
| **Mevcut Ürün Bilgilerini Güncelle** | Mevcut ürünün içeriğini pazaryerinden (yerel değişiklikleri de ezerek) yazan kutu | "Mevcut Ürünleri Güncelle" (o mod adı) |
| **Yayınla** | Yeni ilan açan gönderim (fiyat/stok dahil) | "güncelle" |
| **Hızlı Güncelle** | Kayıtlı ayarla, pencere açmadan gönderim; ayar yoksa fiyat + stok; ayar tarayıcıda | "otomatik güncelleme" |
| **Platformlarda Güncelle** (düzenleyici) / **Platformlara Güncelle** (liste) | Yayındaki ilana seçili bilgileri gönderir; elle yazma sayılır | "senkronize et" |
| **İçe / Dışa Aktar** (Excel) | Varyant satırlı dosya; yeni ürün açmaz | "Excel'den ürün oluşturma" |
| **Ürün Hareketleri** | İşlem Takibi › Loglar'daki stok/fiyat/durum/iade logu | "ürün geçmişi" (maliyet, ad vb. burada yok) |

**Sipariş, iade, fatura, stok**

| Terim | Anlamı | Kaçınılacak |
|---|---|---|
| Sipariş | Müşteri siparişi; bir ya da birden çok paket taşır | paketle eş anlamlı (kartlar "N Paket" sayar) |
| Paket | Kargoya giden birim; durumu siparişin durumunu belirler | "gönderi", "kargo" |
| **Bekleyen Siparişler** / **Bekleyen İşler** | Depo kuyruğu: NEW, PROCESSING, READY_TO_SHIP, PARTIALLY_CANCELLED, MANUAL_REVIEW | "Askıda" demek (o ayrı durum), "açık sipariş" (SHIPPED dahil değil) |
| **Geciken** | Kargoya verme süresi **geçmiş** bekleyen sipariş | Süresine 24 saatten az kalmış sipariş (o satırın uyarısı **Süre Doluyor**) |
| **Askıda** (sekme, halka, dışa aktarma; eski "Askıdaki" / "Beklemede" adları kalktı) | Pazaryerinin askıya aldığı paket (`ON_HOLD`) | Ödeme bekleyen siparişe demek |
| **Ödeme Bekleniyor** (halka; ayrı sekmesi yok) | Trendyol ödeme onaylanmadan işlenemez | "Askıda" |
| **İnceleme Gerekli** (sekme ve halka aynı ad; iş durumundaki "İnceleme Bekliyor" ayrı bir durumdur) | Paket durumu okunamadı ya da tanınmadı | Fatura süzgecindeki "İnceleme Gerekli" ile karıştırmak |
| **Yeniden Gönderim** | Trendyol'un yeniden gönderim paketi + sahibin elle girdiği hatırlatıcı sayı | "Başka Kargo Firması İle Gönder" |
| **İşleme Al** | Pazaryerine "Picking" gönderir (Trendyol, N11, Pazarama); Hepsiburada'da paket oluşturur; PttAVM ve Shopify'da düğme yok | "Onayla" |
| **Kısmi İptal** | Sipariş tamamen iptal edilmemiş, bazı adetleri iptal | "kısmi iade" |
| **Kısmen Onaylandı** | **İade talebinin** bir kısmı onaylanmış | Fatura durumu sanmak |
| Fatura kesimi | E-Faturam'da mali belge oluşturmak; her zaman sahip onayıyla | "fatura gönderimi" |
| Fatura iletimi | Kesilmiş faturanın **linkini** pazaryerine bildirmek; otomatik ve yeniden denenir | "fatura kesimi", "dosya yükleme" |
| **Tekrar Gönder** | Yalnız iletimi yeniden dener; yeni fatura kesmez | "faturayı yeniden kes" |
| e-Arşiv / e-Fatura | Mükellef sorgusuyla seçilir; sorgu başarısızsa fatura durur | "varsayılan e-Arşiv" |
| **Faturası Yüklendi** | Sahibin elle işareti; panel fatura kesmez | "fatura kesildi" |
| Bilinmiyor (pazaryeri fatura durumu) | N11, PttAVM, Pazarama faturayı göremiyor | "Fatura Kesilmedi" |
| İade faturası | Panelde kesilmez; E-Faturam portalında düzenlenir | — |
| Eldeki stok | Paneldeki fiziksel stok; sipariş okununca düşer | "rezerve stok" (rezervasyon yok) |
| Eksik adet | Stoğun karşılayamadığı adet; yalnız bilgi, sonradan düşülmez | "stok gelince düşülecek adet" |
| **Güvenlik Stoğu** | Pazaryerine gönderilen stoktan düşülen pay (teklif başına) | depo stoğu |
| Ana depo (`MAIN`) | Tek kullanılan depo konumu | çoklu depo |
| **Stoğa Ekle** (iade) | Onay kesinleşince ya da "Satılabilir" kararıyla eldeki stoğu artırır | "otomatik stok iadesi" |

**Raporlar ve para**

| Terim | Anlamı | Kaçınılacak |
|---|---|---|
| **Satış** (ana sayfa) = **Toplam Ciro** (Raporlar) = **Satış Tutarı** (Kâr Hesabı) | İptal edilmemiş siparişlerin satış tutarı; iadeler **düşülmemiş** | tek başına "ciro", "brüt satış", "hakediş" |
| **Net Ciro** | Toplam Ciro − İade Edilen Tutar | "kâr" |
| **İade Edilen Tutar** | Sayılan iadelerin müşterinin ödediği tutarı | "masraf", "iade bedeli" |
| **Maliyeti Olan Ciro** | Maliyeti girili satırların satış tutarı, iadeler düşülmeden | "net ciro" |
| **Ürün Maliyeti** | Maliyet × müşteride kalan adet; maliyet KDV dahil varsayılır | "alış fiyatı toplamı" |
| **Masraflar Sonrası** | Toplam Ciro − iade − komisyon − kargo − kesintiler | "Brüt Kâr", "hakediş" |
| **Net Kâr** / **Tahmini Net Kâr** / **Tahmini Zarar** | Masraflar Sonrası − Net KDV − Ürün Maliyeti − Reklam ve Diğer Kesintiler | "kesin kâr" |
| Brüt Kâr | Ekranda karşılığı yok (kart kaldırıldı) | Raporlardan "Brüt Kâr" okumak |
| **Kâr Marjı** (Raporlar Genel Bakış) = **Net Kâr / Satış Fiyatı Oranı** | Net Kâr ÷ Toplam Ciro | Sipariş Kârlılığı'ndaki **Sipariş Kâr Marjı** ile aynı sanmak |
| **Maliyete Göre Kâr** = **Net Kâr / Ürün Maliyeti Oranı**; tarifede **Kâr Oranı** | Kâr ÷ ürün maliyeti | "marj" |
| **Toplam Sipariş** (ana sayfa) / **Sipariş** (Raporlar) | Ana sayfa yalnız tutarı > 0 ve ana para birimindeki; Raporlar hepsini sayar | ikisini aynı sanmak |
| **Ortalama Sepet** | Ciro ÷ tutarı olan sipariş, iadeler dahil | "net sepet" |
| **Yaklaşan** (Faturalar kartı) / **Süresi Yaklaşanlar** | Teslimden 4 gün geçmiş, faturası kesilmemiş | "5 gün", "vadesi geçmiş" |
| **Düşük Stok** | Bir renk grubunda varyantların en az yarısı stoksuz olan ürün | "stoğu X adedin altında" |
| **Hakediş** | Pazaryerinin ödeyeceği/ödediği para | "kâr", "ciro" |
| **Toplam Güncel Bakiye** | Ödenmemiş tüm hakediş kayıtlarının neti | "gelecek ödeme" |
| **Sıradaki Ödeme** | İlk pozitif ödeme gününün tahmini tutarı | "kesin ödeme" |
| **Vadesi Geçti** / **Vadesi Geçmiş Bekleyen** | Vadesi geçmiş, henüz bir ödemeye bağlanmamış | "ödenmedi", "batık" |
| **Teslim Bekleyen Sipariş** (Hakediş) | NEW, PROCESSING, READY_TO_SHIP, SHIPPED; TRY net tutar | Bekleyen Siparişler ile aynı sanmak |
| **Ödeme Özeti** | Hakediş sekmesindeki ödeme listesi | ayrı sekme sanmak |
| **Komisyon** (gerçek / tahmini / bilinmiyor) | Pazaryeri kaydında gerçek; yoksa oran tahmini; ikisi de yoksa bilinmiyor | bilinmeyeni 0 saymak |
| **Kesintiler** | Hizmet Bedeli, Ödeme Komisyonu, Stopaj (siparişe bağlı) | reklam/cezayı bunun içinde saymak |
| **Reklam ve Diğer Kesintiler** | Siparişe bağlı olmayan Reklam, Ceza, Erken Ödeme Kesintisi, Diğer Kesinti | — |
| **Trendyol Reklamı** / **Reklam Raporu** | Cari hesaptaki reklam bedeli (dönem) / yüklenen kampanya raporu (kampanya başından) | ikisini toplamak |
| **Ürün Komisyon Tarifesi** (sekme) = **Komisyon Tarifeleri** (bölüm) | Trendyol'un haftalık fiyat bandı–komisyon dosyası | — |
| **Flash Ürünler** | Trendyol flaş teklif dosyası sekmesi | "Flaş Ürünler" |
| **Excel İndir** | Tarife seçimlerini Trendyol dosyasına yazar; satıcı kendisi yükler | "Trendyol'a gönder" |

**İşler, altyapı, ayarlar**

| Terim | Anlamı | Kaçınılacak |
|---|---|---|
| **İşlem Takibi** | Menüdeki `/jobs` sayfası; Ayarlar › Veri'deki kategori ve Verileri Sıfırla'daki "İşlem Takibi Kayıtları" da bu adı taşır | sayfaya "İş Takibi" ya da "İşler ekranı" demek |
| **İşler** | İşlem Takibi'nin ilk sekmesi (iş listesi) | sayfanın adı sanmak |
| İş | Arka planda çalışan tek görev (kuyrukta bir satır) | "senkron", "süreç" |
| Rozet ve süzgeç: **Bekliyor**, **Çalışıyor**, **Yeniden Denenecek**, **Engellendi**, **İnceleme Bekliyor**, **Başarılı**, **Deneme Limiti Doldu**, **İptal Edildi** | İş durumları; her yerde aynı ad (6.3) | "dead letter kuyruğu" (ayrı kuyruk yok), "Ölen İş" |
| Deneme | Bir işin bir kez çalıştırılması (ilki de denemedir) | "retry" |
| Erteleme | Kilit/kota yüzünden hak yemeden ileri atma | "hata" |
| Kira | Bir işin 2 dk'lık sahiplenilmesi | "kilit" |
| Kilit grubu | Bir bağlantıda aynı anda tek iş çalışan grup | "kuyruk" |
| Şerit | Worker'ın belirli işleri alan yuva kümesi | "kuyruk" (tek kuyruk var) |
| Kota | Pazaryerinin istek sınırı; paylaşılan kovalarla sayılır | "hız sınırı" (o panelin kendi giriş sınırları) |
| Bağlantı geri çekilmesi | 429/kimlik reddi sonrası bağlantı + grubun beklemesi | "ban" |
| **Bildirim geçmişi** (zil) | Tarayıcıda tutulan en çok 20 kayıt + sunucunun dikkat bildirimleri | "sunucu bildirimi"; operasyonel sorunla karıştırmak |
| Operasyonel sorun | Sunucuda kayıtlı sorun; zile düşmez | "zil bildirimi", "alarm" |
| **Alarm Bildirimleri** | Sunucu alarmlarının Gmail ile gönderimi | "Bildirimler" (o zil ya da Görünüm önizlemesi) |
| **Uygulama Şifresi** | Google'ın 16 harflik uygulama şifresi | "Gmail şifresi" |
| **Claude API Anahtarı** / **Varsayılan Model** / Beceri | Hesabın Anthropic anahtarı / bütün yapay zekâ özelliklerinin tek modeli / Claude'a yüklenen talimat paketi | "yapay zekâ şifresi", "eklenti" |
| **Sistem Ayarları** | `/settings` sayfası (üst yol etiketi "Ayarlar") | menüde "Ayarlar" demek |
| **Hızlı Arama** | Ctrl+K ile açılan sayfa listesi | "arama" (kayıt aramaz) |
| Sahip / Yönetici | `OWNER` / `ADMINISTRATOR`; ayarlar, sıfırlama, fatura kesme yetkisi | tek başına "admin" |
| Validate / Deploy / Canlı | GitHub doğrulama iş akışı / sunucuya kurulum / api, worker ve caddy aynı sha'da | "main'e itildi" = canlı sanmak |
| Geri yükleme tatbikatı / şema göçü provası | Aylık yedekten açma denemesi / deploy'da göçün yedek kopyada denenmesi | ikisini karıştırmak |
| CrowdSec simülasyon modu | Uyarı kaydeder, engellemez | "CrowdSec koruyor" |

---

## 9. Bilinen Tutarsızlıklar

12 Kasım 2026'da beş düzeltme turu (siparişler/iadeler/faturalar/stok, entegrasyonlar, raporlar, sistem/ayarlar, katalog)
`origin/main`'e girdi ve canlıya çıktı (`4fc160b69`). Önceki sürümdeki 67 maddenin 53'ü kodda giderildi; 14 sahip sorusunun
tamamı **13 Kasım 2026'da sahip tarafından yanıtlandı** ve kararlar ilgili bölümlere ile 10. bölüme yazıldı. Yalnız bir madde
(Ürün Hareketleri özeti) belirsiz kaldı. "Doğrulanmadı" yazanlar koddan çıkarılmıştır, canlıda denenmemiştir.

### 9.1 Sahip Kararı Bekleyenler

Sahibin 13 Kasım 2026'da yanıtladığı 14 sorunun 13'ü kapandı (kararlar ilgili bölümlerde ve 10. bölümün 8. kuralında);
mevcut davranış sürer.

1. **Ürün Hareketleri'ndeki beş türlük özet çubuğu (Stok, Fiyat, Durum, İade, Ürün Bilgisi) uygun mu?** Sahip yanıtı belirsiz;
   çubuk şimdiki hâliyle durur (3.14; `product-activity.ts:58-62`).

### 9.2 Açık Teknik Notlar

Karar değil, bilinen kalan işler ya da doğrulanamayanlar.

1. **Sunucu dışı yedek kopyası sunucuda kurulu değil**: birimler depoda hazır, `deploy/secrets` altına açık anahtar ve uzak hedef
   konunca deploy kurar; sahibin yapılandırma adımı bekliyor (6.8; `docs/CI-CD-DEPLOY.md` "Sunucu dışı yedek").
2. **Etiket tasarım şablonları yalnız tarayıcıda**: başka cihazda görünmez. Hesapta kargo ayarı yokken Sahip/Yönetici olmayan
   kullanıcının tarayıcısındaki eski kopyanın sunucuya aktarımı sessizce başarısız olur (7.3; `shipping-settings.ts:33-52`;
   `SystemSettingsPage.tsx:798-1007`).
3. **İki adımlı doğrulama zorunluluğunun canlı değeri okunamadı**; Sahibin TOTP'si kurulmamışken yazma işleri çalıştığına göre
   kapalı olduğu çıkarılıyor (7.1).
4. **N11 iade sayfalaması**: 20'den fazla iade olan pencerede yalnız ilk sayfa okunabilir — doğrulanmadı
   (`docs:n11:30-32`).
5. **Bağlantıyı Sil sonrası kalan finans kayıtlarının raporlara etkisi** doğrulanmadı (2.7; `ConnectionLeftovers.cs:10-16`).

---

## 10. Sahip Kuralları

Sahibin verdiği ve sistemin uyduğu kararlar. Bir değişiklik bunlardan birine dokunuyorsa önce sahibe sorulur.

1. **Fatura asla kendiliğinden kesilmez.** Kesim yalnız Sahip ya da Yönetici önizlemede **Onayla ve Faturayı Kes** dediğinde
   olur; fatura politikası `AutoSubmit=false` yazılır, Fatura Süresi Taraması fatura kesmez. Tür seçimi (e-Arşiv / e-Fatura)
   otomatiktir ama mükellef sorgusu başarısızsa fatura durur; son kontrolde sipariş değiştiyse yeniden onay istenir. Kendiliğinden
   olan yalnız kesilmiş faturanın **linkinin** iletimi ve yeniden denenmesidir (4.4; `InvoicingBillingService.cs:2176`).
2. **Otomatik fiyat/stok yazması kapalı** (1 Kas 2026, bütün bağlantılarda). Açılmaz; elle gönderim (Yayınla, Hızlı Güncelle,
   Platformlarda/Platformlara Güncelle) çalışır (2.5; `SynchronizationPolicies.cs:631-666`).
3. **İşleyiş ayarları veri değildir.** Stok kuralları, kargo eşleşmeleri, eşitleme kuralları, depo tanımları ve eşleşmeleri,
   fatura kuralları ve Excel kolon profilleri **hiçbir sıfırlamada silinmez** ve Paneli Tamamen Sıfırla'nın Kalanlar listesinde
   de **anılmaz**. Yalnız silinmiş bir bağlantının ayarları bağlantıyla birlikte kalkar (7.2 d; `PanelFactoryResetInventory.cs`).
4. **Marka listesi çekimi tam listedir, ad araması yoktur.** Marka listesi Trendyol ve Pazarama'dan bütün olarak indirilir;
   hiçbir pazaryerinde ada göre marka aranmaz. Eşleştirmeler › Marka Eşleme'de otomatik seçim ve öneri yoktur; elle eşlemeyi
   kullanıcı yapar (3.10; `brand-reference.tsx:1-27`). Ürün aktarımı, klasik içe aktarma ve Excel için kural 5 geçerlidir.
5. **12 Kasım kararı — ürün aktarımında marka, kategori ve özellik gibi kendiliğinden kurulur**: önce bağlantıdaki eşleme,
   yoksa panelde aynı adlı marka, o da yoksa yeni panel markası; Trendyol'da eşleme de yazılır (3.11; `ImportedBrandRule.cs:5-10`).
   Değişiklik 12 Kas 19:10Z'de canlıya çıktı (`ac24f253c`). Aynı kural artık üç yolda tek kodla işler (`PanelBrandByName.cs`):
   Platformdan Ürün Aktar, klasik içe aktarma ve Excel'in **Marka** sütunu markayı **ada göre** bulur ya da açar; bir ad
   her yolda aynı markaya iner, kapalı marka yeniden açılmaz (3.9, 3.11, 3.12).
6. **Bağlantının Tam Sıfırlaması eşleme şablonlarını ve varsayılan özellik değerlerini (Platform Varsayılanları) de siler**
   (12 Kas 2026; `OperationalDataMaintenanceService.FullReset.cs:166-170`). Pencere metni de bunu söyler.
7. **Yeni promosyon ya da komisyon dosyası eskisini değiştirir.** Plus, Avantajlı Ürün Etiketi ve Flash dosyalarında önceki
   dosya koşulsuz silinir; Ürün Komisyon Tarifesi'nde günleri çakışan ve süresi geçmiş dosyalar silinir, yürürlükteki başka
   haftanın tarifesi bitene kadar kalır (5.4).
8. **13 Kasım 2026 kararları** (sahibin 14 soruya yanıtı):
   - **İade Edildi / İadede / Ödeme Bekleniyor / Doğrulanmadı sekmeleri gelmeyecek**; bu siparişler ve sayıları Tümü'dedir
     (4.2; `order-status-tabs.ts:3-19`).
   - **Pasife Al** Trendyol ve Shopify webhook'unu pazaryerinde kapatır, **Aktifleştir** yeniden açar ve aradaki siparişleri okur;
     Hepsiburada webhook'u elle kalır (2.6; `InstantNotificationService.cs:245,284`; `3e91997d2`).
   - **Net Kâr**: maliyet kapsamı ≥ %95 ise gösterilir, eksik maliyet satış payına göre tahmin edilir ve "Tahmini" yazar; altında
     "Maliyet Eksik" (5.3.2; `ReportProfitRate.cs:16-23`; `b9af15939`).
   - **Toplam Sipariş** (ana sayfa) değişmedi; Raporlar'daki Sipariş'ten ayrı kalır (5.2.5; `ReportBuilder.cs:499-501`).
   - **`CommissionTariffLookup` silindi**: siparişin komisyonu kendi kaydında (5.4; `8cd9ab12b`, `1b2be7dbd`).
   - **Kuyruk ve Kota** ekranı ve **Bildirimler** merkezi İşlem Takibi'ndedir; bildirimler 7 gün saklanır (6.10; `5783e80f`).
   - **Kullanıcı yönetimi** Sistem Ayarları › Kullanıcılar'dadır, yalnız Sahip ve Yönetici (7.5; `91997d416`).
   - **CrowdSec**: panel beyaz listesi isteğin şekline göre kuruldu (12 Kas 23:54Z, simülasyon); sahibin IP'si dinamik olduğu için
     IP izin listesi yok; `--enforce` 2–3 sessiz günden sonra, sahibin adımıyla (6.8; `deploy/crowdsec`,
     `docs/CI-CD-DEPLOY.md`).
   - **Pazaryerinden aktarılan eksik alanlı ürün normal (taslak değil) kaydedilir**; klasik içe aktarmaya Garanti/Teslimat
     sütunları eklendi; **Toplu Satışa Aç eksikleri atlar ve bildirir** (3.2; `c5dec4c00`).
   - Kategori Eşleme'ye **"Yapıyı Pazaryerinden Çek" eklenmeyecek**; yapı ürün çekiminde gelir (6.4).
   - Yedek setlerinin silinmesi: sahip eskileri kendisi silmiş olabilir; konu kapandı (6.8).
   - **Fabrika sıfırlaması kendi izlerini saymaz** ve kiracısız giriş loglarını siler (7.2; `b07ac9686`, `6a8687290`);
     Ayarlar › Veri'de "Geri alınamaz" notu sade metindir (7.2; `e1ce7e96`); Eşleştirmeler'de Panel ve Platform seçicileri
     aynı hizadadır (3.10; `987add449`).
9. Diğer yürürlükteki kararlar (ilgili bölümde ayrıntılı):
   - Rezervasyon tutulmaz; sipariş okununca stok düşer, eksik adet yalnız bilgi amaçlıdır (30 Eki; 4.5).
   - İade faturası panelde kesilmez; kısmi iade faturayı yerinde bırakır, tam iade düzeltme bekletir (6 Kas; 4.4).
   - Fatura kesme, iptal ve iletim yalnız Sahip ve Yönetici (6 Kas; 7.5).
   - Hepsiburada iadesinde Para İadesi / Değişim seçimi zorunlu; değişimde aynı link yeniden iletilir (4.3).
   - Bilinmeyen bilinmeyen kalır: eksik alan durum çıkarmaz (fatura linki yok ≠ faturasız) (2.4).
   - Yurt dışı vitrin siparişleri vitrinin ülkesinde sayılır, İstanbul'da değil (2.8).
   - Maliyet 0 = maliyeti siler; Excel yeni ürün açmaz (3.12).
   - 11 zorunlu alan; fiyatlar 0'dan büyük; taslak eksik olabilir, satıştaki kartın eksikli kaydı durur (10–11 Kas; 3.2).
     Bu kural artık **sunucuda** da tutulur (düzenleyici, Toplu Değer Ata, Excel ve API aynı kuralı görür); **desi** her yolda
     0'dan büyük ve en çok **9.999**'dur (12 Kas; `ProductRequiredFields.cs`, `ProductDesi.cs`).
   - Maliyet kapsamı %95'in altındaysa **Net Kâr gösterilmez** ("Maliyet Eksik"): yanlış rakam eksik rakamdan kötüdür; maliyet
     KDV dahil kabul edilir ve ekran bunu yazar (12 Kas; eşik 13 Kas'ta %95 oldu; 5.3.2).
   - Raporlar dönem düğmeleri ve ana sayfa aynı **İstanbul günüyle** çalışır (12 Kas; 5.2.5).
   - **Bağlantıyı Sil** Trendyol ve Shopify webhook kaydını pazaryerinde de kapatır; olmazsa nota yazar (12 Kas; 2.6). Pasife Al için kural 8.
   - Ayarlar › Veri'de **bütün veri türleri** seçilirse bu Paneli Tamamen Sıfırla sayılır ve **PANELİ SIFIRLA** ister; sayfa
     yalnız Sahip ve Yönetici'ye açıktır (12 Kas; 7.2).
   - **Hızlı Güncelle** seçimi hesap başına sunucuda tutulur (12 Kas; 3.13).
   - Kimlik yenilenince eski anahtar hemen silinir; silinen bağlantının ayarları ve satırı kökten kaldırılır (12 Kas; 2.1, 2.7).
   - Sayfa değişince sayfa en üstten açılır (12 Kas; 1.3).
