# Ravencia Tasarım Dili — "Sade"

Kaynak ekran: **Eşleştirmeler › Kategori özellikleri** penceresi (`CategoryAttributeWorkspace`, `features/marketplace/category-workbench.css`).
Değerler canlı panelden (`panel.ravencia.com`, koyu tema "Ravencia — Sade", 16.10.2026) `getComputedStyle` ile ölçüldü.
Amaç: bu ekranın renk, yazı, boşluk, ikon ve bileşen kurallarını panelin geri kalanına aynen taşımak.

---

## 1. Temel fikir

| İlke | Uygulama |
|---|---|
| **Renk az, çizgi çok** | Bölgeler gölgeyle değil 1 px çizgiyle ayrılır. Kart gölgesi yok. |
| **Tek vurgu rengi** | Mor (`primary`) yalnız üç yerde: seçili satır, ana düğme, odak halkası. |
| **Durum = şekil** | Tamam / eksik / yok renkle değil ikonun şekliyle anlatılır; renkler gri tonlarında kalır. |
| **Sakin tipografi** | Gövde 14 px, en büyük başlık 18 px. Kalınlık 400 / 500 / 600; 700+ yok. |
| **Sabit ritim** | Kontroller 34–36 px, liste satırı 38 px, tablo satırı 44–46 px. |
| **Her şey hizalı** | Araç çubukları altındaki tablonun kolonlarına oturur (aynı grid, aynı iç boşluk, aynı kaydırma payı). |

---

## 2. Renk paleti (koyu tema "Ravencia — Sade")

Tanım: `styles/tokens.css` ve `features/settings/appearance-settings.ts › defaultDarkPalette`.

### 2.1 Yüzeyler ve çizgiler

| Token | Hex | Rol | Bu ekranda kullanıldığı yer |
|---|---|---|---|
| `--rv-color-bg` | `#101014` | Sayfa zemini | Pencere arkası |
| `--rv-color-surface` | `#17171c` | Kart / pencere / kontrol zemini | Pencere, seçim kutuları, arama, ikincil düğmeler, alt bar |
| `--rv-color-surface-raised` | `#1d1d23` | Bant / başlık zemini | Kategori satırı (bağlam bandı), tablo başlığı, salt-okunur alan kutusu |
| `--rv-color-surface-soft` | `#22222a` | Hover / sayaç zemini | Liste hover'ı, `cm-count` rozeti |
| `--rv-color-border` | `#2a2a32` | Standart ayırıcı | Bölge çizgileri, tablo satır çizgileri, tablo çerçevesi |
| `--rv-color-border-strong` | `#383842` | Kontrol kenarı | Seçim kutusu, arama, düğme, etiket çerçevesi |

> Kural: **kontroller `surface` zemininde durur, bantlar `surface-raised` zemininde.** Bir kontrol bandın üstündeyse bile kendi zemini `surface` kalır (kategori seçimi gibi).

### 2.2 Metin

| Token | Hex | Rol |
|---|---|---|
| `--rv-color-ink` | `#ececf0` | Ana metin, başlık, değer, düğme yazısı |
| `--rv-color-muted` | `#a3a3b0` | Açıklama, bölüm başlığı (ZORUNLU), tablo başlığı, sayaç, alt bar metni |
| `--rv-color-subtle` | `#6f6f7c` | Alan etiketi (`Panel kategorisi`), satır meta (`23/27 değer`), boş durum ikonları |

Üç kademe yeterli; dördüncü bir gri eklenmez.

### 2.3 Vurgu ve durum

| Token | Hex | Ne zaman |
|---|---|---|
| `--rv-color-primary` | `#8b5cf6` | Ana düğme zemini, seçili satırın sol çizgisi, odak halkası, açık filtre düğmesi |
| `--rv-color-primary-soft` | `#272139` | Seçili satır zemini, açık filtre düğmesi zemini |
| `--rv-color-warning` | `#e2aa4b` | Yalnız **sayaçta** dikkat (ör. "Gözden geçirilecek 2") — zemini `warning` %14 |
| `--rv-color-accent` | `#46b98f` | Bu ekranda kullanılmaz; başarı bildirimleri ve "her şey yolunda" özetleri için ayrılmıştır |
| `--rv-color-danger` | `#e4636f` | Silme / kalıcı hata; bu ekranda yok |

**Yapılmayacaklar:** durum için renkli rozet, renkli ikon, renkli satır zemini. İlerleme çubuğu dolgusu da gri (`muted`), mor değil.

---

## 3. Tipografi

Yazı ailesi: `--rv-font-ui` (Inter → sistem yazısı). Rakamlar `font-variant-numeric: tabular-nums` (sayaçlar, `23/27`, `4 / 10`).

