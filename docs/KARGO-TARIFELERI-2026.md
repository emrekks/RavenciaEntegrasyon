# Pazaryeri Kargo Tarifeleri ve Hizmet Bedelleri 2026

Belgenin bölümleri:
- 1–5: pazaryerlerinin kargo tarifeleri ve baremleri.
- 6: hizmet ve platform bedelleri.
- 7: Shopify.
- 8: komisyonun kaynağı.
- 9: kargoyu kim öder.
- 10: iade, iptal ve indirim.
- 11: kesintilerin KDV'si.

24 Ekim 2026'da derlendi. Kaynaklar:
- herkese açık resmî sayfalar ve PDF'ler;
- resmî metin bulunamayan yerlerde entegratör blogları;
- kendi Trendyol kargo faturalarımız (salt okunur SQL).

Satıcı panellerine giriş yapılmadı. Her rakamın yanında kaynağı ve güveni yazılı.

Tutarlar aksi yazılmadıkça **KDV hariç** TL'dir; satıcıya %20 KDV eklenerek faturalanır.
- N11 ve Pazarama'da ayrıca %2,35 Posta Hizmet Bedeli (PHB) eklenir.
- Desi = en × boy × yükseklik / 3000. Gerçek ağırlık daha büyükse ağırlık esas alınır.

Güven:
- **R:** resmî kaynak.
- **B:** birbirini tutan bloglar.
- **D:** tek kaynak ya da şüpheli.

## Özet: 1 desilik bir paket satıcıya kaça gelir

| Pazaryeri | En ucuz anlaşmalı firma | 0–1 desi, KDV hariç | KDV dahil | Barem (düşük sepet) | Güven |
|---|---|---|---|---|---|
| Trendyol | Trendyol Express | 81,95 | **98,34** | 350 TL altı, 10 desiye kadar | B, faturalarımızla doğrulandı |
| Hepsiburada | hepsiJET | 81,99 | 98,39 | 400 TL altı, yalnızca hepsiJET/Sürat | R (desi), B (barem) |
| N11 | PTT | 81,82 | 98,18 + PHB | 300 TL altı, 10 desiye kadar | R |
| Pazarama | Aras | 91,46 | 109,75 + PHB | 300 TL altı, 10 desiye kadar | R |
| PTT AVM | PTT Kargo | 65,63 | 78,75 | çelişkili | R (sözleşme eki, 27 Mart 2026) |

## 1. Trendyol

**Kendi faturalarımız.** Temmuz–Ekim 2026 arasındaki gönderi satırlarını taşıyıcı ve desiye göre ayırdık:

| Fatura tutarı (KDV dahil) | KDV hariç | Taşıyıcı | Desi | Karşılığı |
|---|---|---|---|---|
| 93,05 | 77,54 | TEX, PTT | 0–2 | 1 Ocak 2026 tarifesi (Eylül ortasına kadar faturalarda) |
| **98,34** | 81,95 | TEX, PTT, Logistiq | 0–2 | 10 Ağustos 2026 tarifesi |
| 100,72 | 83,93 | — | 1 | Aras 0–2 desi, 1 Ocak tarifesi |
| 106,76 | 88,96 | Aras | 1 | Aras 0–2 desi, 10 Ağustos tarifesi |
| 120,24 | 100,20 | PTT | 3 | 3 desi |
| 128,64 | 107,20 | TEX | 4 | TEX 4 desi |

İade kargosu da aynı tutarlarla faturalanıyor ("İade Kargo Bedeli" satırları).

Gönderi satırlarının siparişleri 350 TL'nin üstünde. Bu yüzden faturalarımızda henüz barem tutarı görünmüyor.

**Desi tablosu, 10 Ağustos 2026'dan beri.** PTT sütunu 8 Eylül'de değişti.
- Kaynaklar: Sentos (güncelleme 8 Eylül 2026), kargoentegrator (7 Ağustos), kargonomi (22 Ekim).
- Güven: B; TEX sütunu faturalarımızla doğrulandı.

| Desi | TEX | PTT* | Aras | Sürat | Kolay Gelsin | DHL eCom | Yurtiçi |
|---|---|---|---|---|---|---|---|
| 0–2 | 81,95 | 81,95 | 88,96 | 95,54 | 96,59 | 97,99 | 121,75 |
| 3 | 100,20 | 100,00 | 100,84 | 106,45 | 107,09 | 110,99 | 132,56 |
| 4 | 107,20 | 105,60 | 109,90 | 116,40 | 118,64 | 124,99 | 135,41 |
| 5 | 114,10 | 110,61 | 117,85 | 122,41 | 128,09 | 137,99 | 157,16 |
| 6 | 125,60 | 120,83 | 128,39 | 134,48 | 138,59 | 150,99 | 164,75 |
| 7 | 133,41 | 129,17 | 136,17 | 143,61 | 148,04 | 159,99 | 186,34 |
| 8 | 142,32 | 138,30 | 145,27 | 152,60 | 158,54 | 169,99 | 193,50 |
| 9 | 150,64 | 150,00 | 153,60 | 161,73 | 167,99 | 179,99 | 205,50 |
| 10 | 164,18 | 169,84 | 164,23 | 170,86 | 179,54 | 189,99 | 214,59 |
| 11 | 173,42 | 178,21 | 173,09 | 182,99 | 190,04 | 199,99 | 228,48 |
| 12 | 182,20 | 187,17 | 179,47 | 193,34 | 201,59 | 209,99 | 242,84 |
| 13 | 190,46 | 196,16 | 187,61 | 201,11 | 212,09 | 219,99 | 250,52 |
| 14 | 198,10 | 205,14 | 194,84 | 206,02 | 223,64 | 229,99 | 270,13 |
| 15 | 206,03 | 214,08 | 202,03 | 213,51 | 235,19 | 244,98 | 284,54 |
| 16 | 213,82 | 223,04 | 213,36 | 220,73 | 246,74 | 264,99 | 292,69 |
| 17 | 224,32 | 232,02 | 224,62 | 232,45 | 258,29 | 280,99 | 308,96 |
| 18 | 233,82 | 240,98 | 235,95 | 244,17 | 269,84 | 299,99 | 324,29 |
| 19 | 243,28 | 249,97 | 247,24 | 255,75 | 281,39 | 315,99 | 331,02 |
| 20 | 252,77 | 258,94 | 252,09 | 267,46 | 292,94 | 334,99 | 338,15 |
| 21 | 263,34 | 267,90 | 264,80 | 279,46 | 304,49 | 361,99 | 357,33 |
| 22 | 272,61 | 276,87 | 276,40 | 290,36 | 316,04 | 383,99 | 368,35 |
| 23 | 283,24 | 285,83 | 287,97 | 300,98 | 327,59 | 410,99 | 382,26 |
| 24 | 293,35 | 294,79 | 298,26 | 311,75 | 339,14 | 437,99 | 389,91 |
| 25 | 303,36 | 303,77 | 308,50 | 322,37 | 350,69 | 464,99 | 416,23 |
| 26 | 315,98 | 312,75 | 322,85 | 332,73 | 362,24 | 491,99 | 455,06 |
| 27 | 327,76 | 321,71 | 335,94 | 343,09 | 373,79 | 518,99 | 476,59 |
| 28 | 338,64 | 330,67 | 347,66 | 353,45 | 385,34 | 545,99 | 498,16 |
| 29 | 350,65 | 339,64 | 361,09 | 363,79 | 396,89 | 572,99 | 515,42 |
| 30 | 360,55 | 348,60 | 371,34 | 374,15 | 408,44 | 599,99 | 520,70 |

