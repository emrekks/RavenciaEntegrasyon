# Ravencia MarketplaceHub — Proje Bilgi ve Durum Raporu

**Belge amacı:** Bu dosya projenin tek güncel bilgi kaynağıdır. Projenin amacını, mevcut çalışma modelini, sunucu ve entegrasyon bilgilerini, uygulanmış kapsamı, bilinen sınırları ve planlanan işleri açıklar.

**Son güncelleme:** 20 Ağustos 2026  
**Belge sahibi:** Ravencia proje sahibi  
**Çalışma modeli:** Panelden iletilen talep uygulanır, gerekli en dar kontrol yapılır, `main` dalına commit/push edilir ve talep kapsamındaysa sunucu güncellenir.

> Bu rapor operasyonel bir proje özetidir. Test kanıtı, faz/evidence günlüğü, capability raporu, release kapısı ve ayrıntılı tarihçe tutulmaz. Teknik geçmiş gerektiğinde Git commit geçmişinden incelenir.

## 1. Projenin amacı

Ravencia MarketplaceHub, Ravencia'nın ürün, varyant, stok, fiyat, sipariş, paket, kargo, iade ve fatura operasyonlarını tek panelden yönetmesi için geliştirilmiş web uygulamasıdır.

Temel hedefler:

- Ürün ve varyantları merkezi katalogda yönetmek.
- Trendyol ürün, sipariş, paket, kargo ve iade verilerini panelde göstermek.
- Stok ve fiyat düzenlemelerini hızlı ve toplu biçimde yapmak.
- Sipariş bilgilerini, müşteri/adres verilerini ve kargo durumunu tek ekranda izlemek.
- Siparişten Trendyol E-Faturam üzerinde E-Arşiv/E-Fatura oluşturmak.
- Oluşturulan faturanın durumunu, PDF belgesini ve sipariş bağlantısını takip etmek.
- Günlük çalışmayı ürün sahibinin manuel talimatlarıyla hızlı biçimde sürdürmek.

## 2. Güncel durum özeti

| Alan | Güncel durum |
|---|---|
| Ürün kataloğu | Ürün, varyant, stok, fiyat, model kodu, görsel ve platform durumu yönetiliyor. |
| Eşleştirme merkezi | Panel kategorisi, Trendyol kategori/özellik ve marka eşleştirmeleri mevcut. |
| Siparişler | Tümü varsayılan liste; ürün, alıcı, fiyat, paket, kargo ve fatura bilgileri aynı tabloda. |
| İadeler | İade listesi, ürün görseli, sipariş ve kargo bilgileri panelde gösteriliyor. |
| Faturalama | Trendyol E-Faturam doğrudan hesap bağlantısı ile manuel fatura oluşturma akışı mevcut. |
| PDF | Arşivlenmiş son PDF, “Faturayı Gör” bağlantısından doğrudan indiriliyor. |
| Fatura durumu | Yerel fatura kaydı, Trendyol sipariş snapshot'ındaki eski fatura bilgisinden öncelikli. |
| Başarı akışı | Fatura oluşturma başarılı olduğunda başarı bildirimi gösterilir, modal kapanır ve liste yenilenir. |
| Çalışma tipi | Manuel panel operasyonu. GitHub Actions, evidence ve faz belgeleri günlük teslim kapısı değildir. |
| Aktif platform kapsamı | `TRENDYOL` ve `TRENDYOL_EFATURAM`. |
| Yeni platformlar | Şimdilik plan dışı; ihtiyaç oluştuğunda ayrıca ele alınacak. |

## 3. Sunucu ve erişim bilgileri

### 3.1 Sunucu