| Rol | Boyut | Kalınlık | Renk | Örnek |
|---|---|---|---|---|
| Pencere / sayfa başlığı | 18 px | 600 | ink | "Kategori Alanları Ve Değerleri" |
| Bölüm başlığı (detay) | 18 px | 600 | ink | "Beden" |
| Değer / vurgulu bilgi | 16 px | 600 | ink | Kategori yolu, salt-okunur alan değeri |
| Gövde, liste öğesi, kontrol | 14 px | 400 | ink | "Web Color", seçim kutusu metni |
| Alt başlık (tablo grubu) | 14 px | 600 | ink | "Değer eşlemeleri" |
| Düğme | 13–14 px | 500 | ink | Araç çubuğu 13 px, form düğmeleri 14 px |
| Tablo başlığı, alt bar notu | 13 px | 400 | muted | "Panel değeri", "6 zorunlu alan bekliyor" |
| Alan etiketi | 12 px | 500 | subtle | "Panel kategorisi" |
| Bölüm etiketi | 12 px | 600, BÜYÜK HARF, +0.02em | muted | "ZORUNLU", "İSTEĞE BAĞLI" |
| Meta / sayaç | 11–12 px | 500 | subtle / muted | "23/27 değer", sayaç rozeti |
| Etiket (tag) | 12 px | 500 | muted | "Zorunlu" |

**Harf kuralları**
- Pencere ve sayfa **başlıkları** Baş Harfler Büyük (CSS `capitalize`).
- Alan etiketleri, düğmeler, tablo başlıkları, açıklamalar **cümle düzeninde** ("Tüm değerleri eşle").
- Bölüm etiketleri (ZORUNLU) tamamen büyük harf, küçük punto.
- Veri (değer adları, kodlar) olduğu gibi gösterilir; dönüştürülmez.

**Ağırlık kuralı:** liste satırları 400, seçili satır da 400 (seçim zemin ve çizgiyle anlatılır, kalınlıkla değil). Kalınlık 600 yalnızca başlıklarda ve "henüz eşlenmemiş" değerde (dikkat çekmek için).

---

## 4. Boşluk, ölçü ve köşe

### 4.1 Ölçüler

| Öğe | Yükseklik | İç boşluk |
|---|---|---|
| Araç çubuğu düğmesi | 34 px | 0 10 px |
| Form düğmesi / arama | 34 px | 0 12 px |
| Seçim kutusu, salt-okunur kutu | 36 px | 0 12 px (ok için sağ 38 px) |
| Liste satırı (sol menü) | 38 px | 0 20 px 0 18 px (+2 px sol çizgi) |
| Tablo başlık satırı | 34 px | 5 px 16 px |
| Tablo satırı | 44–46 px | 5 px 16 px |
| Sayaç rozeti | 18 px | 0 5 px, min 20 px genişlik |
| Etiket (tag) | 20–22 px | 0 8 px |
| İlerleme çubuğu | 4 px | — |
| Durum ikonu | 16 × 16 px | — |

### 4.2 Boşluklar

| Yer | Değer |
|---|---|
| Pencere başlığı | 18–20 px üst/alt, 24 px yan |
| Bağlam bandı (kategori satırı) | 14 px üst/alt, 24 px yan; öğeler arası 12 px dikey / 28 px yatay |
| Araç çubuğu düğmeleri arası | **8 px**, düğmeler satırı eşit paylaşarak doldurur |
| Detay paneli | 22 px üst, 28 px yan |
| Form alanları | 12 px kolon arası, 16 px satır arası; altında 20 px + çizgi |
| Değer araçları ↔ tablo | 20 px üst, 12 px alt |
| Alt bar | 14 px üst/alt, 28 px yan (detayla aynı) |
| Etiket ↔ kontrol | 4–6 px |

Ritim **4 px'in katları** (4, 8, 12, 16, 20, 24, 28). 10 ve 14 yalnız düğme iç boşluğu ve bant dikeyinde.

### 4.3 Köşeler

| Öğe | Yarıçap |
|---|---|
| Pencere / kart | 12 px |
| Tablo çerçevesi | 8 px |
| Kontrol (düğme, seçim, arama, salt-okunur kutu) | 7 px |
| Etiket, sayaç rozeti | 5 px |
| Liste satırı, bant, tablo satırı | 0 (düz) |
| İlerleme çubuğu | tam yuvarlak |

---

## 5. İkonlar