\* PTT sütunu 8 Eylül sonrasına ait. 10 Ağustos listesinde PTT 0–2 desi 83,74 idi; kaynaklar çelişiyor (D). Faturalarımızdaki PTT 0–2 desi satırları 98,34 (= 81,95 + KDV) ile bu tabloyu destekliyor.

- **30 desinin üstü:**
  - Firma şubesiyle mutabakat gerekiyor.
  - PTT 31 desiden itibaren 720,28.
  - 100 desi ve üstü "ağır kargo" ek ücreti: Aras 4.250, Sürat 4.500, Yurtiçi 5.350, DHL 7.154,99.
  - Lojistik firmaları 0–30 desi arasında sabit fiyat uyguluyor ve baremde değiller: CEVA Tedarik 516,21, CEVA 697,63, Horoz 679,75.
- **Önceki tarife (1 Ocak – 9 Ağustos 2026), TEX:**
  - 0–2 desi 77,54 · 3 desi 93,63 · 4 desi 101,46 · 5 desi 107,98 · 10 desi 153,47.
  - Aras 0–2 desi 83,93.
  - Kaynak: Dopigo (B).

**Barem.** Yalnızca Trendyol Anlaşmalı Kargo modelinde ve 10 desiye kadar geçerli. 350 TL ve üstü sipariş desi tablosuyla ücretlenir.

| Dönem, model | Sepet | TEX | PTT | Aras | Sürat | Kolay Gelsin | DHL | Yurtiçi |
|---|---|---|---|---|---|---|---|---|
| 10 Ağustos 2026, Model 1 (Hızlı: 1 günde kargoya teslim / Hızlı Teslimat etiketi) | 0–199,99 | 38,74 | 40,41 | 48,33 | 54,58 | 55,83 | 57,08 | 83,33 |
| | 200–349,99 | 70,41 | 72,08 | 79,16 | 85,41 | 86,66 | 87,91 | 113,33 |
| 10 Ağustos 2026, Model 2 (Standart) | 0–199,99 | 73,33 | 74,99 | 80,83 | 87,08 | 88,33 | 89,58 | 114,16 |
| | 200–349,99 | 78,74 | 80,41 | 86,24 | 92,49 | 93,74 | 94,99 | 119,16 |

Barem geçmişi (B/D):
- **1 Ocak 2026:** 0–149,99 / 150–299,99 TL; TEX 34,16 / 65,83.
- **26 Mart 2026:** dilimler 200 / 350 TL oldu ve Hızlı/Standart diye iki model geldi.

**Diğer kurallar (B/D):**
- İade kargosu Trendyol anlaşmasından faturalanır. Barem siparişinin iadesi Model 1 barem fiyatıyla ücretlenir.
- Cayma iadesinin kargosunu satıcı öder.
- Başarısız teslimatta gönderi ücretinin bir oranı alınır:
  - %50: Aras, DHL, Kolay Gelsin, Yurtiçi.
  - %30: PTT.
  - %25: Sürat.
  - %100: lojistik firmaları.
- Çok paketli siparişte her paket ayrı satır olarak faturalanır.
- Platform hizmet bedeli ayrı bir kalemdir. Faturalarımızda sipariş başına 13,19 TL (KDV dahil) görünüyor.

## 2. Hepsiburada

**Desi tablosu (R).**
- Kaynak: Çözüm Merkezi PDF'i, "05 Ekim 2026 itibariyle geçerli, KDV hariçtir", https://images.hepsiburada.net/cst/assets/solutioncenterui/downloads/cargo.pdf
- PDF 0'dan 4.500 desiye kadar her desiyi tek tek veriyor. MNG listede yok.
- hepsiJET 60 desiye kadar taşıyor; daha ağır gönderiler hepsiJET XL ile gidiyor.