- Sağlayıcı: AWS EC2 sınıfı sanal sunucu
- Hostname: `ip-172-31-6-193`
- İşletim sistemi: Ubuntu 26.04 LTS, `x86_64`
- Public IP: `63.180.140.51`
- Panel adresi: `https://panel.ravencia.com`
- Sağlık adresi: `https://panel.ravencia.com/health/ready`
- SSH kullanıcı adı: `ubuntu`
- SSH erişimi: Anahtar tabanlı; özel anahtar içeriği bu belgede veya Git'te tutulmaz.
- Sunucu repository yolu: `/home/ubuntu/RavenciaEntegrasyon`

### 3.2 Sunucu çalışma yapısı

Docker Compose üzerinde şu servisler çalışır:

| Servis | Görev | Güncel durum |
|---|---|---|
| Caddy | HTTPS, reverse proxy ve web arayüzü | Healthy |
| API | Panel API'si ve iş kuyruklarının giriş noktası | Healthy |
| Worker | Sipariş, fatura, PDF, retry ve reconciliation işleri | Healthy |
| PostgreSQL | Uygulama ve operasyon veritabanı | Healthy |

Son gözlenen imajlar:

- Edge: `marketplacehub-edge:manual-95d54ce`
- API/Worker: `marketplacehub-app:manual-d9fdb0e`
- PostgreSQL: `postgres:18.4`

Son gözlemde kök disk yaklaşık 72 GB toplam, 29 GB kullanılmış ve 44 GB boştu. Bu bilgi kapasite planlaması içindir; disk kullanımı büyüdükçe ayrıca kontrol edilmelidir.

### 3.3 Yayın akışı

1. Değişiklik Windows geliştirme ortamında yapılır.
2. İlgili en dar kontrol çalıştırılır.
3. Değişiklik `main` dalına commit edilir ve GitHub'a push edilir.
4. Sunucu repository'si `git pull --ff-only origin main` ile güncellenir.
5. Değişen API veya edge imajı oluşturulur.
6. İlgili Docker Compose servisi yeniden başlatılır.
7. `/health/ready` kontrolü yapılır.

Git geçmişi yalnız kodun teknik geçmişidir; günlük iş akışında belge veya release kanıtı yerine kullanılmaz.

## 4. Entegrasyonlar

### 4.1 Trendyol

Trendyol bağlantısı sipariş, ürün, paket, kargo ve iade verilerinin alınması için kullanılır. Panelde ürün ve sipariş operasyonlarının temel kaynağıdır.

### 4.2 Trendyol E-Faturam

- Kapsam: Tek işletmenin doğrudan E-Faturam hesabı.
- Kimlik doğrulama: Panelde şifreli saklanan hesap e-postası ve parola.
- Ortam: Stage/Production ayrımı korunur; mevcut bağlantı hesabın seçili ortamına göre çalışır.
- Fatura türü: Sipariş bilgilerinden uygun E-Arşiv/E-Fatura türü belirlenir.
- Fatura akışı: Sipariş bilgileri → yerel fatura taslağı → provider gönderimi → durum sorgusu → PDF arşivi → gerekiyorsa Trendyol teslimi.
- PDF: Provider belgesi hazır olduğunda özel depolamaya alınır ve panelde güvenli indirme bağlantısı verilir.
- Hassas bilgiler: Hesap parolası, token veya secret bu belgeye yazılmaz.

## 5. Mevcut kullanıcı akışları

### 5.1 Ürünler

- Ürünler ana ürün kartları altında listelenir.
- Model kodu ana ürün seviyesinde gösterilir.
- Varyantlar renk ve seçenek kırılımlarıyla açılır.
- Fiyat ve stok için ayrı hızlı güncelleme bağlantıları vardır.
- Toplu seçim ve toplu fiyat/stok işlemleri desteklenir.
- Platform eşleşmesi ikon ve renk göstergesiyle görünür.

### 5.2 Siparişler

- Varsayılan görünüm “Tümü” sekmesidir.
- Sipariş numarası, müşteri, ürün, stok/model/barkod, fiyat, kargo ve fatura alanları aynı satırda görünür.
- Kargo/paket bilgisi eksikse açıkça bekleme durumu gösterilir.
- Fatura işlemleri sipariş satırındaki menüden yapılır.
- Başarılı yerel fatura kaydı, Trendyol snapshot'ındaki eski `NOTINVOICED` değerine rağmen doğru şekilde “oluşturuldu” olarak gösterilir.