### 5.1 Çizgi stili
- Boyut **16 px** (araç çubuğunda 15 px), `viewBox 0 0 16 16`.
- Çizgi kalınlığı **1.6**, uçlar ve köşeler yuvarlak (`round`).
- Renk `currentColor`; varsayılan `subtle`, tamamlanmış durumda `ink`.
- Dolgulu ikon yalnız **tamam** durumunda.

### 5.2 Durum seti (her yerde aynı üçlü)

| Durum | Şekil | Renk | Anlamı |
|---|---|---|---|
| Tamam | Dolu daire + içi boş onay (`surface` rengiyle çizilir) | ink | Alan ve tüm değerleri eşlendi |
| Eksik | %35 opak halka + saat yayı + akrep | subtle | Eşlendi ama değerler eksik |
| Yok | Kesik çizgili boş daire (`2.6 2.2`) | subtle | Henüz eşlenmedi |

Bu set `MappingStateGlyph` (`features/marketplace/pages.tsx`) içinde. Başka sayfalarda (ürün yayın durumu, iş durumu, fatura) aynı şekiller kullanılmalı; yeni durum gerekiyorsa yalnız **hata** eklenir: kesik olmayan boş daire + çapraz çizgi, renk `danger`.

### 5.3 Yardımcı ikonlar
- **Bağlantı (zincir):** iki alan arasında, 36 × 36 kutuda ortalı, `subtle`.
- **Yenile:** araç çubuğu düğmesinde metnin solunda, 15 px.
- **Ok / chevron:** seçim kutularında tarayıcının kendi oku; ek ikon yok.

---

## 6. Bileşenler

### 6.1 Pencere (modal)
- Zemin `surface`, çerçeve `border`, köşe 12 px, gölge yok (arka perde yeterli).
- Başlık bloğu: başlık 18/600 Baş Harfler Büyük + tek satır açıklama 14 muted; altında 1 px çizgi.
- Pencere içinde **ikinci kart açılmaz**; içerik doğrudan pencerenin gövdesidir.

### 6.2 Bağlam bandı
Sayfanın "neyin üstünde çalışıyorum" satırı.
- Zemin `surface-raised`, üst/alt 1 px çizgi.
- Soldan sağa: seçim (240 px) → hedef yolu (16/600) → sağda ilerleme (220 px; etiket 12 muted, değer 12/600 ink, çubuk 4 px).
- İkinci satırda araç çubuğu; tüm düğmeler tek satır, 8 px aralık, satırı birlikte doldurur, kesilmez. Sayaçlı düğmede rozet sağda.

### 6.3 Ana–detay düzeni
- Sol liste 300 px sabit, sağda `border` çizgisi; detay esnek.
- Liste grupları: bölüm etiketi (ZORUNLU · `4 / 10`) + satırlar; gruplar arası 1 px çizgi.
- **Liste satırı:** `[ikon 16] [ad, esnek, taşarsa …] [meta, sağa yaslı]`, aralık 10 px.
  - Hover: zemin `surface-soft`.
  - Seçili: zemin `primary-soft` + 2 px `primary` sol çizgi; yazı kalınlığı değişmez.

### 6.4 Detay başlığı
`Ad (18/600)` + etiket (`Zorunlu`: 20 px, çerçeve `border-strong`, metin muted, zemin yok). Altında 18 px boşluk. Açıklama satırı yok.

### 6.5 Eşleme satırı (form)
`[Kaynak kutusu] [zincir 36] [Hedef seçimi] [Kaldır]`, kaynak ve hedef **eşit genişlikte**, ikon tam ortada.
- Kaynak salt-okunur kutu: 36 px, zemin `surface-raised`, çerçeve `border`, metin 16/600.
- "Kaldır": sessiz düğme (çerçevesiz, metin muted); hover'da zemin gelir.

### 6.6 Değer tablosu
- Çerçeve `border`, köşe 8 px; başlık satırı `surface-raised`, 13 px muted.
- Kolonlar: `minmax(0,1fr) minmax(0,1.3fr) 28px`, 16 px aralık, satır iç boşluğu 16 px.
- Satır: değer adı 16/400 (eşlenmemişse 600) · seçim kutusu · durum ikonu (sağa yaslı).
- Gövde kendi içinde kayar (en fazla ~380 px). **Başlık ve üstteki araç satırı aynı ince kaydırma payını ayırır**, böylece "Değer ara" seçim kolonunun solundan, son düğme sağından başlar/biter.

### 6.7 Araç satırı (tablo üstü)
Tablonun kolonlarında durur: sol kolonda `Başlık (14/600) + sayaç (muted)`, orta kolonda `[arama esnek] [Yalnızca eksikler] [Birebir eşle]`.
- **Açılıp kapanan filtre düğmesi** ("Yalnızca eksikler"): kapalıyken ikincil düğme; açıkken metin `primary`, zemin `primary-soft`, çerçeve `primary` (`aria-pressed`).