| Desi | hepsiJET | Aras | PTT | Sürat | Kolay Gelsin | DHL | Yurtiçi |
|---|---|---|---|---|---|---|---|
| 0–1 | 81,99 | 89,92 | 93,95 | 97,55 | 100,91 | 100,99 | 135,08 |
| 2 | 81,99 | 92,34 | 93,95 | 97,55 | 100,91 | 100,99 | 138,51 |
| 3 | 99,99 | 100,55 | 115,09 | 110,05 | 111,65 | 110,99 | 147,26 |
| 4 | 107,99 | 108,91 | 115,09 | 120,00 | 123,47 | 126,99 | 150,21 |
| 5 | 115,99 | 116,23 | 119,79 | 125,99 | 133,15 | 137,99 | 167,73 |
| 6 | 124,99 | 126,37 | 126,83 | 138,57 | 143,89 | 153,99 | 173,59 |
| 7 | 133,99 | 133,77 | 133,87 | 147,83 | 153,57 | 162,99 | 196,04 |
| 8 | 141,99 | 142,47 | 147,97 | 157,24 | 164,31 | 172,99 | 204,40 |
| 9 | 149,99 | 150,41 | 162,07 | 166,64 | 173,98 | 182,99 | 216,55 |
| 10 | 159,99 | 160,62 | 183,21 | 175,90 | 185,81 | 192,99 | 226,24 |
| 11 | 166,99 | 168,79 | 192,60 | 187,76 | 196,55 | 203,99 | 248,21 |
| 12 | 174,99 | 174,50 | 201,99 | 195,66 | 208,37 | 214,99 | 264,79 |
| 13 | 189,99 | 181,90 | 211,39 | 203,56 | 219,12 | 224,99 | 275,08 |
| 14 | 194,99 | 188,46 | 220,78 | 211,47 | 230,94 | 234,99 | 296,52 |
| 15 | 201,99 | 195,02 | 230,18 | 219,23 | 242,76 | 249,99 | 315,03 |
| 16 | 212,99 | 205,21 | 239,58 | 227,41 | 254,58 | 286,99 | 323,77 |
| 17 | 223,99 | 215,36 | 248,97 | 239,40 | 266,41 | 301,99 | 342,31 |
| 18 | 235,99 | 225,55 | 258,37 | 251,39 | 278,23 | 321,99 | 355,04 |
| 19 | 253,99 | 235,74 | 267,76 | 263,51 | 290,05 | 346,99 | 365,26 |
| 20 | 260,99 | 239,78 | 277,16 | 275,50 | 301,87 | 364,99 | 376,53 |
| 21 | 270,99 | 251,52 | 286,55 | 287,90 | 313,69 | 378,99 | 395,06 |
| 22 | 285,99 | 262,21 | 293,60 | 298,94 | 325,51 | 386,99 | 413,60 |
| 23 | 295,99 | 272,95 | 303,00 | 309,98 | 337,34 | 413,99 | 421,33 |
| 24 | 305,99 | 282,38 | 312,39 | 321,01 | 349,16 | 440,99 | 431,17 |
| 25 | 311,99 | 291,78 | 321,78 | 332,05 | 360,98 | 467,99 | 464,25 |
| 26 | 325,99 | 305,20 | 331,18 | 342,68 | 372,80 | 494,99 | 513,54 |
| 27 | 337,99 | 317,41 | 340,57 | 353,44 | 384,62 | 521,99 | 532,06 |
| 28 | 348,99 | 328,36 | 349,97 | 364,07 | 396,44 | 548,99 | 546,72 |
| 29 | 361,99 | 340,86 | 359,36 | 374,70 | 408,26 | 575,99 | 552,55 |
| 30 | 372,99 | 350,40 | 368,75 | 385,32 | 420,08 | 602,99 | 573,05 |