### 5.3 Fatura oluşturma

1. Sipariş satırından “Fatura Oluştur” seçilir.
2. Müşteri, adres, ürün, miktar ve KDV bilgileri kontrol edilir.
3. Fatura E-Faturam'a gönderilir.
4. Başarı sonucu gösterilir; oluşturma düğmesi kaldırılır ve pencere kapanır.
5. Fatura durumu sipariş listesinde görünür.
6. Arşivlenmiş PDF hazırsa “Faturayı Gör” tıklaması doğrudan PDF indirir.

PDF provider tarafından henüz arşivlenmediyse indirme bağlantısı hemen kullanılabilir olmayabilir; worker belgeyi aldıktan sonra tekrar denenmelidir.

## 6. Güvenlik ve veri bütünlüğü sınırları

Manuel çalışma modeline geçilmesi aşağıdaki teknik zorunlulukları kaldırmaz:

- Stage ve Production ortamları birbirine karıştırılmaz.
- Credential ve secret değerleri kaynak koda, loglara veya bu rapora yazılmaz.
- Migration dosyaları silinmez veya yeniden adlandırılmaz.
- Fatura ve dış sistem yazmaları idempotency ve provider yanıt kontrolüyle yürür.
- Özel fatura dosyaları public web kökünde tutulmaz.
- API, Worker ve PostgreSQL dış portları doğrudan internete açılmaz.
- Fatura ve sipariş işlemlerinde gerçek hata sonucu başarı gibi gösterilmez.
- Yıkıcı veri işlemleri açık kullanıcı talimatı olmadan çalıştırılmaz.

## 7. Bilinen sınırlar

- Provider tarafında PDF veya nihai durum gecikirse panelde belge bekleme durumu görülebilir.
- Dış provider API'sinin desteklemediği operasyonlar panelde aktif başarı olarak sunulmaz.
- Sunucu kapasitesi ve disk kullanımı büyüdükçe manuel olarak izlenmelidir.
- Yeni pazaryeri entegrasyonu henüz aktif kapsamda değildir.
- Bu belge test sonucu, release kanıtı veya ayrıntılı hata günlüğü değildir.

## 8. Yapılması düşünülenler

Öncelik ürün sahibinin panelden ilettiği taleplere göre değişebilir:

1. Sipariş ve fatura akışında provider durumlarının daha hızlı yenilenmesi.
2. PDF arşivlenme durumunun sipariş satırında daha görünür gösterilmesi.
3. Fatura ve kargo işlemlerinin günlük operasyonu kolaylaştıracak toplu aksiyonlara genişletilmesi.
4. Ürün/varyant ekranında fiyat, stok ve görsel düzenleme deneyiminin sadeleştirilmesi.
5. Dashboard üzerinde sipariş, fatura, stok ve entegrasyon özetlerinin geliştirilmesi.
6. Sunucu disk, worker gecikmesi ve provider bağlantı durumunun operasyon kartlarında gösterilmesi.
7. İhtiyaç oluştuğunda yeni pazaryeri adapterlarının değerlendirilmesi.

Planlanan işler kesin takvim değildir; ürün sahibinin yeni manuel talimatları önceliklidir.

## 9. Günlük çalışma kuralı

Kullanıcı panelden neyin değişeceğini iletir. Uygulama değişikliği yapılır, değişikliğin güvenli uygulanması için gerekli en dar kontrol gerçekleştirilir, `main` dalına gönderilir ve talep sunucu güncellemesini kapsıyorsa mevcut sunucu bilgileriyle yayınlanır. Ayrı faz belgesi, test sonucu dosyası, evidence günlüğü veya release raporu oluşturulmaz.