### 6.8 Alt bar
- Detay panelinin altına yapışık (`sticky`), zemin `surface`, üstte çizgi.
- Solda durum notu 13 px muted ("6 zorunlu alan bekliyor" / kaydedilmemiş değişiklik varsa ink renkte "Kaydedilmemiş değişiklikler var").
- Sağda `Sonraki eksik` (ikincil) + `Kaydet` (ana). Ana düğme değişiklik yoksa pasif (opaklık düşük).

### 6.9 Düğmeler

| Tür | Zemin | Çerçeve | Metin | Hover |
|---|---|---|---|---|
| Ana | primary | primary | #fff | biraz parlaklık |
| İkincil | surface | border-strong | ink | surface-soft |
| Sessiz | yok | yok | muted | surface-raised zemin, metin ink |
| Açık filtre | primary-soft | primary | primary | — |

Odak: `primary` çerçeve + 3 px `primary` %18 halka. Pasif: opaklık 0.45, imleç normal.

### 6.10 Sayaç rozeti
18 px yükseklik, min 20 px, 11/500, zemin `surface-soft`, metin muted, köşe 5 px. Dikkat durumunda metin `warning`, zemin `warning` %14.

### 6.11 Kaydırma çubuğu
Mac tarzı (tüm panelde): ok yok, iz şeffaf, 10 px alan içinde 4 px yuvarlak tutamaç (`ink` %22, hover %38).

---

## 7. Durumlar özeti

| Durum | Görünüm |
|---|---|
| Hover (liste, satır) | `surface-soft` zemin |
| Seçili | `primary-soft` zemin + 2 px `primary` sol çizgi |
| Odak (kontrol) | `primary` kenar + yumuşak halka |
| Pasif | %45 opaklık |
| Boş | Ortalanmış tek satır, `muted`, 28 px dikey boşluk |
| Yükleniyor | Mevcut `Busy` bileşeni, metin tek satır |
| Kaydedilmemiş | Alt bar notu ink renge döner, "Kaydet" aktifleşir |

---

## 8. Ölçümde bulunan tutarsızlıklar (uyarlarken düzeltilecek)

| Konu | Şu an | Olması gereken |
|---|---|---|
| Kontrol köşesi | Kontroller 7 px, "Kaldır" düğmesi 8 px | Hepsi 7 px |
| Düğme yazı boyu | Araç çubuğu 13 px, form düğmeleri 14 px | İki kademe bilinçli; diğer sayfalarda da aynı ayrım |
| Sayaç yazısı | "Değer eşlemeleri 23 / 27" sayacı 16 px | 14 px (başlıkla aynı) |
| Tablo satırı metni | 16 px | 14 px (gövde boyu) — yalnız eşlenmemiş değer 600 |
| Salt-okunur kutu metni | 16/600 | 14/600 (seçim kutusuyla aynı boy) |
| Pencere başlık iç boşluğu | Genel `workspace-modal` kuralı bazı pencerelerde 10 px'e indiriyor | 18–20 px |
| Kontrol yüksekliği | 34 ve 36 px karışık | Araç çubuğu ve tablo üstü 34, form alanları 36 |

---

## 9. Panele uyarlama sırası (öneri)

1. **Ortak parçaları ayır:** `MappingStateGlyph` → `shared/components/StatusGlyph`; `cm-count`, `cm-tag`, açık filtre düğmesi, alt bar → paylaşılan sınıflar (`rv-count`, `rv-tag`, `rv-toggle`, `rv-sticky-foot`).
2. **Global kontrol kuralı:** tüm `select`, `input`, ikincil düğme için zemin `surface`, kenar `border-strong`, köşe 7 px, yükseklik 34/36.
3. **Tablolar:** Siparişler, İadeler, Faturalar, İşlem Takibi tablolarında başlık 13 muted, satır çizgisi `border`, renkli rozetler yerine durum ikonları + kısa metin.
4. **Ana–detay ekranları:** Ürün Soruları ve İşlem Takibi detayı aynı sol liste / sağ detay kalıbına.
5. **Gösterge paneli:** KPI kartları gölgesiz, rakamlar tabular, durum noktaları yerine durum ikonları.
6. **Ürün düzenleme:** form alanları 36 px, etiketler 12 subtle, bölümler kart yerine çizgiyle ayrılır.

Her adımda ana CSS bütçesi (672 KiB, şu an 670.8 KiB) kontrol edilmeli; sayfaya özel stiller o sayfanın kendi CSS dosyasına konur, eski kuralların silinmesi bütçeyi açar.