- **Lojistik firmaları**, 0–30 desi arasında sabit fiyat: Ceva Tedarik 509,70; Ceva Lojistik 803,48; hepsiJET XL 675,51 (60 desiye kadar); Horoz 752,02 (44 desiye kadar).
- **30 desinin üstü (PDF'ten):**
  - 31 desi: hepsiJET 390,99 · Aras 361,46 · PTT 720,28 · Sürat 460,95 · Yurtiçi 589,18 · DHL 638,98.
  - 100 desinin üstünde her ek desi: Aras 10,81 · DHL 35,17 · Kolay Gelsin 11,00 · PTT 17,37 · Sürat 14,38 · Yurtiçi 15,96.
- **2026 geçmişi, hepsiJET 0–2 desi (B):**
  - 2 Ocak'tan beri 78,50.
  - 24 Ağustos'ta 81,99'a çıktı.
  - 5 Ekim'de değişmedi.

**Barem (B).** Hepsiburada'nın resmî duyurusu açıkta bulunamadı.

| Dönem | 0–199,99 TL | 200–399,99 TL | 400 TL ve üstü | Hizmet bedeli |
|---|---|---|---|---|
| 21 Eylül 2026 öncesi | 42,00 | 72,00 | desi tablosu | baremde yok, diğer siparişlerde 10,50 |
| **21 Eylül 2026'dan beri** | **43,99** | **75,99** | desi tablosu | **10,99, barem dahil her teslimatta** |

Barem koşulları:
- Yalnızca **hepsiJET veya Sürat** ile gönderilen paketler.
- Kargoya veriliş süresi 0 veya 1 gün.
- Son 7 günde zamanında kargoya verme oranı en az %90.
- "Satıcı Öder" ve "Mağaza Hesabı" kargo modelleri baremde değil.
- 10 desi ve üstü paketler desi tablosuyla ücretlenir (D).
- Ücret koli başına alınır (D).

**Diğer kurallar (D):**
- İade kargosu desi tablosundan faturalanır. Barem siparişinin iadesi barem ücretiyle ücretlenir.
- 100 desinin üstü için firma bazında ek ücret var (Aras 2.250, Yurtiçi 2.850, Sürat 2.500…). Bu ek ücret 5 Ekim PDF'inde yok, eski olabilir.

## 3. N11

- **Kaynak (R):** https://www.n11.com/kampanyalar/ozel-kargo-kampanyasi, 24 Ekim 2026'da okundu. Afişin tarihi 22 Ekim.
- KDV ve %2,35 PHB hariç.
- Fiyat gönderi başına kümülatif: 2 adet 5 kg'lık koli, tek bir 10 kg gönderi gibi fiyatlanır. PTT'de hesap parça başına yapılır.
- Tam tablo (1–100 desi ve 100 üstü her desi için ek ücret): [kargo-tarifeleri/n11-2026-10-24.tsv](kargo-tarifeleri/n11-2026-10-24.tsv).

| Desi | PTT | Aras | Sürat | Kolay Gelsin | DHL eCom | Yurtiçi |
|---|---|---|---|---|---|---|
| Dosya/1 | 81,82 | 90,50 | 95,32 | 98,39 | 99,16 | 117,84 |
| 2 | 81,82 | 92,14 | 95,32 | 98,39 | 99,16 | 120,83 |
| 3 | 101,29 | 102,81 | 107,52 | 108,89 | 112,35 | 128,50 |
| 5 | 102,88 | 122,05 | 121,56 | 129,89 | 137,75 | 146,32 |
| 10 | 153,48 | 170,09 | 169,74 | 181,34 | 191,49 | 197,37 |
| 15 | 192,83 | 209,24 | 214,20 | 236,99 | 246,20 | 262,11 |
| 20 | 232,18 | 261,08 | 269,18 | 294,73 | 359,54 | 311,44 |
| 30 | 308,92 | 384,58 | 376,48 | 410,24 | 593,05 | 479,50 |
| 50 | 631,62 | 635,98 | 706,89 | 640,24 | 1.296,25 | 755,19 |
| 100 | 1.346,25 | 1.264,48 | 1.369,20 | 1.215,24 | 3.054,25 | 1.444,41 |
| 100 üstü her desi | 27,50 | 12,57 | 14,38 | 11,50 | 35,16 | 13,79 |

**Barem (R)**, "Şartlı Ödeme". 10 desiye kadar, 300 TL altı sepet.

| Sepet | PTT | Aras | Sürat | Kolay Gelsin | DHL | Yurtiçi |
|---|---|---|---|---|---|---|
| 0–149,99 | 38,74 | 48,33 | 54,58 | 55,83 | 57,08 | 83,33 |
| 150–299,99 | 70,41 | 79,16 | 85,41 | 86,66 | 87,91 | 113,33 |

Barem geçmişi (B): Haziran 2026 listesinde PTT 34,16 / 65,83 idi ve sınır 5 desiydi. 10 Ağustos 2026 civarında 10 desiye çıktı.

**Diğer kurallar:**
- **Lojistik firmaları:**
  - Horoz: asgari 739,61; desi başına 14,70 şehir içi, 17,26 şehir dışı.
  - Ceva Tedarik: asgari 539,91; desi başına 12,65 / 17,16.
  - Ceva Lojistik: asgari 816,95; desi başına 16,59 / 17,75.
- **Başarısız teslimat:** PTT %30; Yurtiçi, Aras, Sürat, Kolay Gelsin ve Ceva Tedarik %50; DHL, Horoz ve Ceva Lojistik %100.
- **Ağır kargo**, tek parçada 100 desi ve üstü: Yurtiçi 5.350, Aras 4.250, Sürat 4.500, DHL 6.750, PTT 4.000, Kolay Gelsin 5.000.
- **Ek ücretler:** Yurtiçi gönderi başına 0,60 TL SMS ücreti. PTT'de değerli eşya sigortası, ürün bedelinin %0,3'ü + 29 TL.
- **İade:** Kampanya koduyla yapılan iade tablo fiyatından ücretlenir. Cayma iadesinin kargosunu satıcı öder. Özel Kargo kampanyasındaki mağazalar N11 koduyla göndermek zorunda.

## 4. Pazarama

- **Kaynak (R):** "Pazarama Anlaşmalı Kargo Fiyat Listesi", 7 Ekim 2026, https://img.pzrmcdn.com/document/cargoprices7749fabc-37f6-4129-a4ca-8a31bc611ac2.pdf
- Parça başı fiyat; KDV ve %2,35 PHB hariç.
- PDF'teki satır etiketleri bir satır kaymış (DHL sütunu hariç). Tabloda düzeltildi ve kargonomi'nin değerleriyle karşılaştırıldı.
- Tam tablo: [kargo-tarifeleri/pazarama-2026-10-07.tsv](kargo-tarifeleri/pazarama-2026-10-07.tsv).

| Desi | PTT | Aras | Sürat | Kolay Gelsin | DHL eCom | Yurtiçi |
|---|---|---|---|---|---|---|
| Dosya/1 | 93,59 | 91,46 | 97,98 | 99,59 | 106,00 | 124,83 |
| 2 | 93,59 | 101,36 | 97,98 | 99,59 | 106,00 | 127,97 |
| 3 | 113,30 | 113,34 | 111,87 | 110,09 | 116,75 | 136,11 |
| 5 | 134,43 | 137,94 | 134,52 | 131,09 | 144,59 | 154,99 |
| 10 | 196,05 | 185,85 | 186,57 | 182,54 | 197,35 | 209,09 |
| 20 | 294,57 | 278,48 | 311,83 | 295,93 | 366,38 | 347,94 |
| 30 | 535,94 | 395,14 | 454,81 | 411,44 | 599,89 | 529,56 |
| 100 | 2.196,76 | 1.165,84 | 1.476,11 | 1.251,44 | 3.540,10 | 1.585,72 |
| 100 üstü her desi | — | 11,01 | 14,59 | 12,00 | 42,01 | 15,09 |

**Barem (R)**, 1 Ağustos 2026'dan beri.
- Koşullar: Pazarama Anlaşmalı Kargo, en fazla 10 desi, 300 TL altı sepet, taahhüt edilen sürede kargoya teslim.
- "Aynı Gün" fiyatı, teslimat şablonu "0 (Aynı Gün)" seçili satıcılara uygulanır.

| Sepet / şablon | PTT | Aras | Sürat | Kolay Gelsin | DHL | Yurtiçi |
|---|---|---|---|---|---|---|
| 0–149,99, Aynı Gün | 34,16 | 48,33 | 54,58 | 55,83 | 57,08 | 83,33 |
| 150–300, Aynı Gün | 65,83 | 79,16 | 85,41 | 86,66 | 87,91 | 113,33 |
| 0–149,99, diğer | 68,74 | 80,83 | 87,08 | 88,33 | 89,58 | 114,16 |
| 150–300, diğer | 74,16 | 86,24 | 92,49 | 93,74 | 94,99 | 119,16 |

**Diğer kurallar:**
- **Ağır kargo**, 100 desinin üstü: Aras 4.250, Sürat 4.500, Yurtiçi 5.350, DHL 7.425,99.
- **150 desi sınırı:** 9 Ağustos 2026'dan beri anlaşmalı firmalar 150 desinin üstünü taşımıyor; lojistik firmaları hariç.
- **Teslimat bölgesi dışı adres:** Horoz 1.929; Ceva Tedarik 2.310,17; Ceva Lojistik 3.785.
- **Kendi kargo anlaşması:** Kullanılabilir, ama o zaman barem uygulanmaz.
- **Başarısız teslimat:** %50, DHL'de %100 (B; resmî metinde oran yok).

## 5. PTT AVM

**Desi tablosu (R).**
- Kaynak: PTT AVM'nin satıcıya verdiği kargo sözleşmesi eki, "Güncellenen Yeni Kargo Fiyatları". PDF'in tarihi 27 Mart 2026.
- Taşıyıcı PTT Kargo. Fiyatlar **KDV dahil**. Ağırlık ile desiden büyüğü esas alınır.
- Tam tablo: [kargo-tarifeleri/pttavm-2026-03-27.tsv](kargo-tarifeleri/pttavm-2026-03-27.tsv).

| kg/desi | KDV dahil | KDV hariç | | kg/desi | KDV dahil | KDV hariç |
|---|---|---|---|---|---|---|
| 0–0,5 | 77,50 | 64,58 | | 10–11 | 143,75 | 119,79 |
| 0,5–1 | 78,75 | 65,63 | | 15–16 | 156,25 | 130,21 |
| 1–2 | 81,25 | 67,71 | | 20–21 | 169,90 | 141,58 |
| 2–3 | 82,50 | 68,75 | | 25–26 | 194,90 | 162,42 |
| 3–4 | 85,00 | 70,83 | | 30–31 | 199,90 | 166,58 |
| 4–5 | 86,25 | 71,88 | | 40–41 | 337,50 | 281,25 |
| 5–6 | 90,00 | 75,00 | | 50–51 | 398,75 | 332,29 |
| 6–7 | 103,75 | 86,46 | | 75–76 | 681,25 | 567,71 |
| 7–8 | 125,00 | 104,17 | | 99–100 | 1.019,64 | 849,70 |
| 8–9 | 131,25 | 109,38 | | 101 | 1.253,18 | 1.044,32 |
| 9–10 | 138,75 | 115,63 | | 1000 | 8.729,60 | 7.274,67 |

- PTT AVM'nin fiyatı diğer pazaryerlerinden farklı kuruluyor:
  - 0–6 desi arasında fiyat çok yavaş artıyor.
  - Bazı aralıklarda fiyat sabit kalıyor (20–25 desi 169,90; 25–30 desi 194,90).
  - 14–15 desi (153,75), 13–14 desiden (165,00) ucuz. Bu PDF'te öyle yazıyor.
- 100 desinin üstünde PDF her desiyi tek tek veriyor. Fiyat desi başına yaklaşık 7,88 TL artıyor, 960–1000 desi arasında 8,25 TL.
- PDF'te bariz yazım hataları var (örneğin 962 desi "2.380.196", 190 desi "19.845"). Bu satırlar komşu satırlardan okunmalı.
- Bu kaynak, bloglardaki 61,43 TL'den başlayan eski tablonun yerini alıyor.

**Barem:** Kaynaklar çelişiyor.
- Bir kaynak genel kategoride 250 TL, kitapta 750 TL diyor.
- Bir diğeri bunların alıcının ücretsiz kargo eşiği olduğunu söylüyor.
- 2025 tarihli bir kaynağa göre barem yok; 200 TL altında kargoyu alıcı ödüyor.

Kargodan ayrı olarak %1,5 altyapı/ödeme bedeli kesiliyor (2025).

## 6. Hizmet ve platform bedelleri (komisyon ve kargo dışında)

Bu bölüm, komisyon ve kargo tarifesi dışında sipariş ya da satış başına kesilen bedelleri listeler.

### Trendyol

| Bedel | Tutar | KDV dahil | Neye uygulanır | Geçerlilik | Güven |
|---|---|---|---|---|---|
| Platform Hizmet Bedeli | 10,99 + KDV | **13,19** | Taşıma statüsüne geçen her paket. Bölünen siparişte her paket için ayrı alınır. İade gönderisinde alınmaz. | 30 Ocak 2026'dan beri; öncesinde 8,49 + KDV | R + B, ekstrelerimizle doğrulandı |
| Platform Hizmet Bedeli, "Bugün Kargoda" | 4,99 + KDV | 5,99 | Bugün Kargoda etiketli ve aynı gün taşımaya geçen paket; yalnızca hafta içi | 15 Haziran 2026'dan beri; 30 Ocak'ta 6,99 + KDV idi | R + B |
| Uluslararası Hizmet Bedeli | Satış fiyatının **%6'sı** | KDV dahil | Mikro ihracat siparişleri, ürün başına. Trendyol'un karşıladığı indirimler hariç tutulur. İptal ve iadede alınmaz. | 30 Ocak 2026'dan beri; öncesinde %5 | B, ekstrelerimizle doğrulandı (34,22 / 570,34 = %6) |
| AZ-Platform Hizmet Bedeli | 10,99 | KDV yok | Azerbaycan vitrini siparişleri | ekstrelerimizde | Kendi verimiz |
| AZ-Uluslararası Hizmet Bedeli | %6 | — | Azerbaycan vitrini siparişleri | ekstrelerimizde | Kendi verimiz |
| Yurtdışı Operasyon İade Bedeli | 159,90–1.025,00 | — | Mikro ihracat iadesi (Ağustos–Ekim 2026'da 11 kayıt) | ekstrelerimizde | Kendi verimiz |
| E-ticaret stopajı | KDV hariç satışın %1'i | — | Hakedişten kesilir, ekstrede ayrı satır olarak görünür | 1 Ocak 2025'ten beri | R |
| Tedarik edememe / eksik / yanlış gönderim cezası | 0–149 TL siparişte 50 TL'den 10.000 TL ve üstü siparişte 1.000 TL'ye kademeli; ağır kusurlu ürün gönderi başına 300 TL | — | Paket başına; tutar müşteriye hediye çeki olarak verilir | 2 Haziran 2025'ten beri; 2026 güncellemesi bulunamadı | B (2025) |
| Geç kargolama cezası | Kademeli; üst kademe 250 TL. Termin 5 günden fazla aşılırsa tedarik edememe sayılır. | — | Terminde kargoya teslim edilmeyen paket | 2025 | D |

### Hepsiburada

| Bedel | Tutar | KDV dahil | Neye uygulanır | Geçerlilik | Güven |
|---|---|---|---|---|---|
| Hizmet Bedeli | **10,99 + KDV** | 13,19 | Her teslimat, barem gönderileri dahil | 21 Eylül 2026'dan beri | D (tek blog; HB ekstresiyle doğrulanmalı) |
| Hizmet Bedeli (önceki) | 10,50 + KDV | 12,60 | Her teslimat; barem gönderileri hariç | 2 Ocak – 20 Eylül 2026 | B |
| İşlem / ödeme bedeli | Ayrı kalem yok, Hizmet Bedeli'ne dahil | — | — | 5 Temmuz 2025'ten beri | B |
| E-ticaret stopajı | %1 | — | Hakedişten kesilir | 1 Ocak 2025'ten beri | B |
| Gecikme / iptal cezası | 2026 tutarı bulunamadı | — | — | — | — |

### N11

| Bedel | Tutar | KDV dahil | Neye uygulanır | Geçerlilik | Güven |
|---|---|---|---|---|---|
| Pazarlama Hizmet Bedeli | Ürün bedelinin **%1'i** (yatırımlık altın ve gümüşte %0,17) | %1,20 | Tamamlanan her ürün satışı | tarihsiz, 2026'da geçerli | R + B |
| Pazaryeri Hizmet Bedeli | Ürün bedelinin **%0,67'si** | %0,80 | Tüm ürün satışları | tarihsiz, 2026'da geçerli | R + B |
| Sipariş başına sabit bedel | Yok | — | — | — | B |
| E-ticaret stopajı | %1 | — | N11 ödemeden keser | 1 Ocak 2025'ten beri | R |
| Kargo Posta Hizmet Bedeli | Kargo fiyatının %2,35'i | + KDV | Anlaşmalı kargo gönderileri | 2026 | R |

### Pazarama

| Bedel | Tutar | KDV dahil | Neye uygulanır | Geçerlilik | Güven |
|---|---|---|---|---|---|
| Platform (Entegrasyon) Hizmet Bedeli | **8,99 + KDV** | 10,79 | Her sipariş/gönderi, kargo modelinden bağımsız | 10 Şubat 2025'ten beri; 2026 değişikliği bulunamadı | D |
| Kargo Posta Hizmet Bedeli | Kargo fiyatının %2,35'i | + KDV | Anlaşmalı kargo gönderileri | 7 Ekim 2026 listesi | R |
| İkinci dağıtım / iade sevk | Gönderi ücretinin %50'si (DHL'de %100) | — | İade ya da tekrar dağıtım | 2026 | D |
| E-ticaret stopajı | %1 | — | Yasal | 1 Ocak 2025'ten beri | B |

### PTT AVM

| Bedel | Tutar | Neye uygulanır | Güven |
|---|---|---|---|
| Altyapı / ödeme bedeli | %1,5 (KDV dahil). 3 Şubat 2025'te %1'den yükseltildi. 2026 kaynağı bulunamadı. | Satış tutarı | D |
| Sipariş başına hizmet bedeli | Herkese açık bilgi bulunamadı | — | — |
| E-ticaret stopajı | %1 | Yasal | B |

### Stopaj (bütün pazaryerleri)

- **Dayanak:** 7524 sayılı Kanun; oran Cumhurbaşkanı Kararı 9284 ile %1. Yürürlük 1 Ocak 2025. 2026'da değişiklik yok.
- **Matrah:** KDV hariç satış bedeli. Komisyon ve kargo matrahtan düşülmez.
- **Kesinti:** Pazaryeri hakedişten keser; faturada görünmez. Satıcı geçici ve yıllık beyanda mahsup eder.
- Mikro ihracat satışlarında stopaj olup olmadığına dair açık bir kaynak yok. Panel stopajı yalnızca yurt içi satışa uyguluyor.

## 7. Shopify (kendi mağazamız)

**Mağazamızın ayarları (24 Ekim 2026, mağaza sahibinden):**
- Plan: en düşük ücretli paket (Basic).
- Kargoyu satıcı öder, ortalama 120 TL.
- İade kargosunu müşteri öder.

| Kalem | Tutar | Vergi | Kaynak / güven |
|---|---|---|---|
| Basic plan | Aylık ödemede $27, yıllık ödemede ayda $19 | USD faturalanır. Türkiye KDV'sinin faturada nasıl gösterildiği Shopify'ın Billing sayfasından bakılmalı. | R (shopify.com/tr/pricing, 24 Ekim 2026) |
| Shopify işlem ücreti (Basic) | Harici ödeme altyapısıyla alınan her ödemenin **%2'si** | — | R. Shopify Payments Türkiye'de yok, bu yüzden her kartlı ödemede alınır. |
| Havale/EFT, kapıda ödeme | Shopify işlem ücreti **alınmaz** | — | R (manuel ödeme) |
| Ödeme kuruluşu komisyonu | iyzico liste fiyatı %4,29 + işlem başına 0,25 TL; PayTR %2,19'dan başlayan teklif; Param %2,49; taksitte banka vade farkı | KDV değil **%5 BSMV**; iyzico liste fiyatı BSMV dahil | iyzico için R; diğerleri D. Gerçek oran sözleşmeye göre değişir. |
| Kargo | Ortalama **120 TL** sipariş başına (satıcı öder) | Faturaya göre KDV dahil kabul edildi | Mağaza sahibi |
| İade kargosu | Müşteri öder (mağaza ayarı) | — | Mağaza sahibi. **Yasal not:** cayma hakkıyla yapılan iadede kargo masrafı satıcıya aittir. "Tüketici öder" değişikliği 24 Mayıs 2025'te geri alındı (B). Müşteriden kargo bedeli alınan iadeler hukuken itiraza açıktır. |
| E-ticaret stopajı | **Uygulanmaz.** Stopajı aracı hizmet sağlayıcılar (pazaryerleri) keser. Kendi sitesinden kendi ödeme altyapısıyla satan satıcıda kesen yoktur. | — | B/D (Paraşüt, 30 Haziran 2026). Mevzuatta açık bir istisna cümlesi yok; sonuç kesintiyi yapacak kişinin tanımından çıkıyor. |
| Uygulamalar | E-fatura uygulaması ayda yaklaşık $5–9 (ör. Hızlı BT $8,99) + entegratör kontörü; PayTR uygulaması ücretsiz | USD | R/D |

**Örnek:** Basic planda iyzico liste fiyatıyla kartlı tek çekimde satışın yaklaşık **%6,29'u + 0,25 TL** kesilir (iyzico %4,29 + Shopify %2). Buna sipariş başına 120 TL kargo eklenir.

**Panelde:** Shopify siparişlerinde komisyon alanı yok. Ödeme yöntemi (kart mı havale mi) panel tarafından henüz okunmuyor. Kâr hesabı için yüzde ve kargo tutarının panele ayar olarak girilmesi gerekir.

## 8. Komisyonun kaynağı

Komisyon tarifesi bu belgede tablo olarak yok. Oranlar kategoriye ve kampanyaya göre değişiyor, panel komisyonu siparişten okuyor.

Canlı veride son 60 günün sipariş satırlarında hangi alanların dolu olduğu:

| Pazaryeri | Sipariş satırında | Satır sayısı | Panele girilen tarife | KDV | Tipik aralık | Oranların yayımlandığı yer |
|---|---|---|---|---|---|---|
| Trendyol | `commission`: **oran** (ör. 13) | 2.494 / 2.538 satır | 1 tarife, 633 satır (haftalık .xlsx) | **KDV dahil**, üstüne KDV yok (R) | %5–27 | Herkese açık değil; partner panelinde "Ürün Komisyon Tarifeleri" |
| Hepsiburada | `commission.amount`: birim başına **tutar**, ayrıca `commissionRate` (ör. 18) | 226 / 226 satır | yok | Tutar KDV dahil birim fiyatın oranı (2 × 273 TL'de %18 = 49,14). Üstüne KDV eklenip eklenmediği komisyon faturasıyla doğrulanmalı. | %5–25 | Yalnızca satıcı panelinde |
| N11 | `commissionRate`: **oran**; kampanyada `sellerCampaignCommissionRate` düşülür | 27 / 27 satır | yok | **KDV dahil** (R) | %5,5–21 | magazadestek.n11.com/s/komisyon-oranlari |
| PTT AVM | `Komisyon`: **oran** (ör. 20) | 4 / 4 satır | yok | 2022 PDF'ine göre KDV dahil (D) | %5–15 | pttavm.com/lp/magaza-komisyonu |
| Pazarama | **Yok.** Komisyon sipariş servisinde değil, finans servisinde (`order/paymentAgreement` → `commissionAmount`) | 0 / 6 satır | yok | **KDV hariç, %20 KDV eklenir** (R) | %0–19 | isortagim.pazarama.com/static-pages/pazarama-comission-rate-list |
| Shopify | Komisyon yok; ödeme kuruluşu ve Shopify işlem ücreti var (bölüm 7) | — | — | BSMV | — | — |

Doğrulama: Trendyol'da 69 siparişin gerçek komisyonu (12.832,12 TL), satırdaki oranla hesaplanan tutarla (12.831,93 TL) neredeyse aynı.

## 9. Kargoyu kim öder

| Pazaryeri | Kural | Güven |
|---|---|---|
| Trendyol | Satıcı, anlaşmalı modelde barem ya da desi tarifesini öder. Alıcıdan alınan kargo ücreti (Plus/Pass vb.) Trendyol'da kalır, satıcıya geçmez. Kendi kargo anlaşmasında faturayı taşıyıcı keser ve barem uygulanmaz. | R; alıcı payı için D |
| Hepsiburada | Barem 400 TL altında, yalnızca hepsiJET ve Sürat ile. "Satıcı Öder" ve "Mağaza Hesabı" modelleri baremde değil. Alıcıdan alınan kargo payının nereye gittiğine dair 2026 kaynağı yok. | Barem için B, alıcı payı için D |
| N11 | Ücretsiz kargo eşiğini **N11 belirler** (300 TL), satıcı değiştiremez. Şartlı Ödeme'de eşiğin altında kargoyu alıcı, üstünde mağaza öder. N11'e göre bu kalemler satıcı için "nötr" faturalanır (POP/SPP). | R |
| Pazarama | Satıcı barem ya da desi tarifesini öder. Plus üyelerine kargo bedava kampanyası (50 TL üstü) Pazarama'nın. | R |
| PTT AVM | Siparişte `KargoKimden` alanı geliyor. Canlıdaki 4 satırın hepsinde `tedarikci_indirimi`: kargoyu satıcı (tedarikçi) karşılıyor. Eşik kaynakları çelişiyor. | Alan için kendi verimiz, eşik için D |
| Shopify | Satıcı öder, ortalama 120 TL (bölüm 7). | Mağaza sahibi |

## 10. İade, iptal ve indirim

**Genel yasal kural:**
- Cayma hakkıyla yapılan iadede kargo masrafı satıcıya aittir. 1 Ocak 2026'da yürürlüğe girmesi planlanan "tüketici öder" değişikliği 24 Mayıs 2025'te geri alındı (B).
- Ayıplı ürünün iade masrafı da satıcıya aittir.

**İade ve iptal:**

| Pazaryeri | Komisyon geri döner mi | İade kargosu | Gidiş kargosu | Hizmet bedeli | Stopaj | Güven |
|---|---|---|---|---|---|---|
| Trendyol | **Evet.** Sonraki komisyon faturasından düşülür; ekstrede Return satırında "+" görünür. Teslimattan önce iptal edilen siparişte finans kaydı hiç oluşmaz. | Satıcı öder; gidişle aynı faturada ayrı satır | Geri verilmez | Platform bedeli geri verilmez, iade paketinden de alınmaz. Uluslararası bedel iptal ve iadede alınmaz. | İptal edilen stopaj ekstrede ayrı satır olarak geri gelir | R |
| Hepsiburada | Bloglara göre evet; tam mı kısmi mi olduğu belirsiz | Cayma ve ayıplı üründe satıcı öder | Satıcıya faturalanır | Bulunamadı | Bulunamadı | B/D |
| N11 | **Evet.** Sonraki komisyon faturasında "İade" olarak eksi tutar görünür. | Cayma iadesinde mağaza öder. Kapıda iade ya da teslim edilemeyen gönderide kargonun %50'si alınır. | Bulunamadı | Bulunamadı | Bulunamadı | R |
| Pazarama | Onaylanan iadede evet | Satıcı öder (alıcı ücretsiz gönderir). İkinci dağıtım ve iade sevkinde %50. | Bulunamadı | Bulunamadı | Bulunamadı | R/D |
| PTT AVM | Bulunamadı | Bulunamadı | Bulunamadı | Bulunamadı | Bulunamadı | — |
| Shopify | Komisyon yok. Ödeme kuruluşunun iade işleminde komisyonu geri verip vermediği sözleşmeye bağlı. | Müşteri öder (mağaza ayarı; yasal not bölüm 7'de) | Satıcıda kalır | — | — | Mağaza sahibi |

Stopaj iadesi, genel kural (D): aynı ay içinde dönen iadede stopaj oluşmaz. Sonraki aya kalan iadede kesinti geçerli kalır ve satıcı bunu beyanda mahsup eder.

**İndirim ve kupon:**

| Pazaryeri | Satıcının indirimi | Pazaryerinin indirimi | Komisyon matrahı | Güven |
|---|---|---|---|---|
| Trendyol | Satıcı indirimi (`lineItemSellerDiscount`) ve satıcı kuponu hakedişten düşülür. | Trendyol kuponu ve platform kampanyaları (`lineItemTyDiscount`) satıcının gelirini etkilemez. Trendyol'un karşıladığı kuponda satıcı Trendyol'a fatura keser ve tutarı tahsil eder. | Komisyona esas fiyattan **yalnızca satıcının indirimi** düşülür. Uluslararası bedel de Trendyol indirimleri hariç tutularak hesaplanır. | R |
| Hepsiburada | `merchantDiscount` alanında geliyor | 2026 için resmî bir kural yok | Bulunamadı | D |
| N11 | `sellerDiscount`, `sellerCouponDiscount` | N11'in indirimi, puanı ve kuponu (`mallDiscount`) satıcının fiyatını etkilemez; maliyeti N11'e yazılır. | Kampanyada komisyon oranı `commissionRate − sellerCampaignCommissionRate` | R |
| Pazarama | Tek `discountAmount` alanı var; kimin karşıladığı ayrılmıyor | Plus kargo bedava Pazarama'nın | Bulunamadı | D |
| PTT AVM | `IndirimTedarikci` (satıcı) | `IndirimPttavm` (PTT AVM) ayrı alanda geliyor | Bulunamadı | Alanlar kendi verimiz |

## 11. Kesintilerin KDV'si ve faturalar

| Pazaryeri | Komisyon | Kargo | Hizmet / platform bedeli | Diğer | Fatura |
|---|---|---|---|---|---|
| Trendyol | KDV dahil oran | Tarife + %20 KDV | Platform bedeli 10,99 + KDV; uluslararası bedel %6 KDV dahil; Azerbaycan vitrinindeki platform bedelinde KDV yok | Reklam kesinti faturası (%20 varsayılıyor, D). Cezalar hizmet karşılığı değilse KDV'siz olabilir (D). Stopajda KDV yok. | Komisyon faturası ayın 7, 14, 21, 28'i ve son günü; e-belge olarak Finans > Fatura Listeleme'de |
| Hepsiburada | Oran KDV dahil fiyata uygulanıyor; üstüne KDV eklenip eklenmediği doğrulanmalı | Tarife KDV hariç + %20 | 10,99 + KDV (D) | — | Komisyon faturası ay sonunda, "Fatura Yönetimi"nde |
| N11 | KDV dahil oran | Tarife + %2,35 PHB + KDV | %1 ve %0,67 + KDV; komisyon faturasında gösterilir | Stopaj ödeme öncesi kesilir | Komisyon faturası aylık, ayın 15'ine kadar |
| Pazarama | **KDV hariç oran + %20 KDV** | Tarife + PHB + KDV | 8,99 + KDV (D) | — | Finans servisi sipariş başına komisyon faturası numarası ve KDV dahil tutar veriyor |
| PTT AVM | KDV dahil (2022, D) | Sözleşme tablosu KDV dahil | Altyapı %1,5 KDV dahil (D) | — | Bulunamadı |
| Shopify | — | Kargo firmasının faturası | Abonelik USD; ödeme kuruluşu komisyonu BSMV'li (KDV indirimi yok) | — | Shopify faturası Billing sayfasında |

Kesintilerdeki KDV genel kurala göre indirilecek KDV'dir: kâr hesabında maliyet KDV hariç sayılır, KDV net KDV'den düşülür. Muhasebeciyle teyit edilmesi önerilir. Ödeme kuruluşlarının BSMV'si indirilemez, doğrudan maliyettir.

## Panel için notlar

- **Trendyol:** Raporlar kargosu fatura gelmemiş siparişlere güncel tarifeyi (son 30 günde en sık kesilen gönderi tutarı, şu an 98,34) yazıyor. Desi bazında hesap şöyle kurulabilir:
  - Kargo faturalarının satırlarında desi ve tutar var.
  - Her taşıyıcı ve desi için güncel fiyat bu satırlardan çıkarılabilir; tablo elle girilmez.
  - Ürünün desisi (`ProductVariant.Desi`) ve siparişin kargo firması bilindiğinde sipariş o fiyatla hesaplanır.
  - Barem: 350 TL altı ve 10 desiye kadar siparişlerde barem fiyatı kullanılmalı. Hangi modelin (Hızlı/Standart) geçerli olduğu satıcının performansına bağlı; ilk barem faturası geldiğinde doğrulanmalı.
- **Diğer pazaryerleri:** Kargo faturası satırı okunmuyor. Tahmin için bu tablolardan, satıcının seçtiği firma ve ürün desisiyle yararlanılabilir. PTT AVM'de tek taşıyıcı PTT Kargo olduğu için sözleşme tablosu doğrudan kullanılabilir.
- **Hizmet bedelleri:** Panel Trendyol'da platform bedeli (13,19), uluslararası bedel (%6) ve stopajı (%1) hesaplıyor. Diğer pazaryerlerinde de aynı yöntem uygulanabilir:
  - Hepsiburada: teslimat başına 10,99 + KDV.
  - N11: satışın %1 + %0,67'si + KDV.
  - Pazarama: sipariş başına 8,99 + KDV.
  - PTT AVM: kaynak doğrulanınca %1,5.
- **Tarifeler değişiyor:** 2026'da Trendyol'da 3, Hepsiburada'da en az 3 kez değişti. Elle girilen bir tablo eskir. Faturadan öğrenilen fiyat esas alınmalı, bu belge kontrol için kullanılmalı.
