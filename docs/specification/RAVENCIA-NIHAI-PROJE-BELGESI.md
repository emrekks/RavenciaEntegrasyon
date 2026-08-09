# RAVENCIA MARKETPLACEHUB

## Ana Proje Planı, Sistem Tasarımı, Kullanıcı İşleyişi, Uygulama Yol Haritası ve Durum Takip Belgesi

**Belge sürümü:** 7.5
**Belge tarihi:** 5 Ağustos 2026
**Belge statüsü:** Nihai ana proje planı ve yetkili teknik kaynak  
**Plan yaklaşımı:** Sistem başlangıçtan itibaren bu belgede tanımlanan kademeli kapsam ve mimariyle uygulanır  
**Güncel uygulama statüsü:** `F3_CORE_CODE_COMPLETE_VALIDATION_PENDING / F4_CODE_COMPLETE_VALIDATION_PENDING / PRODUCTION_BLOCKED`
**Aktif entegrasyon kapsamı:** Trendyol ve Trendyol E-Faturam  
**Ürün sahibi:** Ravencia  
**Sistem adı:** Ravencia MarketplaceHub

**v10.18 kullanıcı akışı notu:** Eşleştirme merkezi yalnız aktif Trendyol kapsamını sunar. Kategori ve marka sekmeleri aynı karşılıklı panel/Trendyol düzenini, aranabilir seçim kutularını ve güvenli yerel mapping kaydını kullanır. Panel kategorisi eşleştirme ekranından oluşturulabilir; bu işlem Trendyol'a dış yazma değildir.

---

# 1. Belgenin amacı ve kullanım şekli

Bu belge Ravencia MarketplaceHub'ın başlangıçtan production kullanımına kadar uygulanacak bağlayıcı ürün, teknik mimari, güvenlik, geliştirme, test ve operasyon planını tanımlar. Belge yalnız hedefleri sıralamaz; kullanıcı panelinin nasıl çalışacağını, arka plan süreçlerini, faz sırasını, tamamlanma ölçütlerini ve yeni platformların hangi kurallarla ekleneceğini de belirler.

Belgenin üç temel görevi vardır:

1. **Planlama:** Sistemin kapsamını, mimarisini, sunucu düzenini, kullanıcı işleyişini, güvenlik sınırlarını ve uygulama sırasını tek kaynaktan tanımlamak.
2. **Durum takibi:** Her işin planlanan, geliştirilen, kodlanan, test edilen, Stage'de doğrulanan veya production'a hazır durumunu kanıtla göstermek.
3. **Devralma:** Codex veya başka bir geliştiricinin repository'yi açtığında mevcut fazı, sıradaki işi, test zorunluluğunu ve değiştirilmemesi gereken sistem sınırlarını doğrudan anlayabilmesini sağlamak.

Bu belge yaşayan ana kaynaktır. Kapsam, faz veya uygulama durumu değiştiğinde ana plan, makinece okunabilir durum dosyası, aktif faz belgesi, capability matrisi, ilgili faz planı, evidence log, README ve değişiklik kaydı aynı dokümantasyon işlemi içinde birlikte güncellenir.

## 1.1 Yetkili kaynak sırası

Bir çelişki oluşursa aşağıdaki öncelik uygulanır:

1. `docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md`: bağlayıcı ana plan, hedef işleyiş ve değişmez sınırlar.
2. `docs/implementation/PROJECT-STATUS.yaml`: makinece okunabilir güncel durum kayıtları.
3. `docs/implementation/CURRENT-PHASE.md`: aktif faz, blokajlar ve sıradaki işler.
4. `docs/platform-rules/capability-matrix.md`: dış API yeteneklerinin kanıt durumu.
5. ADR belgeleri: yürürlükteki mimari kararların teknik ayrıntıları ve sınırları.
6. Faz planları ve evidence logları: uygulama adımları, test komutları ve kanıtlar.
7. Kaynak kod ve testler: mevcut fiili davranış.
8. `docs/CHANGELOG.md`: uygulanmış değişikliklerin kronolojik özeti.

Kaynak kod ile hedef belge arasında fark varsa bu fark gizlenmez. Kodun sunduğu durum “mevcut”, hedeflenen davranış “planlanan” olarak işaretlenir ve ilgili faza açık iş olarak eklenir.

# 2. Durum sözleşmesi ve tamamlanma tanımı

## 2.1 Kullanılacak durumlar

| Durum | Kesin anlamı |
|---|---|
| `PLANLANDI` | İş kapsamı ve kabul ölçütü tanımlandı; kod bulunmayabilir. |
| `GELİŞTİRİLİYOR` | Aktif çalışma var; sonuç henüz çıkış kapısını geçmedi. |
| `KODLANDI` | Kaynak kod mevcut; test veya dış sistem kanıtı olmayabilir. |
| `TEST_EDİLDİ_YEREL` | İlgili unit, integration veya contract testleri yerelde geçti. |
| `DOĞRULANDI_STAGE` | Resmî Stage/SIT ortamında gerçek kapsam ve credential ile senaryo geçti. |
| `PİLOT_PRODUCTION` | Sınırlı mağaza ve işlem hacminde kontrollü production kullanımı başladı. |
| `PRODUCTION_HAZIR` | Güvenlik, izleme, backup/restore, rollback, Stage ve iş kabul kapıları geçti. |
| `BLOKE_EXTERNAL` | Credential, resmî ortam, dış sağlayıcı veya iş onayı eksik. |
| `BLOKE_TEKNİK` | Kod, veri modeli, test veya operasyon açığı var. |
| `SONRAKİ_FAZ` | Mevcut faz tamamlandıktan sonra uygulanacak şekilde planlandı. |
| `KAPSAM_DIŞI` | Bu ürün sürümünün ve aktif yol haritasının parçası değildir. |

## 2.2 “Tamamlandı” denebilmesi için zorunlu şartlar

Bir görev yalnız aşağıdaki koşulların tamamı sağlandığında tamamlandı yazılabilir:

- Kabul kriteri açıkça tanımlanmış olmalıdır.
- İlgili kod ve gerekiyorsa migration değişikliği repository'de bulunmalıdır.
- Değişiklik etkisine uygun hedefli testler çalıştırılmalıdır.
- Başarısız veya çalıştırılamayan test saklanmamalıdır.
- Dış API yazma işlemi ise Stage/SIT kanıtı bulunmalıdır.
- Retry, duplicate, idempotency ve hata senaryosu doğrulanmalıdır.
- Gerekli audit, log ve operasyon görünürlüğü bulunmalıdır.
- İlgili evidence log ve capability satırı güncellenmelidir.
- Production etkisi varsa rollback ve backup/restore etkisi değerlendirilmelidir.

`HTTP 200/201`, ekranda buton görünmesi, adapter metodunun bulunması veya yalnız fake testin geçmesi tek başına tamamlanma değildir.

# 3. Projenin amacı ve kademeli kapsam modeli

Ravencia MarketplaceHub; ürün, varyant, stok, fiyat, sipariş, paket, kargo, iade ve fatura süreçlerini tek bir güvenli web panelinden yönetmek için geliştirilir. Sistem Ravencia'nın kendi sunucusunda çalışır ve dış platformlarla API üzerinden haberleşir.

Proje başlangıçtan itibaren kademeli teslim modeliyle uygulanır:

1. Trendyol ürün, stok, fiyat, sipariş, paket, kargo ve iade süreçleri tamamlanır.
2. Trendyol E-Faturam üzerinden siparişe göre otomatik E-Fatura/E-Arşiv oluşturma, durum takibi, PDF saklama, iptal ve Trendyol'a fatura teslimi tamamlanır.
3. İki entegrasyon Stage kanıtı, production pilotu ve operasyon stabilizasyonundan geçirilir.
4. Platform Adapter Registry/Resolver katmanı sertleştirilir.
5. Hepsiburada, N11, Pazarama, PTTAVM ve Shopify ihtiyaç sırasına göre tek tek eklenir.

Hedef kullanıcı deneyimi:

- Kullanıcı panele tarayıcıdan güvenli biçimde giriş yapar.
- Ürünlerini tek katalogda yönetir.
- Trendyol'dan ürünleri içeri alır veya onaylı ürünleri Trendyol'a gönderir.
- Stok ve fiyatları merkezden günceller.
- Siparişleri tek listede görür ve ayrıntılarına ulaşır.
- Siparişi işleme alır, paket ve kargo işlemlerini yürütür.
- Desteklenen koşullarda kargo firmasını değiştirir.
- Kargo etiketini görüntüler ve yazdırır.
- Sipariş için E-Fatura veya E-Arşiv oluşturur.
- Fatura durumunu, PDF belgesini ve Trendyol'a teslim sonucunu takip eder.
- Hatalı işlemleri operasyon merkezinden güvenli biçimde yeniden dener.
- Platform erişim bilgilerini şifreli ve audit edilebilir biçimde yönetir.

Sistem normal bir sanal sunucuda Docker ile taşınabilir biçimde çalışır. Ücretli veya güvenilirliği doğrulanmamış eklentiler temel iş akışının zorunlu bağımlılığı yapılmaz.

# 4. Bağlayıcı sistem ve kapsam kararları

## 4.1 Aktif entegrasyon sırası

- Aktif geliştirme kapsamı yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM`dır.
- Bu iki entegrasyon production stabilizasyonunu tamamlamadan yeni platform geliştirmesi başlatılmaz.
- Sonraki platformlar ortak domain ve job altyapısını kullanır; platforma özgü adapter, mapping, capability ve contract testleri ayrı geliştirilir.
- Yeni platform eklemek için mevcut Trendyol akışının davranışı değiştirilmez.

## 4.2 Mimari ve altyapı kararları

- Backend .NET tabanlı modüler monolittir.
- API ve Worker ayrı process/container olarak çalışır.
- PostgreSQL iş verisinin, job kayıtlarının, inbox/idempotency kayıtlarının, audit bilgisinin ve migration zincirinin tek otoritesidir.
- Web paneli React tabanlıdır ve API ile güvenli HTTPS üzerinden haberleşir.
- Production hostu Ubuntu Server üzerinde Docker Engine ve Docker Compose kullanır.
- Caddy HTTPS termination ve reverse proxy katmanıdır.
- Mikroservis, Kubernetes, Kafka, RabbitMQ, Redis veya service mesh mevcut ölçek için zorunlu değildir ve ihtiyaç kanıtlanmadan eklenmez.

## 4.3 Değişmez güvenlik ve veri bütünlüğü kuralları

- Dış yazma işlemleri varsayılan olarak kapalıdır.
- Bir dış yazma ancak global switch, bağlantı switch'i, environment ve capability birlikte izin verirse çalışır.
- Uygulanmış migration dosyaları silinmez veya yeniden adlandırılmaz.
- Hassas dosyalar public web root içinde tutulmaz.
- Credential ve secret değerleri Git'e, loglara veya kullanıcıya dönen hata mesajlarına yazılmaz.
- Her mali ve dış sistem işlemi correlation, audit ve idempotency kaydı üretir.
- Test ve gerekli Stage kanıtı olmadan hiçbir özellik tamamlandı veya production-ready işaretlenmez.
- Ana geliştirme repository'sinde Git geçmişi korunur; deployment paketine `.git` dahil edilmez.

# 5. Sunucu, çalışma ortamı ve altyapı planı

## 5.1 Geliştirme ortamı

- Geliştirme Windows bilgisayarda yapılabilir.
- Kod doğrudan Windows üzerinde veya uygun geliştirme araçlarıyla düzenlenir.
- Linux container davranışı Docker üzerinden doğrulanır.
- Yerel sonuç production kanıtı değildir; sunucuda tekrar doğrulama gerekir.
- Secret, gerçek credential, PostgreSQL data klasörü ve üretilmiş dosyalar Git'e eklenmez.

## 5.2 Güncel production sunucu kararı

Bağlayıcı başlangıç hedefi mevcut AWS sunucusudur:

- Ubuntu Server 26.04 LTS x86_64
- 2 vCPU
- 8 GB sınıfı RAM
- 80 GB NVMe sınıfı disk
- Statik public IPv4
- Production domain ve Caddy üzerinden HTTPS
- Docker Engine ve Docker Compose plugin

Bu kapasite başlangıç pilotu içindir. Disk büyümesi, queue gecikmesi, CPU, memory ve x5 yük profili ölçülür; yetersiz kalırsa mimari değişmeden instance veya disk büyütülür.

## 5.3 Container topolojisi

| Bileşen | Görev | Dış erişim |
|---|---|---|
| Caddy | HTTPS, reverse proxy, güvenli edge | Yalnız 80/443 |
| Web/API | Panel statik içeriği ve uygulama API'si | Caddy üzerinden |
| Worker | Uzun süren sync, batch, fatura, retry ve reconciliation | Dış port yok |
| PostgreSQL | İş verisi, job, audit, idempotency | Yalnız internal network |
| Private file volume | Fatura, etiket ve özel dosyalar | Doğrudan public değil |

## 5.4 Sunucu güvenliği

- SSH parola yerine anahtar ile yapılır.
- SSH erişimi mümkünse yönetici IP allow-list veya VPN ile sınırlandırılır.
- Root ile günlük çalışma yapılmaz; yetkili sudo kullanıcısı tercih edilir.
- API, Worker ve PostgreSQL portları public internete açılmaz.
- UFW/security group yalnız gerekli portlara izin verir.
- Container image'ları mutable tag yerine `name@sha256:...` digest ile sabitlenir.
- Secret değerleri environment örneğinde bulunmaz; runtime secret dosyası veya güvenli secret mekanizmasıyla verilir.
- İşletim sistemi ve Docker güncellemeleri kontrollü bakım penceresinde yapılır.

## 5.5 Backup ve kurtarma

- PostgreSQL dump pilotta en fazla 6 saatlik RPO hedefiyle planlanır.
- Private files, Data Protection key ring ve gerekli Caddy verileri birlikte yedeklenir.
- Aynı sunucudaki kopya hızlı operasyon yedeğidir; gerçek felaket kurtarma değildir.
- Production kabulü için şifreli off-host ve farklı failure domain'de kopya gerekir.
- Backup alınması başarı sayılmaz; temiz hedefe restore ve smoke testi yapılır.
- Restore kanıtında checksum, kayıt sayısı, kritik iş ilişkileri ve private dosya erişimi doğrulanır.

# 6. Sistem çalışma modeli

## 6.1 Ön plandaki kullanıcı işlemi ile arka plandaki süreç ayrımı

Kullanıcı panelde bir butona bastığında uzun süren dış API işlemi doğrudan tarayıcı isteği içinde tamamlanmış gibi gösterilmez. Genel akış şöyledir:

1. Panel gerekli alanları doğrular ve kullanıcıya işlem özetini gösterir.
2. API yetki, tenant, bağlantı, environment, capability ve kill-switch kontrollerini yapar.
3. İşlem için benzersiz idempotency anahtarı oluşturulur veya kullanıcı isteğinden alınır.
4. PostgreSQL'e durable job kaydı yazılır.
5. API kullanıcıya `Kuyruğa alındı` ve iş kimliği döndürür.
6. Worker işi lease ile alır ve dış platforma çağrı yapar.
7. Dış platform batch kimliği döndürürse işlem `SUBMITTED` olarak saklanır.
8. Poll/reconciliation işi terminal sonucu bekler.
9. Satır bazlı başarı ve hata bilgileri kaydedilir.
10. Panel job durumunu yeniler ve kullanıcıya gerçek sonucu gösterir.

Bu model sayesinde tarayıcı kapanması, API restartı veya geçici ağ hatası işi kaybettirmez.

## 6.2 Başarı ve hata görünürlüğü

Kullanıcıya yalnız teknik hata kodu gösterilmez. Her işlem için şu bilgiler bulunmalıdır:

- İşlemin adı ve kaynağı
- Başlatan kullanıcı
- Başlangıç ve son güncelleme zamanı
- Yerel kayıt kimliği
- Uzak batch/belge/paket kimliği
- Durum: bekliyor, çalışıyor, uzak yanıt bekliyor, tamamlandı, kısmi hata, retry, bloke, iptal
- Güvenli hata özeti
- Tekrar deneme zamanı ve deneme sayısı
- Kullanılabilir kullanıcı aksiyonu
- Audit/correlation kimliği

# 7. Kullanıcı paneli: uçtan uca işleyiş

Bu bölüm hedef kullanıcı deneyimini anlatır. Her özellik ayrıca mevcut uygulama durumu ile işaretlenir. Planlanan fakat API capability kanıtı olmayan aksiyonlar ekranda aktif buton olarak gösterilmez.

## 7.1 Giriş, parola ve MFA

**Kullanıcının gördüğü süreç:**

1. Kullanıcı güvenli giriş sayfasını açar.
2. E-posta/kullanıcı adı ve parolasını girer.
3. İlk giriş ise zorunlu parola değiştirme ekranına yönlendirilir.
4. MFA aktifse Authenticator kodu veya recovery code istenir.
5. Başarılı giriş sonrası kullanıcı dashboard'a gider.
6. Başarısız girişlerde hassas ayrıntı verilmez; rate limit uygulanır.

**Mevcut durum:** Giriş, ilk parola değişikliği, TOTP challenge ve recovery code altyapısı kodlanmıştır; production kullanıcı/rol politikası ve operasyon kabulü tamamlanmalıdır.

## 7.2 Dashboard

Dashboard kullanıcının sabah ilk açacağı operasyon ekranıdır. Hedef kartlar:

- Trendyol bağlantı sağlığı
- E-Faturam bağlantı sağlığı
- Son sipariş eşitleme zamanı
- Yeni ve işlem bekleyen sipariş sayısı
- Hazırlanan/kargoda/teslim sipariş sayıları
- Fatura bekleyen paketler
- Hatalı veya retry bekleyen faturalar
- Ürün yayınlama batch sonuçları
- Stok-fiyat eşitleme gecikmesi
- İade aksiyon süresi yaklaşan kayıtlar
- Dead-letter ve reconciliation farkları
- Backup yaşı ve son restore kanıtı
- External write anahtarlarının durumu

Kartlara tıklanınca ilgili filtrelenmiş liste açılmalıdır. “Sistem aktif” göstergesi yalnız API ayakta diye yeşil olmamalı; veritabanı, Worker ve kritik entegrasyon gecikmeleri de hesaba katılmalıdır.

## 7.3 Platform bağlantıları

Kullanıcı Ayarlar > Platformlar ekranından:

1. Trendyol veya Trendyol E-Faturam bağlantısı seçer.
2. Stage veya Production ortamını belirler.
3. Satıcı/firma kapsam kimliğini girer.
4. Credential değerlerini güvenli formda kaydeder.
5. “Bağlantıyı test et” işlemini çalıştırır.
6. Son test zamanı, hata kodu ve capability listesini görür.
7. Bağlantıyı yalnız test başarıyla geçince `VERIFIED` yapar.
8. Dış yazma için ayrıca ayrı etkinleştirme onayı verir.

Credential değerleri kaydedildikten sonra panelde tekrar açık biçimde gösterilmez. Değişiklik gerekiyorsa credential döndürme işlemi yapılır ve audit kaydı oluşur.

## 7.4 Referans veri ve eşleştirme

Trendyol'a ürün göndermeden önce kullanıcı:

1. Kategori ağacını eşitler.
2. Yalnız `leaf` kategorileri ürün kategorisi olarak seçebilir.
3. Marka listesini eşitler.
4. Seçili kategori için zorunlu/opsiyonel özellikleri çeker.
5. Özellik değerlerini eşitler.
6. Yerel kategori, marka, özellik ve değerleri Trendyol kimlikleriyle eşler.
7. Snapshot'ın güncelliğini ve doğrulanma tarihini görür.
8. Eski snapshot ile hazırlanmış yayın işlemi varsa sistem yeniden doğrulama ister.

Panelde kategori kapsamı seçildikten sonra özellik ve özellik değeri eşleme adımları aynı çalışma alanında ardışık olarak açılır. Özellik değeri bölümü, özellik eşlemesi doğrulanmadan gösterilmez. Marka eşlemesi aynı güvenli snapshot kurallarını kullanır ancak ayrı görünümde tutulur.

Kategori kırılımının ara düğümleri ürün yayınında seçilemez. Bu, yarım kategori seçimi problemini önleyen zorunlu kuraldır.

**Mevcut durum:** Kategori, marka, kategori-kapsamlı özellik ve özellik değeri eşleme API/UI akışları kodlanmıştır. Vitest senaryosu birleşik kategori–özellik–değer akışına, Playwright kabuk senaryosu güncel menü ve rol görünürlüğüne göre yenilenmiştir. Exact Node/npm ortamı kurulamadığı için bu değişikliklerin dinamik frontend doğrulaması `BLOCKED_ENVIRONMENT` durumundadır; Stage referans verisi ve gerçek credential doğrulaması ayrıca bekler.

## 7.5 Ürün listesi ve ürün detayı

Ürün listesinde hedef alanlar:

- Ürün adı
- Ana ürün kodu
- Varyant SKU ve barkodları
- Marka ve yerel kategori
- Aktif/pasif durumu
- Toplam ve kullanılabilir stok
- Merkezi satış fiyatı
- Trendyol yayın durumu
- Son batch sonucu
- Eksik eşleme/özellik uyarısı
- Son güncelleme zamanı

Kullanıcı ürün detayında:

- Ürün adını ve açıklamasını düzenler.
- Marka ve kategori seçer.
- Renk, beden ve diğer varyant özelliklerini tanımlar.
- SKU, barkod, stok, liste fiyatı ve satış fiyatını girer.
- Görselleri ekler, sıralar ve doğrular.
- Trendyol zorunlu özelliklerini tamamlar.
- Yayın öncesi doğrulama raporunu görür.
- Uygun olduğunda yayınlama işini kuyruğa alır.
- Uzak batch sonucunu satır bazında izler.
- Hatalı satırı düzelterek yalnız ilgili ürün/varyantı yeniden gönderir.

**Mevcut durum:** Yerel katalog ve eşleme çekirdeğine ek olarak Trendyol Product Create application orkestrasyonu kodlanmıştır. Yayın isteği yalnız `PRODUCT_WRITE=SUPPORTED`, global ve bağlantı bazlı dış yazma anahtarları açık, listing profile etkin, eşlemeler güncel/doğrulanmış, varyant barkod-SKU-model kodları geçerli, aktif TRY teklifi ve MAIN stok kaydı mevcut ve Trendyol tarafından erişilebilir kalıcı HTTPS görsel URL'si kayıtlı olduğunda durable job üretir. Private product-media upload tek başına uzak görsel URL'si sayılmaz; kalıcı URL `/api/v1/files/product-media-url` ile kaydedilir.

Worker create çağrısını `SUBMIT -> POLL` durum makinesiyle yürütür. Dış çağrıdan önce external-effect fence oluşturur; sonucu belirsiz ağ/5xx/contract durumunda otomatik ikinci create yerine `MANUAL_REVIEW` uygular. Batch sonucu varyant barkoduna göre `CREATE_ACCEPTED` veya `CREATE_REJECTED` olarak kaydedilir; kısmi sonuç `PARTIAL_FAILURE`, tam kabul ise yalnız `APPROVAL_PENDING` durumudur. `APPROVAL_PENDING`, ürünün Trendyol'da yayında olduğu anlamına gelmez. Kullanıcı `/api/v1/products/{productId}/publication-status/{connectionId}` üzerinden profile, job ve satır durumlarını okuyabilir.

Create batch içinde en az bir kabul edilen varyant varsa ayrı `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` işi otomatik oluşur. Batch aşamasında `CREATE_REJECTED` olan satırlar read-back dışında tutulur ve mevcut ret kodları korunur. Worker her barkodu önce onaylı ürün servisinde, bulunmazsa onaysız ürün servisinde sorgular. Onaylı satırın `contentId` ve `variantId` kimlikleri yerel ürün/varyant linklerine idempotent biçimde kaydedilir ve satır `LIVE` olur. `pendingApproval` veya iki listede de henüz görünmeyen barkod `APPROVAL_PENDING` olarak kalıp yeniden denenir; `rejected` satırı ret koduyla `REJECTED` olur. `archived`, `locked`, `blacklisted`, bilinmeyen durum veya mevcut yerel/uzak kimlik çatışması sessiz yeniden bağlama yapmadan `MANUAL_REVIEW` sınırına taşınır. Yedi günlük deadline Trendyol onay SLA'sı değil, sonsuz otomatik polling'i önleyen yerel operasyon korumasıdır. Onay işi başlamadan güncel listing-state payload hash değeri kontrol edilir; daha yeni bir yayınlama denemesi varsa eski iş `PRODUCT_APPROVAL_SUPERSEDED` ile dış sorgu ve durum değişikliği yapmadan durur. Deadline, contract veya kimlik hataları da önceki `CREATE_REJECTED` satırlarının kanıtını ezmez. Bu read-back işi yeni bir dış yazma üretmez.

Product Update, uzak archive/unarchive, birleşik fiyat-stok batch, Order V2/stream read, paket aksiyonları, ortak etiket, iade approve/reject/evidence/read-back, capability evidence API/UI ve ilgili operatör ekranları kodlanmıştır. Bütün dış yazmalar capability, global anahtar, bağlantı anahtarı, idempotency ve external-effect fence ile kapatılır; write sonucu uygun read-back olmadan kesin başarıya yükseltilmez. Exact .NET/PostgreSQL/Docker testleri ve gerçek Trendyol Stage safe-write/read-back kanıtı bulunmadığından F3 production kapanışı yapılmamıştır.

## 7.5.1 Trendyol Türkiye CORE operasyon kapanışı

Kodlanan CORE kapsamı şunlardır:

- Connection, credential, capability probe ve tarihli Stage/SIT evidence kaydı.
- Kategori, marka, kategori özelliği ve değer snapshot/eşleme akışı.
- Product V2 approved/unapproved read, create, update, archive/unarchive, batch poll ve approval reconciliation.
- Birleşik fiyat-stok batch ve sürüm kontrollü sonuç uzlaştırması.
- Order V2 tekil read, order stream cursor, idempotent order/package upsert ve webhook ingress.
- Capability ile sınırlandırılmış paket aksiyonları; `PICKING` ve `INVOICED` satıcı durumları, `TRACKING_NUMBER`, iptal/split/cargo/alternative/manual akışları yalnız kanıtlı action listesinde görünür. `Shipped` ve `Delivered` satıcı tarafından uydurulmaz; uzak kargo hareketinden okunur.
- Ortak etiket create/poll/private storage akışı.
- İade read, exact claim read-back, approve/reject, zorunlu özel kanıt dosyası ve karar uzlaştırması.
- Trendyol invoice-link teslimi `SUBMITTED` ara durumu ve manuel teyit sınırı. Resmî terminal query kanıtı olmadan sahte `CONFIRMED` üretilmez.
- Ürün, envanter, shipment, return ve capability evidence operatör ekranları.

Bu kapanış `storeFrontCode=TR` ve ürün payload'ında `channels=["CORE"]` ile Türkiye CORE mağaza kapsamıdır. `LUXE`, uluslararası storefront veya farklı kanal kimlikleri ayrı ADR, capability evidence, fixture ve kabul testi olmadan etkin değildir. Trendyol E-Faturam mali sağlayıcı kapanışı F4'tür ve F3 CORE kod kapanışına dahil değildir.

**Durum:** `CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`.

## 7.6 CSV/XLSX içe aktarma

Kullanıcı toplu ürün aktarımında:

1. CSV veya XLSX import oturumu açar.
2. Dosyayı private storage'a yükler.
3. Dosya başlıklarını sistem alanlarıyla eşler.
4. Preview job çalıştırır.
5. Geçerli, hatalı ve şüpheli satırları görür.
6. Her aday için `Yeni oluştur`, `Mevcutla eşle` veya `Atla` kararı verir.
7. Hataları CSV olarak indirir.
8. Onay sonrası apply job çalıştırır.
9. Sonuçları ve oluşturulan ürün kimliklerini görür.

Aynı dosyanın veya satırın tekrar uygulanması duplicate ürün oluşturmamalıdır.

## 7.7 Stok ve fiyat yönetimi

Kullanıcı stok ekranında SKU, depo, eldeki, rezerve ve kullanılabilir miktarı görür. Manuel stok düzeltmesinde neden girmek zorundadır ve audit kaydı oluşur.

Hedef Trendyol akışı:

1. Kullanıcı bir veya daha fazla varyantın stok/fiyatını değiştirir.
2. Sistem merkezi stok ve fiyat otoritesini günceller.
3. Yalnız `LIVE` ve uzak kimliği bağlı varyantlar, aktif TRY teklifleri ve MAIN stok projection'ları deterministic payload'a girer.
4. Fiyat ve stok tek `/price-and-inventory` batch işiyle gönderilir.
5. İş, gönderilen `offerId`, `priceVersion`, `projectionVersion` ve payload hash kanıtlarını saklar.
6. Batch sonucu satır bazında işlenir; daha yeni yerel fiyat veya stok sürümü varsa eski sonuç uygulanmaz ve uzlaştırma gerekir.

**Mevcut durum:** Birleşik fiyat-stok composer, durable job, external-effect fence, batch polling, stale-version koruması ve panel tetikleyicisi kodlanmıştır. Dynamic/PostgreSQL ve Stage write testi bekler.
3. Trendyol'a gönderilecek değişiklikler tek birleşik `price-and-inventory` job'ında gruplanır.
4. Batch kimliği saklanır.
5. Uzak sonuç tamamlanana kadar “başarılı” yazılmaz.
6. Kısmi hatalı satırlar ayrı gösterilir.
7. Uzak ve yerel değer farkı reconciliation ekranında görünür.

Safety stock başlangıçta `0`, ana depo kodu `MAIN`dir. Bu iş kararları açık onay olmadan otomatik değiştirilmez.

## 7.8 Sipariş listesi

Sipariş ekranı yalnız kayıt göstermek için değil, günlük operasyonun merkezi olmak için tasarlanır.

Kullanıcı listede şunları görür:

- Sipariş numarası ve tarih
- Sipariş/paket durumu
- Ürün satırı ve toplam adet
- Ürün adları veya ilk ürün özeti
- Brüt, indirim ve net tutar
- Para birimi ve KDV bilgisi
- Paket sayısı
- Kargo firması ve takip numarası
- Fatura durumu
- İade/iptal işareti
- Son senkronizasyon zamanı

Kullanıcı sipariş numarası, SKU, barkod, ürün adı, paket kimliği veya takip numarasıyla arama yapabilmelidir. Durum, tarih, fatura, kargo ve hata filtresi bulunmalıdır. Toplu dışa aktarma yalnız yetkili role açılır.

**Mevcut durum:** Sipariş listesi; kompakt açık/gelişmiş filtreleri, arama, tutar, satır ve paket sayısı görünümünü aynı operasyon yüzeyinde sunar. Müşteri, teslimat/fatura adresi, ürün/SKU/barkod, brüt-indirim-net tutar ve kargo paketi aynı satır düzeninde gösterilir. Satır işlem menüleri görünür alana göre aşağı/yukarı yönlenir. Mikro ihracat öncelikle resmî platform alanlarından; bu alanları taşımayan tarihsel Stage kayıtlarında yalnız belgelenmiş PM3–Arvato partner kimliğinden türetilir ve sipariş numarası sabitlenmez. Mikro ihracat satırı mavi çizgiyle ayrılır ve kısa “Mikro ihracat” rozeti yalnız fatura sütununda gösterilir. Termin alanı yoksa tarih uydurulmaz ve uzak veri eksikliği açıkça belirtilir. Sol menü masaüstünde ikon görünümüne daraltılabilir; sipariş detayında liste bilgisini tekrarlayan büyük özet kartı kullanılmaz. Aktif kapsam yalnız Trendyol olduğundan “tüm kanallar” gibi yanıltıcı metinler kullanılmamalıdır.

## 7.9 Sipariş detayı

Kullanıcı sipariş detayında aşağıdaki bölümleri görür:

### Sipariş özeti

- Sipariş numarası ve zamanı
- Trendyol ham durumu ve sistem türetilmiş durumu
- Brüt tutar, indirim, net tutar ve para birimi
- Fatura adresi ve teslimat adresi; yalnız yetkili roller için
- Müşteri bilgileri, PII maskeleme kurallarına uygun biçimde
- Sipariş kaynak bağlantısı ve son sync bilgisi

### Ürün satırları

- Ürün adı
- SKU ve barkod
- Sipariş adedi
- İptal edilen, gönderilen, teslim ve iade adetleri
- Birim fiyat
- Satır indirimi
- KDV oranı
- Satırın platform durumu

### Paket ve kargo

- Shipment package kimliği
- Paket içindeki satırlar
- Mevcut kargo firması
- Takip numarası ve takip bağlantısı
- Paket durumu ve durum zamanı
- Etiket/çıktı durumu
- Fatura durumu

## 7.10 Siparişi işleme alma ve paket operasyonu

Hedef operasyon sırası:

1. Yeni sipariş sisteme gelir ve duplicate kontrolünden geçer.
2. Kullanıcı siparişi açar ve ürün/stok uygunluğunu kontrol eder.
3. Platform capability destekliyorsa “İşleme al” aksiyonu görünür.
4. Kullanıcı paket içeriğini doğrular.
5. Desteklenen durum geçişi job olarak kuyruğa alınır.
6. Trendyol sonucu alınmadan yerel durum terminal başarıya geçirilmez.
7. Başarısızlıkta güvenli hata ve yapılabilecek aksiyon gösterilir.

Sipariş durumları geri alınamaz veya platformun izin vermediği sıraya geçirilemez. UI, capability ve mevcut statüye göre butonları oluşturur.

**Mevcut durum:** Sipariş ve paket okuma omurgası vardır. Siparişi işleme alma/paket write aksiyonları capability kanıtı ve Stage testleri tamamlanana kadar `PLANLANDI / BLOKE` durumundadır.

## 7.11 Kargo firmasını değiştirme

Kargo değişikliği her siparişte serbest bir alan değildir. Hedef davranış:

1. Sistem yalnız ilgili paket durumunda ve Trendyol'un desteklediği capability varsa “Kargoyu değiştir” butonunu gösterir.
2. Kullanıcı desteklenen kargo firmaları listesinden seçim yapar.
3. Değişiklik öncesi mevcut ve yeni kargo bilgisi gösterilir.
4. Onay sonrası durable job oluşturulur.
5. Uzak sonuç doğrulanır ve paket tekrar okunur.
6. Audit kaydında eski/yeni değer, kullanıcı ve dış yanıt bulunur.

**Durum:** Endpoint ve durum doğrulaması kodlandı; buton yalnız bağlantıdaki tarihli Stage/SIT capability evidence içinde aksiyon izinliyse gösterilir. Evidence yoksa capability `UNKNOWN` ve dış yazma kapalıdır.

## 7.12 Kargo etiketi ve çıktı alma

Hedef etiket akışı:

1. Kullanıcı paket satırında “Etiket oluştur/indir” seçeneğini görür.
2. Sistem platformun sunduğu etiket formatını ve boyutunu doğrular.
3. Etiket dosyası Worker tarafından alınır veya güvenli biçimde üretilir.
4. Dosya tipi, boyutu ve imzası doğrulanır.
5. Private storage'a kaydedilir.
6. Kullanıcı A4 veya desteklenen termal formatta ön izleme/indirme yapar.
7. Yeniden yazdırma audit kaydına işlenir.

**Durum:** Ortak etiket create/get, private storage, checksum ve sürümlü shipment document kaydı kodlandı. UI yalnız `LABEL_READ` + `LABEL_WRITE` evidence ve desteklenen format bulunduğunda aksiyonu gösterir; Stage kabulü olmadan production kapalıdır.

## 7.13 Fatura kesme: kullanıcı akışı

Sipariş veya paket detayında kullanıcı “Fatura oluştur” seçeneğini kullanır. Hedef süreç:

1. Sistem E-Faturam bağlantısının aktif, credential'ın mevcut ve capability'nin supported olduğunu kontrol eder.
2. Aynı sipariş/paket için mevcut fatura veya devam eden job aranır.
3. Müşterinin e-Fatura mükellefi olup olmadığı sorgulanır.
4. Sonuca göre e-Fatura veya e-Arşiv türü seçilir; kullanıcıya gerekirse neden gösterilir.
5. Ürün satırları, miktar, fiyat, indirim, KDV, adres ve mali alanlar hazırlanır.
6. Kuruş/yuvarlama kontrolleri yapılır.
7. Kullanıcı ön izleme veya özet doğrulaması görür.
8. “Onayla ve gönder” sonrası idempotent invoice job oluşur.
9. E-Faturam belge kimliği saklanır.
10. Belge durumu terminal sonuca kadar poll/reconciliation ile izlenir.
11. Kalıcı PDF URL alınır; URL güvenlik kontrollerinden geçirilir.
12. PDF private storage'a indirilir ve bütünlük bilgisi kaydedilir.
13. Trendyol paketine fatura linki gönderilir.
14. Trendyol teslim sonucu ayrıca doğrulanır.
15. Kullanıcı fatura numarası, türü, durum, PDF, teslim ve hata bilgilerini görür.

“Fatura oluştur” butonuna basılması faturanın başarıyla kesildiği anlamına gelmez. Kullanıcıya `Taslak`, `Kuyrukta`, `Gönderildi`, `Sağlayıcı işliyor`, `Başarılı`, `Başarısız`, `İptal bekliyor`, `İptal edildi`, `Trendyol'a teslim bekliyor` gibi ayrı durumlar gösterilir.

**Mevcut durum:** Doğrudan API_USER kimlik doğrulaması, sign-in tokenından company/user scope, Trendyol siparişinden otomatik TEMELFATURA/EARSIVFATURA seçimi, otomatik internet satışı ödeme/taşıyıcı alanları, numeric provider status uzlaştırması, güvenli kalıcı PDF, E-Arşiv iptali ve Trendyol link teslimi kodlanmıştır. Gönderen mali hesap, seri, senaryo, ödeme ve kargo eşleme ayarları panelde tutulmaz. Giden E-Fatura status endpointi exact Stage/SIT kanıtı olmadan fail-closed kalır. Exact runtime ve gerçek Stage mali E2E tamamlanmadığı için production bloke durumdadır.

## 7.14 Fatura listesi ve fatura detayı

Fatura listesinde:

- Fatura numarası
- Sipariş ve paket numarası
- e-Fatura/e-Arşiv türü
- Tutar
- Yerel durum
- E-Faturam uzak durumu
- Trendyol teslim durumu
- Oluşturma ve son kontrol zamanı
- Hata/retry durumu

Fatura detayında:

- Mali belge alanları
- Satır toplamları ve yuvarlama özeti
- Provider request/response için hassas veriden arındırılmış özet
- PDF indirme/ön izleme
- Trendyol'a link gönderme veya yeniden uzlaştırma
- Capability ve mali kurallar izin veriyorsa iptal
- Tüm durum geçmişi ve audit kaydı

## 7.15 İade yönetimi

Kullanıcı iade listesinde claim kimliği, sipariş numarası, neden, durum ve son aksiyon süresini görür. İade detayında platform ham durumu ve kullanılabilir aksiyonlar gösterilir.

İade onaylama, reddetme veya anlaşmazlık aksiyonları ayrı capability olarak ele alınır. Stage kanıtı olmayan aksiyonlar disabled gösterilir; kullanıcıya neden kapalı olduğu açıklanır. İade stoğa yalnız kalite sonucu `PASS` ise döner.

## 7.16 Operasyon merkezi ve hata kuyruğu

Yetkili kullanıcılar:

- Retry bekleyen işleri
- Dead-letter kayıtlarını
- Uzun süredir çalışan jobları
- Uzak batch sonucu bekleyen işlemleri
- Duplicate/idempotency engellerini
- Reconciliation farklarını
- Bağlantı hatalarını
- Fatura teslim problemlerini

görebilir. Kullanıcı yalnız güvenli aksiyonları çalıştırabilir: tekrar dene, uzlaştır, dış yazmayı kapat, iş detayını incele veya teknik incelemeye işaretle. Ham credential veya hassas response body gösterilmez.

# 8. Yapım ve geliştirme yöntemi

## 8.1 Çalışma sırası

Her özellik aşağıdaki sırayla geliştirilir:

1. İş gereksinimi ve kullanıcı akışı yazılır.
2. Capability ve resmî API sözleşmesi doğrulanır.
3. Threat model ve veri etkisi değerlendirilir.
4. Domain/application sözleşmeleri hazırlanır.
5. Persistence ve migration gerekiyorsa eklenir.
6. Adapter ve dış çağrı uygulanır.
7. Worker/job/retry/reconciliation akışı tamamlanır.
8. API endpoint ve yetki kontrolü eklenir.
9. Panel ekranı ve durum görünürlüğü geliştirilir.
10. Hedefli testler çalıştırılır.
11. Stage kanıtı alınır.
12. Dokümantasyon işlemi tamamlanır.
13. Faz çıkış kapısı geçirilir.

UI önce yapılıp arka planı boş bırakılmaz. Adapter metodu bulunup application akışı olmadan özellik tamamlandı sayılmaz.

## 8.2 Kod değişikliği sınırları

- Küçük işler küçük ve izlenebilir commitlere ayrılır.
- Bir commit tek mantıksal amacı taşır.
- Migration, model ve test aynı değişiklik grubunda tutulur.
- Uygulanmış migration yeniden yazılmaz; yeni düzeltme migration'ı eklenir.
- Ortak port değişikliği bütün adapter etkileri incelenmeden yapılmaz.
- Yeni dependency eklemek için gerekçe, sürüm sabitleme ve supply-chain değerlendirmesi gerekir.

# 9. Test ve doğrulama stratejisi

## 9.1 Testsiz başarı yasaktır

Çalıştırılmamış test “geçti” olarak yazılamaz. Ortam veya credential yoksa durum `NOT_RUN`, `BLOCKED_EXTERNAL` veya `BLOCKED_ENVIRONMENT` olarak kaydedilir. Bir test başarısızsa hata düzeltilmeden capability veya faz durumu yükseltilmez.

## 9.2 Token tasarruflu fakat güvenilir test düzeni

Codex'in her küçük değişiklikten sonra bütün solution logunu konuşma bağlamına taşıması gereksiz token tüketir. Bunun yerine değişiklik etkisine göre katmanlı doğrulama yapılır:

### Seviye A - Hızlı ön kontrol

Her anlamlı değişiklikte:

- Format/syntax kontrolü
- İlgili proje build'i
- Değişen modülün hedefli unit testleri
- Repository ve dokümantasyon tutarlılık kontrolü

### Seviye B - Modül doğrulaması

Bir iş tamamlanırken:

- İlgili test projesi
- Gerekli integration/contract test filtresi
- İlgili web typecheck ve component testleri
- Migration/schema kontrolü

### Seviye C - Faz/commit çıkış kapısı

Faz kapanışı, release adayı, tag veya production öncesinde:

- Locked restore
- Tüm solution build
- Tüm backend testleri
- Format doğrulaması
- Web typecheck, test ve build
- Docker build/smoke
- Repository temizliği
- Dokümantasyon transaction doğrulaması

### Seviye D - Dış sistem kabulü

- Stage read testleri
- Açık onaylı güvenli write testi
- Duplicate/retry/idempotency testi
- Batch polling ve partial failure testi
- Gerçek PDF/etiket dosya güvenliği testi
- Rollback/reconciliation testi

## 9.3 Token kullanım ilkeleri

- Test komutunun tam çıktısı dosyaya kaydedilir; sohbete yalnız özet, exit code, test sayısı, başarısız test adı ve evidence yolu yazılır.
- Başarılı binlerce satır log kopyalanmaz.
- İlk hata bulununca gerekli bağlam alınır; ilgisiz log yüklenmez.
- `--no-restore` ve `--no-build` uygun sıralamada kullanılır.
- Değişmeyen dependency restore tekrar tekrar yapılmaz.
- Hedefli `--filter` testleri geliştirme döngüsünde kullanılır; tam suite faz kapısında zorunludur.
- Test kanıtı tarih, commit, komut, ortam ve sonuçla evidence loga yazılır.
- Token tasarrufu test kapsamını azaltmak için değil, gereksiz çıktıyı azaltmak için kullanılır.

## 9.4 Test katmanları

| Katman | Amaç |
|---|---|
| Unit | Domain kuralları, fiyat/stok, durum geçişi, idempotency anahtarı |
| Application | Use-case, yetki, capability ve orchestration |
| Persistence integration | PostgreSQL constraint, migration, lease ve concurrency |
| API integration | Auth, route, validation, idempotency ve response sözleşmesi |
| Adapter contract | Dış JSON/XML mapping, endpoint sözleşmesi, hata sınıfları |
| Web component | Form, filtre, durum ve güvenli buton görünürlüğü |
| End-to-end | API + Worker + DB + fake/Stage ile uçtan uca süreç |
| Operational | Backup/restore, restart, health, rollback ve immutable image |

# 10. Dokümantasyon dağıtımı ve eşzamanlı güncelleme düzeni

## 10.1 Dosya görevleri

| Dosya | Görevi | Ne zaman güncellenir |
|---|---|---|
| `RAVENCIA-NIHAI-PROJE-BELGESI.md` | Ana plan, hedef sistem ve bağlayıcı süreç | Kapsam, ana mimari veya kullanıcı akışı değişince |
| `PROJECT-STATUS.yaml` | Makinece okunabilir tek durum kaydı | Her durum değişikliğinde |
| `CURRENT-PHASE.md` | Aktif faz, blokaj, sıradaki işler | Her tamamlanan/başlayan işte |
| `capability-matrix.md` | Dış API kanıtı ve güvenli açma durumu | Capability kanıtı değişince |
| `F*-plan.md` | Faz hedefleri ve iş sırası | Faz kapsamı değişince |
| `F*-evidence-log.md` | Komut, test ve dış kanıt | Her doğrulamada |
| `traceability-matrix.md` | Gereksinim-kod-test ilişkisi | Gereksinim veya test eklenince |
| `docs/CHANGELOG.md` | Kronolojik insan özeti | Her mantıksal değişiklik grubunda |
| `README.md` | Kısa giriş ve güncel yönlendirme | Ana durum/kapsam değişince |
| `AGENTS.md` | Codex çalışma kuralları | Süreç, test veya kaynak önceliği değişince |
| ADR | Kalıcı karar ve gerekçe | Yeni bağlayıcı karar veya supersede durumunda |
| Review/manifest | İnceleme ve teslim kaydı | Paket/nihai belge/release oluşturulunca |

## 10.2 Dokümantasyon transaction kuralı

Bir kod değişikliği durum veya capability etkiliyorsa aynı commit içinde en az şu dosyalar güncellenir:

1. `PROJECT-STATUS.yaml`
2. `CURRENT-PHASE.md`
3. İlgili `F*-evidence-log.md`
4. `docs/CHANGELOG.md`
5. Gerekliyse capability matrisi ve traceability matrisi
6. Kullanıcı görünür davranış değiştiyse ana plan ve README

Dokümanlar farklı günlerde rastgele güncellenmez. “Kod tamamlandı, belge sonra yazılır” yaklaşımı kabul edilmez.

## 10.3 Otomatik tutarlılık kontrolü

CI ve yerel kontrol şu durumları hata kabul eder:

- Ürün kodu değişmiş fakat changelog/evidence güncellenmemiş.
- `CURRENT-PHASE.md` ile `PROJECT-STATUS.yaml` ana fazı farklı.
- Capability `SUPPORTED` yapılmış fakat evidence veya doğrulama tarihi yok.
- Ana kapsam değişmiş fakat README ve AGENTS hâlâ eski kapsamı söylüyor.
- Faz “tamamlandı” yazılmış fakat çıkış testi kanıtı yok.

# 11. Git, commit ve paket politikası

## 11.1 Geliştirme repository'sinde Git kaydı

Geliştirme repository'sindeki Git kaydı şu amaçlarla korunur:

- Hangi kararın hangi kod değişikliğiyle uygulandığını gösterir.
- Eski ve yeni davranış arasındaki diff'i sağlar.
- Test eklenme veya silinme geçmişini izler.
- `git blame`, tag ve release commitleriyle hata araştırmasını kolaylaştırır.
- Codex'in mevcut kodun gelişim bağlamını, testlerin kaynağını ve commit sınırlarını anlamasına yardım eder.
- Yanlış değişikliği güvenli biçimde geri alma veya forward-fix hazırlama imkânı verir.

Bu nedenle ana geliştirme repository'sinden `.git` silinmeyecektir. Orijinal commit, branch, tag ve remote geçmişi korunacaktır.

## 11.2 İki ayrı paket türü

### Geliştirme repository paketi

- `.git` geçmişi bulunur.
- Branch, tag, commit ve diff takibi yapılabilir.
- Codex/geliştirici bu paket üzerinde çalışır.
- Secret, `bin/obj`, `node_modules`, runtime DB ve log yine bulunmaz.

### Temiz release/deployment paketi

- `.git` bulunmaz.
- Yalnız derleme için gereken kaynak ve deployment dosyaları bulunur.
- Secret, test çıktısı, cache, runtime veri ve gereksiz doküman çıktıları bulunmaz.
- Production'a kaynak ZIP taşımak yerine tercihen CI tarafından üretilen immutable image digest deploy edilir.

`.git` klasörünün release paketinde olmaması geçmişin silinmesi anlamına gelmez; ana repository ve GitHub üzerinde korunması gerekir.

## 11.3 Commit düzeni

- Her mantıksal iş için ayrı commit.
- Commit mesajı yapılan işi ve fazı açıklar.
- Kod + test + evidence + durum kaydı aynı committe bulunur.
- Faz çıkışında tag oluşturulur.
- Release tag yalnız CI tam doğrulamayı geçince kullanılır.
- Geçmişi yeniden yazan force-push varsayılan olarak yapılmaz.
- Uygulanmış migration ve evidence commitleri değiştirilmez veya silinmez.

---

# KISIM II - GÜNCEL TEKNİK TASARIM, MODÜLLER VE FAZLAR

Aşağıdaki bölümler projenin teknik kapsamını, veri ve güvenlik mimarisini, faz planlarını ve production kabul kriterlerini ayrıntılandırır. Yukarıdaki ana plan ve kullanıcı işleyişi bu teknik bölümlerin iş otoritesidir.


# 12. Ana plan durum panosu

| Alan | Planlanan nihai sonuç | Güncel durum | Sonraki kapı |
|---|---|---|---|
| Mimari temel | Modüler monolit, API/Worker ayrımı | `TAMAMLANDI_F0` | Değişiklik yalnız ADR ile |
| Kimlik ve güvenlik çekirdeği | Güvenli login, parola, MFA, secret | `READY_LOCAL` | Production rol/MFA kabulü |
| Yerel katalog/import | Ürün, varyant, özellik, import | `READY_LOCAL` | Exact runtime regresyonu |
| Trendyol read/reference | Kategori, marka, özellik, ürün, sipariş | `KODLANDI` | Gerçek Stage kapsam testi |
| Trendyol product write | Create/update/archive + batch | `CORE_CODE_COMPLETE_STATIC_VERIFIED` | Exact runtime ve Stage safe-write kabulü |
| Stok-fiyat write | Birleşik batch ve reconciliation | `CORE_CODE_COMPLETE_STATIC_VERIFIED` | Exact runtime, partial-batch ve Stage kabulü |
| Sipariş/paket write | Sipariş okuma, izinli paket aksiyonları ve read-back | `CORE_CODE_COMPLETE_STATIC_VERIFIED_CAPABILITY_GATED` | Stage capability evidence ve güvenli yazma kabulü |
| Etiket | Capability-gated ortak etiket oluşturma ve okuma | `CORE_CODE_COMPLETE_STATIC_VERIFIED_CAPABILITY_GATED` | Stage format ve yazdırma kabulü |
| E-Faturam submit | Doğru belge oluşturma | `KODLANDI_STATIK_DOGRULANDI` | Exact runtime + Stage E2E |
| Mükellef/status/cancel | Uçtan uca fatura yaşam döngüsü | `KODLANDI_STATIK_DOGRULANDI` | E-Fatura status exact endpoint evidence + Stage E2E |
| PDF ve Trendyol teslim | Güvenli PDF + 8 yıllık link | `KODLANDI_KISMEN` | Güvenlik ve reconciliation |
| Production | Kontrollü pilot ve geri dönüş | `BLOKE` | F3/F4, off-host backup, CI |
| Yeni platformlar | Adapter registry ile sırayla ekleme | `SONRAKİ_FAZ_F8` | F7 çıkış kapısı |

# 13. Kesin ürün kapsamı

## 13.1 Aktif kapsam

| Platform kodu | Rol | Aktif durum |
|---|---|---|
| `TRENDYOL` | Türkiye CORE ürün, referans, stok-fiyat, sipariş, paket, iade, webhook ve fatura linki | F3 CORE kod kapsamı tamamlandı; dinamik/Stage kabulü bekliyor |
| `TRENDYOL_EFATURAM` | Mükellef, e-Fatura/e-Arşiv, numeric durum, PDF, E-Arşiv iptal ve belge sağlayıcı işlemleri | F4 kod kapsamı tamamlandı; exact runtime/Stage kabulü bekliyor |

## 13.2 Mevcut fazlarda kapsam dışı

Aşağıdaki platformlar tasarımın gelecekte destekleyebileceği kanallardır fakat şu anda adapter, menü, route, job türü veya capability olarak aktif edilmez:

- Hepsiburada
- N11
- Pazarama
- PTTAVM
- Shopify
- Diğer pazaryerleri veya web mağazaları

Bu platformlara ait adapter, menü, route ve job uygulamaları mevcut kapsamda etkin değildir. Ortak domain, port, mapping, job, audit ve reconciliation altyapısı sonraki platformların standart biçimde eklenebilmesi için kullanılır.

## 13.3 Ürün kapsamı dışında kalan konular

- Genel amaçlı ERP veya muhasebe programı olmak.
- Banka, ödeme kuruluşu veya kargo firmaları için bağımsız tam entegrasyon paketi sunmak.
- Birden fazla bağımsız şirketi aynı kurulumda aktif SaaS müşterisi olarak barındırmak.
- Kanıtı olmayan otomatik iade onayı, paket değişikliği veya mali iptal yapmak.
- Credential, müşteri verisi veya mali belgeyi public storage'da tutmak.
- Kullanıcı onayı ve rollback planı olmadan production dış yazmalarını açmak.

---

# 14. Kullanıcı ve yetki modeli

## 14.1 İşletme modeli

İlk sürüm tek işletme ve tek aktif tenant modeliyle çalışır. Veri modelinde tenant sınırı korunur; ancak aktif çok kiracılı SaaS yüzeyi açılmaz. Bu karar veri izolasyonunu baştan korurken gereksiz tenant yönetim karmaşıklığını engeller.

## 14.2 Roller

Hedef rol seti:

| Rol | Yetki özeti |
|---|---|
| Sistem Sahibi | Tüm ayarlar, kullanıcılar, bağlantılar, dış yazma açma, release ve acil durum işlemleri |
| Yönetici | Ürün, sipariş, fatura, bağlantı ve operasyon yönetimi; kritik güvenlik ayarları sınırlı |
| Operasyon | Ürün, sipariş, paket, iade ve hata kuyruğu işlemleri |
| Finans/Fatura | Fatura doğrulama, gönderme, durum, PDF ve iptal işlemleri |
| Görüntüleyici | Salt-okunur rapor, ürün, sipariş ve operasyon görünümü |

Mevcut kodda temel kullanıcı/tenant/membership altyapısı vardır. İnce taneli rol ve permission matrisi production öncesinde tamamlanmalıdır.

## 14.3 Kimlik doğrulama

- Kullanıcı adı/e-posta ve parola ile giriş.
- Güvenli session cookie.
- CSRF koruması.
- Login ve MFA endpointlerinde rate limiting.
- TOTP tabanlı MFA altyapısı.
- Recovery code üretimi ve hash'li saklama.
- Aktif oturumları listeleme ve iptal etme.
- Parola değişikliğinde diğer oturumları iptal etme seçeneği.
- Başlangıç yöneticisi yalnız bootstrap aşamasında secret dosyasından oluşturulur.
- Kaynak kodda sabit varsayılan parola bulunmaz.

## 14.4 MFA politikası

MFA başlangıçta operasyon kararına göre kapalı olabilir; ancak production sahibi ve kritik yetkili hesaplar için zorunlu hale getirilmesi önerilir. MFA devre dışı bırakma, recovery code yenileme ve break-glass işlemleri audit kaydı üretmelidir.

---

# 15. Fonksiyonel modüller

## 15.1 Yönetim paneli ve ana gösterge ekranı

Panelin amacı kullanıcının sistem durumunu bir bakışta anlamasıdır. Ana ekran aşağıdaki bilgileri göstermelidir:

- Trendyol bağlantı durumu ve son başarılı test.
- E-Faturam bağlantı durumu ve son başarılı test.
- Son referans veri eşitleme zamanı.
- Bekleyen, leased, retry, blocked ve dead job sayıları.
- Son 24 saatte ürün batch sonuçları.
- Sipariş sync gecikmesi ve son cursor.
- Fatura durum dağılımı.
- Teslim edilmemiş fatura linkleri.
- Reconciliation farkları.
- Backup ve son restore doğrulama zamanı.
- Production external-write anahtarının durumu.

Gösterge ekranı güvenilir olmayan “yeşil” durum üretmemelidir. Örneğin yalnız bağlantı testinin geçmiş olması, ürün yazma capability'sinin supported olduğu anlamına gelmez.

## 15.2 Bağlantı yönetimi

Her platform bağlantısı şu bilgileri içerir:

- Platform kodu.
- Ortam: Stage veya Production.
- Görünen ad.
- Dış mağaza/satıcı/firma kimliği.
- Credential referansı.
- API sürümü.
- Bağlantı durumu.
- Son test zamanı ve sonucu.
- Capability kanıtları.
- Connection seviyesinde dış yazma izni.
- Sync politikası ve cursor.

Credential değeri UI'ya geri döndürülmez. Güncelleme işlemi yeni credential yazar; mevcut secret maskeli gösterilir. Bağlantı testi durable job olarak çalışır ve sonucu audit ile kaydedilir.

## 15.3 Capability yönetimi

Her dış işlem capability bazında kapılandırılır. Örnekler:

- `REFERENCE_READ`
- `PRODUCT_READ`
- `PRODUCT_WRITE`
- `INVENTORY_WRITE`
- `PRICE_WRITE`
- `ORDER_READ`
- `SHIPMENT_WRITE`
- `RETURN_READ`
- `RETURN_WRITE`
- `INVOICE_SUBMIT`
- `INVOICE_STATUS_READ`
- `INVOICE_DOCUMENT_READ`
- `INVOICE_CANCEL`
- `INVOICE_DELIVER`

Bir capability `UNKNOWN` ise API ve UI ilgili dış işi oluşturamaz. Capability ancak resmî kaynak, ortam, mağaza/firma scope'u, API sürümü, doğrulama tarihi ve kanıt notuyla `SUPPORTED` yapılır.

## 15.4 Yerel katalog

Yerel katalog platformlardan bağımsız ana veri kaynağıdır. Temel varlıklar:

- Ürün.
- Ürün varyantı.
- Kategori.
- Marka.
- Özellik tanımı ve değerleri.
- Kategori özellik zorunlulukları.
- Ürün özellik atamaları.
- Seçenek ve seçenek değerleri.
- Ürün görselleri ve sıralaması.
- Platform listing profili.

Ürünün yerel kimliği değişmez. Platform dış kimlikleri ayrı link tablolarında tutulur. Barkod ve SKU kuralları validation katmanında kontrol edilir.

## 15.5 Ürün içe aktarma

Dosya içe aktarma akışı aşağıdaki adımlardan oluşur:

1. Import session oluşturma.
2. Kaynak dosyayı private storage'a yükleme.
3. Kolon eşleme profili seçme veya oluşturma.
4. Preview job çalıştırma.
5. Staging kayıtlarını ve eşleşme adaylarını üretme.
6. Kullanıcının create/update/skip kararlarını vermesi.
7. Apply job ile yerel kataloğa uygulama.
8. Hata CSV'si üretme.
9. Her alan için provenance kaydı tutma.

Dosya içeriği doğrudan ana tablolara yazılmaz. Önce staging ve kullanıcı kararı gerekir. Dosya boyutu, MIME, uzantı ve satır sayısı sınırları uygulanır.

## 15.6 Trendyol referans verileri

Sistem Trendyol'dan aşağıdaki referansları alır:

- Kategori ağacı.
- Marka listesi.
- Kategori özellikleri.
- Özellik değerleri.
- Gerekirse kargo sağlayıcı listesi ve diğer sabit tablolar.

Kategori ağacında yalnız `IsLeaf=true` olan son kırılımlar ürün yayınlama için seçilebilir. Ara kategoriler navigasyon için görünür, seçim için kapalıdır.

Referans veri snapshot mantığıyla saklanır. Her snapshot alınma zamanı, scope ve kaynak kimliği ile kayıtlıdır. Mapping kayıtları belirli snapshot'a bağlanır; uzaktaki değişiklikler eski eşlemeyi sessizce değiştirmez.

## 15.7 Kategori, marka ve özellik eşleme

Yerel katalog ile Trendyol arasında şu eşlemeler vardır:

- Yerel kategori -> Trendyol kategori.
- Yerel marka -> Trendyol marka.
- Yerel özellik -> Trendyol kategori özelliği.
- Yerel özellik değeri -> Trendyol özellik değeri veya izin varsa custom value.

Yayınlama öncesi validation şu kontrolleri yapar:

- Kategori leaf mi?
- Marka geçerli ve aktif mi?
- Zorunlu özelliklerin tamamı var mı?
- Çoklu değer izni doğru mu?
- Custom value kullanılabilir mi?
- Barkod, SKU ve ürün kodu kurallara uygun mu?
- Görsel sayısı ve URL/asset koşulları sağlanıyor mu?
- Liste fiyatı satış fiyatından düşük mü?
- Varyant gruplaması tutarlı mı?

Eşleme çalışma alanında seçim bağımlılıkları aşağıdaki sırayla uygulanır:

1. ACTIVE Trendyol bağlantısı seçilir.
2. Doğrulanmış yerel kategori eşlemesi üzerinden uzak kategori kapsamı belirlenir.
3. Güncel kategori özelliği snapshot'ından yerel özellik eşlenir.
4. Özellik seçim listesi taşıyorsa güncel özellik değeri snapshot'ından yerel değer eşlenir.
5. Her kayıt belirli snapshot ve scope ile `VERIFIED` saklanır; eski veya farklı scope'taki kayıt yayınlama kapısında kabul edilmez.

**Kod durumu:** Bu zincir web panelinde tek kategori-kapsamlı özellik/değer çalışma alanı olarak uygulanmıştır; marka eşlemesi ayrı görünümde devam eder. Frontend regression testleri güncel bileşen, erişilebilir alan adı, buton metni, PUT payload'ı ve değer endpoint'iyle hizalanmıştır. Dinamik Vitest/Playwright sonucu henüz üretilmemiştir.

## 15.8 Ürün okuma ve yerel eşleştirme

Trendyol'daki onaylı ürünler sayfalı biçimde çekilir. Her uzak ürün için:

- Uzak ürün ve varyant kimliği.
- Barkod.
- SKU/merchant SKU.
- Değişiklik zamanı.
- Ham cevap hash'i.
- Yerel ürün/varyant linki.

saklanır.

Eşleştirme önceliği açık kuralla yapılır. Önerilen sıra:

1. Önceden kayıtlı uzak kimlik linki.
2. Barkod eşleşmesi.
3. SKU eşleşmesi.
4. Kullanıcı onaylı alias.
5. Manuel eşleştirme kuyruğu.

Belirsiz eşleşme otomatik uygulanmaz.

## 15.9 Ürün oluşturma ve güncelleme

Ürün yazma akışı create, update ve archive olarak ayrılır. Tek bir belirsiz `Upsert` operasyonu kalıcı hedef değildir.

### Create akışı

1. Ürün ve listing profile doğrulanır.
2. Canonical payload üretilir.
3. Payload hash ve idempotency key hesaplanır.
4. Publication job oluşturulur.
5. Trendyol Product V2 create endpointine gönderilir.
6. Dönen `batchRequestId` kaydedilir.
7. Batch result belirli aralıklarla sorgulanır.
8. Her satırın success/failure sonucu saklanır.
9. Başarılı satırlar marketplace link tablolarına bağlanır.
10. Validation hataları kullanıcıya alan/satır bazında gösterilir.

### Update akışı

Onaylı ürün içeriği, varyantı ve teslimat bilgisi için resmî V2 sözleşmelerine göre ayrı komutlar kullanılmalıdır. Barkodun değiştirilemeyen kimlik olduğu senaryolar korunur. Partial update yalnız gönderilen alanları değiştirmelidir.

### Archive/delete akışı

Silme veya arşivleme capability'si resmî endpoint ve Stage kanıtı olmadan açılmaz. Yerel ürün arşivlendiğinde uzak ürüne otomatik destructive işlem uygulanmaz; ayrı kullanıcı onayı ve job gerekir.

## 15.10 Stok ve fiyat yönetimi

Yerel stok ledger tabanlıdır. Temel kavramlar:

- On hand.
- Reserved.
- Available.
- Safety stock.
- Projection version.
- Stok hareket kaynağı.
- Idempotency source event.

Fiyat modeli:

- Liste fiyatı.
- Satış fiyatı.
- Para birimi.
- KDV oranı ve dahil/hariç bilgisi.
- Yuvarlama modu.
- Price version.

Trendyol uzak sözleşmesi stok ve fiyatı birleşik `price-and-inventory` batch isteği olarak işler. Bu nedenle mevcut ayrı stock/price portları hedef mimaride tek `PriceInventoryBatch` komutuna dönüştürülmelidir.

Gönderim kuralları:

- Aynı barkod için son proje versiyonu gönderilir.
- Batch maksimum satır sınırına göre bölünür.
- Her batch için uzak operation id saklanır.
- Timeout sonrası aynı etkiyi tekrar üretmeden önce batch sonucu sorgulanır.
- Partial failure satır bazında retry veya kalıcı hata olarak ayrılır.
- Uzak sonuç tamamlanmadan yerel listing state “başarılı” olmaz.

## 15.11 Sipariş yönetimi

Siparişler polling ve uygun olduğunda webhook ile alınır. Polling her zaman reconciliation fallback'i olarak korunur.

Sipariş modelinde:

- Uzak sipariş kimliği ve sipariş numarası.
- Sipariş zamanı ve son değişiklik zamanı.
- Para birimi ve finansal toplamlar.
- Müşteri snapshot'ı.
- Teslimat ve fatura adres snapshot'ı.
- Sipariş satırları.
- Paketler ve paket-satır tahsisleri.
- Durum geçmişi.

bulunur.

Gizlilik gereği müşteri ve adres verileri yalnız gerekli operasyon süresi ve yasal saklama politikası kapsamında tutulur. Log ve metric etiketlerine yazılmaz.

Polling penceresi overlap içerir. Aynı sipariş tekrar geldiğinde unique dış kimlik ve upsert/idempotency kuralları duplicate oluşmasını önler. Out-of-order olaylarda olay zamanı ve bilinen durum geçiş kuralları kullanılır.

## 15.12 Paket ve kargo işlemleri

Paketler siparişten ayrı durum yaşam döngüsüne sahiptir. Hedef işlemler:

- Paketleri görüntüleme.
- Satır tahsislerini görüntüleme.
- Kargo sağlayıcı ve takip numarası saklama.
- İzin verilen paket aksiyonlarını capability'ye göre sunma.
- Etiket veya shipment document alma.
- Her yazma aksiyonunu idempotent job olarak işletme.

Exact endpoint, yetki ve Stage kanıtı bulunmayan paket aksiyonları UI'da gösterilmez veya disabled açıklamasıyla görünür.

## 15.13 İade yönetimi

İade talepleri ayrı claim ve line modeliyle tutulur. Sistem:

- İade taleplerini sayfalı ve tarih pencereli çeker.
- Claim durumunu ve son değişiklik zamanını saklar.
- Sipariş satırıyla eşleştirir.
- Aksiyon son tarihini gösterir.
- Kanıt dosyalarını private storage'da tutar.
- Onay, red veya dispute işlemlerini ayrı capability olarak ele alır.
- İade sonrası stok disposition kararını ayrı kaydeder.

Otomatik iade onayı varsayılan kapalıdır.

## 15.14 E-Faturam otomatik belge türü kararı

Panel ayrı bir mükellef sorgusu çalıştırmaz. Fatura türü, Trendyol sipariş snapshotındaki iki alanla deterministik seçilir:

- `commercial=true` ve `invoiceAddress.eInvoiceAvailable=true`: `TEMELFATURA`.
- Diğer tüm durumlar: `EARSIVFATURA`.

Müşteri VKN/TCKN, ad/unvan ve adres bilgileri fatura adresi snapshotından alınır. Eksik veya geçersiz zorunlu müşteri bilgisi submit öncesi validation hatasıdır. Kullanıcı belge türünü elle değiştiremez.

## 15.15 Fatura türü ve mali politika

Fatura üretimi kod içine dağılmış if bloklarıyla değil, versiyonlanmış `InvoicePolicy` üzerinden yönetilir.

Politika en az şunları belirler:

- Faturanın hangi sipariş/paket durumunda hazırlanacağı.
- Sipariş bazlı mı paket bazlı mı kesileceği.
- e-Fatura/e-Arşiv seçimi.
- Fatura tarihi ve son tarih kuralı.
- Yuvarlama ve kuruş farkı kuralı.
- İskonto, kargo ve diğer adjustment dağıtımı.
- İptal ve yeniden düzenleme politikası.
- Otomatik submit izni.

E-Faturam API tutarları kuruş biriminde beklediği için decimal TL tutarları merkezi ve test edilmiş dönüşüm fonksiyonuyla long kuruş değerine çevrilmelidir. Yuvarlama farkı fatura toplamını bozmayacak biçimde satırlara deterministik dağıtılmalıdır.

## 15.16 Fatura oluşturma

Fatura yaşam döngüsü hedef olarak:

`Draft -> Validated -> Ready -> Submitting -> Submitted -> Processing -> Accepted/Completed`  
veya  
`Rejected/Failed/CancellationPending/Cancelled`

şeklinde açık state machine ile yönetilir.

Akış:

1. Sipariş/paket finansal snapshot'ından draft oluşturulur.
2. Satır, vergi, indirim ve toplamlar hesaplanır.
3. Alıcı ve satıcı party snapshot'ları kaydedilir.
4. Validation çalışır.
5. Idempotency key ile submit job oluşturulur.
6. E-Faturam canonical payload'a dönüştürülür.
7. Uzak external reference, invoice number ve ETTN saklanır.
8. Uzak status terminal duruma kadar poll edilir.
9. Hata durumunda retry sınıfı belirlenir.
10. Duplicate local reference veya timeout-after-success reconciliation ile çözülür.

POST isteğinin 2xx dönmesi tek başına faturanın nihai kabul edildiği anlamına gelmez.

## 15.17 Fatura PDF ve belge saklama

E-Faturam'dan geçici veya kalıcı doküman URL'si alınabilir. Güvenli indirme şu kapılardan geçer:

- HTTPS zorunluluğu.
- Exact host allow-list.
- User-info ve beklenmeyen port reddi.
- DNS/IP çözümünde private, loopback, link-local ve metadata adreslerinin reddi.
- Redirect kapalı veya her redirect hedefinin yeniden doğrulanması.
- Maksimum dosya boyutu.
- `Content-Type` kontrolü.
- `%PDF-` magic byte kontrolü.
- SHA-256 checksum.
- Private immutable storage.
- İndirme ve erişim audit kaydı.

PDF public web klasörüne veya doğrudan source repository'ye yazılmaz. Trendyol'a verilen kalıcı linkin yasal erişim süresini karşılaması ayrıca izlenir.

**Kod durumu:** Exact HTTPS host allow-list, public DNS/IP doğrulaması, sınırlı redirect, streaming boyut sınırı, MIME ve `%PDF-` kontrolü uygulanmıştır. Gerçek E-Faturam Stage hostu ve örnek PDF ile dinamik doğrulama yapılmadığı için capability production'a açılmaz.

## 15.18 Fatura iptali

İptal işlemi:

- Kullanıcı yetkisi.
- Belge türü.
- Uzak statü.
- İptal süresi.
- Sebep.
- İlgili sipariş/paket.
- Yeni belge gerekip gerekmediği.

kontrollerinden geçer.

İptal job'ı uzak terminal sonucu görülmeden `Cancelled` yazmaz. E-Arşiv ve e-Fatura iptal/ret süreçleri aynı işlem gibi ele alınmaz.

## 15.19 Trendyol'a fatura teslimi

Fatura link teslimi doğru `shipmentPackageId` ile yapılır. Akış:

1. Fatura terminal kabul durumuna gelir.
2. PDF veya kalıcı URL erişilebilirlik kontrolünden geçer.
3. Invoice number ve tarih formatı doğrulanır.
4. Marketplace delivery kaydı `Pending` oluşturulur.
5. Job Trendyol'a link gönderir.
6. 2xx sonrası durum `Submitted` olur.
7. Duplicate/409 ve timeout senaryoları reconciliation ile çözülür.
8. Gerçek teslim kanıtı oluşunca `Confirmed` yapılır.

Fatura URL'si periyodik probe ile izlenmeli; erişilemezlik operasyon alarmı oluşturmalıdır.

**Kod durumu:** Link gönderiminde HTTP 2xx yalnız `Submitted` kabul edilir. Resmî read-back/confirmation endpoint'i doğrulanmadığı için otomatik `Confirmed` üretilmez; belirsiz sonuç yeniden dış etki oluşturmadan `MANUAL_REVIEW` akışına gider.

## 15.20 Operasyon ve hata yönetimi

Operasyon ekranı aşağıdaki kayıtları yönetir:

- Bekleyen ve çalışan joblar.
- Retry planlanan işler.
- Blocked işler.
- Dead-letter işler.
- Son hata kodu ve güvenli hata mesajı.
- Uzak request id.
- Correlation id.
- Reconciliation farkları.
- Capability blokajları.
- Manuel retry/cancel işlemleri.

Kullanıcıya ham credential, token, müşteri adresi veya tam mali payload gösterilmez.

---

# 16. Trendyol entegrasyon kuralları

## 16.1 API sürümü

Ürün entegrasyonunda Product V2 esas alınır. Product V1 servislerinin 10 Ağustos 2026 itibarıyla geçersiz olacağı resmî dokümantasyonda belirtilmiştir. Bu nedenle yeni kod V1 endpointine bağlanmamalıdır.

## 16.2 Asenkron batch modeli

Ürün oluşturma, ürün güncelleme ve stok-fiyat işlemleri asenkron batch mantığıyla çalışır. Başarılı ilk istekten alınan `batchRequestId`, işlemin tamamlandığı anlamına gelmez. Batch sonucu sorgulanmalı, status ve satır failure reason'ları kaydedilmelidir.

Ürün oluşturma V2 için tek istekte en fazla 1.000 item gönderilir. Sistem daha büyük işleri deterministik batchlere böler.

## 16.3 Rate limit

Rate limit servis grubu bazında ele alınır. Worker concurrency yalnız sunucu kapasitesine göre değil, platform grubunun limitine göre ayarlanır. HTTP 429 ve Retry-After bilgisi transient olarak sınıflandırılır; jitter'lı backoff uygulanır.

## 16.4 Hata sınıflandırması

| Sınıf | Örnek | İşlem |
|---|---|---|
| Transient network | DNS, timeout, bağlantı reset | Retry + reconciliation |
| Rate limit | HTTP 429 | Retry-After'a uy |
| Remote 5xx | Platform geçici hata | Sınırlı retry |
| Authentication | 401/403 | Bağlantıyı bloke et, kullanıcı aksiyonu |
| Validation | Hatalı kategori/özellik/fiyat | Kalıcı satır hatası |
| Business conflict | Duplicate link, durum çakışması | Uzak durumu sorgula |
| Not found | Silinmiş paket/claim | Scope'a göre kalıcı veya reconciliation |
| Contract violation | Beklenmeyen JSON/alan | Capability'yi degrade et, alarm |
| Internal bug | Mapper/state hatası | Dead-letter + geliştirici müdahalesi |

## 16.5 Fatura linki saklama süresi

Trendyol fatura linkinin en az 8 yıl erişilebilir olmasını ister. Bu nedenle yalnız provider'ın “permanent URL” adını kullanması yeterli kabul edilmez. Link sahipliği, retention, probe ve gerekirse kontrollü belge gateway'i ile garanti edilmelidir.

---

# 17. Trendyol E-Faturam entegrasyon kuralları

## 17.1 Entegrasyon modeli

Aktif ürün kapsamında Ravencia kendi E-Faturam hesabını doğrudan `API_USER` modeliyle kullanır. Panel yalnız E-Faturam e-posta/parolasını şifreli saklar. `companyId` ve `userId` başarılı sign-in tokenından okunur; panelde girilmez, API ile geri gösterilmez ve connection settings içinde saklanmaz. Prefix/seri gönderilmez; E-Faturam hesabındaki varsayılan seri kullanılır. Partner `customerSignIn` ve çoklu müşteri mali hesap modeli aktif kapsam dışıdır.

## 17.2 Ortamlar

- Stage gateway: resmî test ortamı.
- Production gateway: canlı ortam.

Stage ve production credential'ları kesin olarak ayrılır. Production credential ile otomatik test yapılmaz.

## 17.3 Yetkilendirme

Sign-in ile alınan token güvenli memory/cache süresi içinde kullanılır. Token loglanmaz, kalıcı düz metin saklanmaz ve süresi dolduğunda yenilenir. 401/403 durumunda sonsuz retry yapılmaz.

## 17.4 Tutarlar

API payload tutarları kuruş cinsinden integer değerler olarak gönderilir. Örneğin 114,55 TL, 11455 olarak taşınır. Dönüşüm merkezi mali hesaplama bileşeninde yapılır ve property bazlı testlerle doğrulanır.

## 17.5 Belge türü ve internet satış e-Arşiv alanları

Belge türü kullanıcı ayarı değildir. Trendyol siparişinde `commercial=true` ve fatura adresinde `eInvoiceAvailable=true` birlikteyse `TEMELFATURA`; diğer siparişlerde `EARSIVFATURA` seçilir. Ayrı mükellef sorgusu veya Temel/Ticari senaryo seçimi yapılmaz.

İnternet satışına ait E-Arşiv belgelerinde provider sözleşmesinin istediği `paymentInfo` ve `deliveryInfo` alanları panelden ayarlanmaz. Ödeme bağlamı Trendyol siparişinden, taşıyıcı VKN/unvanı resmî Trendyol kargo sağlayıcı kataloğundan deterministik üretilir. Bilinmeyen taşıyıcıda tahmin yapılmaz ve submit bloklanır.

## 17.6 Statü takibi

E-Faturam statü kodları yerel canonical statülere eşlenir. Örnek uzak statüler:

- İşleniyor.
- Doküman hazırlanıyor.
- Oluşturuldu.
- GİB'e gönderildi.
- Yanıt bekleniyor.
- Onaylandı.
- İptal edildi.
- Hatalı.

Her kodun terminal olup olmadığı belge türüne ve senaryoya göre belirlenir. Bilinmeyen yeni statü başarıya çevrilmez; `UnknownRemoteStatus` operasyon kaydı oluşturur.

---

# 18. Sistem mimarisi

## 18.1 Mimari yaklaşım

Sistem modüler monolittir. Modüler monolit şu avantajları sağlar:

- Tek deploy birimi ve daha basit operasyon.
- Domain transactionlarının PostgreSQL içinde güvenli yürütülmesi.
- Mikroservisler arası ağ ve dağıtık transaction karmaşıklığının olmaması.
- Platform adapterlarının portlar üzerinden ayrılması.
- İleride yük gerektirirse Worker veya belirli modüllerin ayrıştırılabilmesi.

## 18.2 Çalışma bileşenleri

| Bileşen | Sorumluluk |
|---|---|
| React Web | Kullanıcı paneli, form, tablo, hata ve operasyon görünümü |
| ASP.NET Core API | Kimlik, yetkilendirme, use-case endpointleri, validation ve query/command kabulü |
| Worker | Job lease, heartbeat, adapter çağrısı, retry ve reconciliation işleri |
| Application | Portlar, command/view sözleşmeleri ve use-case sınırları |
| Domain | Ürün, sipariş, stok, fatura, job ve iş kuralları |
| Infrastructure | EF Core, PostgreSQL, file storage, güvenlik, adapterlar ve dış HTTP istemcileri |
| PostgreSQL | Ana veri, job, inbox, idempotency, audit ve reconciliation kayıtları |
| Private file storage | Ürün görselleri, import dosyaları, kanıt ve fatura PDF'leri |
| Caddy | TLS, reverse proxy ve dışa açılan tek giriş noktası |

## 18.3 Mantıksal veri akışı

```text
Kullanıcı Tarayıcısı
        |
      HTTPS
        v
      Caddy
        |
        v
 ASP.NET Core API ----> PostgreSQL
        |                    ^
        | job oluşturur      | lease/result/audit
        v                    |
      Worker ----------------+
        |
        +----> Trendyol API
        |
        +----> Trendyol E-Faturam API
        |
        +----> Private File Storage
```

API uzun dış işlemi request içinde tamamlamaya çalışmaz. Kullanıcı isteği doğrulandıktan sonra job oluşturur. Worker lease alır, dış etkiyi yürütür ve sonucu kaydeder. UI job ve entity durumunu sorgular.

## 18.4 Katman bağımlılıkları

- Domain dış katmanlara bağımlı değildir.
- Application, Domain'i kullanır ve dış sistemler için interface/port tanımlar.
- Infrastructure, Application portlarını uygular.
- API ve Worker, Dependency Injection üzerinden Application/Infrastructure hizmetlerini kullanır.
- Web yalnız HTTP API sözleşmesine bağımlıdır.

Platforma özel DTO ve JSON modelleri Domain'e sızmamalıdır.

## 18.5 Port ve adapter tasarımı

Mevcut ortak portlar:

- `IConnectionPort`
- `IReferenceDataPort`
- `IProductPort`
- `IInventoryPricePort`
- `IOrderPort`
- `IReturnPort`
- `IWebhookVerifier`
- `IInvoiceProviderPort`
- `IInvoiceMarketplacePort`

F3/F4 tamamlandıktan sonra birden fazla pazaryeri için hedef seçim modeli:

```text
IPlatformAdapterResolver
  ├─ TRENDYOL -> TrendyolAdapterSet
  ├─ HEPSIBURADA -> HepsiburadaAdapterSet
  ├─ N11 -> N11AdapterSet
  └─ PAZARAMA -> PazaramaAdapterSet
```

Resolver bağlantının `PlatformCode` değerine göre doğru adapter setini seçer. Ana servislerde `if platform == ...` zinciri büyütülmez.

## 18.6 Network sınırları

- Caddy yalnız edge ve backend ağındadır.
- API ve Worker backend + egress ağını kullanır.
- PostgreSQL yalnız internal backend ağındadır.
- PostgreSQL host portu açılmaz.
- Dış dünyaya yalnız Caddy 80/443 açılır.
- Secretlar Docker secret dosyaları üzerinden mount edilir.

---

# 19. Veri mimarisi

## 19.1 Veri grupları

Mevcut veritabanı modeli aşağıdaki grupları içerir:

| Grup | Ana tablolar/varlıklar |
|---|---|
| Kimlik | Tenant, membership, user security, recovery code, session, bootstrap state |
| Operasyon | Integration job, job attempt, inbox, external effect, audit, issue, feature flag, idempotency |
| Katalog | Product, variant, category, brand, attribute, option, media |
| Platform mapping | Connection, credential, capability, reference snapshot/item, category/brand/attribute mapping |
| Listing | Marketplace product/variant link, listing state, listing profile, channel media order |
| Import | Import session, column profile/mapping, staging record, candidate, decision, provenance |
| Stok/fiyat | Inventory location/item, ledger, reservation, offer, price history, connection policy |
| Sync | Webhook subscription, sync cursor, sync policy, reconciliation run/difference |
| Sipariş | Order, line, financial allocation, package, package allocation, status history |
| Kargo/iade | Shipment document/attempt, cargo mapping, return claim/line/decision/evidence/disposition |
| Fatura | Legal entity, policy, invoice, line, party snapshot, document, attempt, marketplace delivery |

## 19.2 Kimlik stratejisi

- Yerel entity kimlikleri GUID v7 ile oluşturulur.
- Dış sistem kimlikleri ayrı alan ve link tablolarında tutulur.
- Dış kimlik üzerinde unique scope uygulanır: tenant + connection + entity type + external id.
- Dış kimlik değişikliği veya alias ihtiyacı `ExternalIdentifierAlias` üzerinden yönetilir.

## 19.3 Optimistic concurrency

Kritik varlıklarda `Version` alanı kullanılır. Update komutları beklenen versiyonu gönderir. Başka kullanıcı veya Worker kaydı değiştirmişse işlem conflict döndürür; sessiz overwrite yapılmaz.

## 19.4 Idempotency

Idempotency üç düzeyde uygulanır:

1. API idempotency: aynı kullanıcı komutunun tekrarında aynı sonuç/job.
2. Job deduplication: tenant + job type + dedup key unique.
3. External effect: aynı uzak etkinin ikinci kez üretilmesini engelleyen effect idempotency key.

Payload hash, correlation id ve uzak request id kanıt olarak saklanır.

## 19.5 Migration politikası

- Uygulanmış EF Core migration silinmez veya yeniden adlandırılmaz.
- Migration dosya adı migration zincirinin sabit kimliğidir; uygulanmış migration yeniden adlandırılmaz.
- Model snapshot zinciri korunur.
- Production migration önce backup ve staging restore üzerinde denenir.
- Destructive migration ayrı ADR, veri sayımı, rollback ve bakım penceresi gerektirir.
- Kapsam dışı platform kayıtları otomatik hard-delete edilmez.

---

# 20. Job, Worker ve güvenilir dış işlem modeli

## 20.1 Job durumları

`Pending`, `Leased`, `RetryScheduled`, `Blocked`, `ManualReview`, `Succeeded`, `Dead`, `Cancelled`.

## 20.2 Lease ve fencing

Worker bir işi lease token ile alır. Heartbeat lease süresini uzatır. Lease kaybedilirse yerel execution iptal edilir. Completion yalnız aynı lease token hâlâ geçerliyse kabul edilir. Bu fencing yaklaşımı iki Worker'ın aynı işi başarılı işaretlemesini önler.

## 20.3 Retry politikası

- Validation, authentication ve not-supported hataları otomatik sonsuz retry almaz.
- Network, 429 ve uygun 5xx hataları limitli exponential backoff + jitter alır.
- Timeout-after-submit durumunda yeniden yazmadan önce reconciliation yapılır.
- Maksimum deneme sonrası iş `Dead` olur ve operasyon müdahalesi gerekir.
- Retry zamanı platform `Retry-After` bilgisini dikkate alır.

## 20.4 Worker grupları

Hedef mantıksal gruplar:

| Grup | İşler | Öncelik |
|---|---|---|
| Security/maintenance | lease reap, due scan, cleanup | Yüksek |
| Reference sync | kategori, marka, özellik | Orta/düşük |
| Product publication | create/update/batch poll | Orta |
| Stock/price | birleşik fiyat-stok batch | Yüksek |
| Order/package | polling, webhook ingest, package action | Yüksek |
| Return | claim sync ve kontrollü action | Orta |
| Invoice | otomatik belge türü, submit, status, PDF, cancel | Yüksek |
| Invoice delivery | Trendyol link submit ve reconcile | Yüksek |
| Reconciliation | uzak/yerel karşılaştırma | Orta/düşük |

İlk sürümde tek Worker prosesi kullanılabilir. Hız ihtiyacı doğduğunda aynı job tablosu üzerinde queue/partition veya job type bazlı Worker deployment ayrımı yapılabilir.

## 20.5 Scheduler

Tekrarlayan işler doğrudan sonsuz job üretmemelidir. Scheduler aktif job var mı kontrol eder ve dedup key ile yeni işi oluşturur. Önerilen başlangıç aralıkları:

- Sipariş sync: 2-5 dakika.
- İade sync: 5-15 dakika.
- Fatura status: yeni belgelerde kısa, eski processing belgelerde artan aralık.
- Referans sync: günlük veya kullanıcı tetiklemeli.
- Reconciliation: günlük ve hata sonrası.
- Fatura link probe: günlük/haftalık risk seviyesine göre.
- Backup: günlük, retention politikasına göre.

Kesin aralıklar rate limit ve gerçek operasyon hacmiyle ayarlanır.

---

# 21. API tasarım ilkeleri

- Sürüm öneki: `/api/v1`.
- Kimlik endpointleri `/api/v1/auth` altında.
- Query endpointleri cursor pagination kullanır.
- Write endpointleri CSRF ve authorization kontrolünden geçer.
- Kritik komutlarda `Idempotency-Key` zorunludur.
- Update işlemlerinde expected version zorunludur.
- Uzun işlemler `202 Accepted` + job id döndürür.
- Hatalar güvenli problem detail formatında döndürülür.
- Correlation id response ve loglarda yer alır.
- Credential veya PII response'a eklenmez.
- Capability kapalıysa 409/422 benzeri açık iş kuralı hatası döner; sessiz no-op yapılmaz.

Mevcut API ürün, kategori, marka, import, stok, offer, referans, mapping, bağlantı, sipariş, paket, etiket, iade, fatura ve kimlik endpointlerini içerir. Trendyol Türkiye CORE dış yazma yüzeyleri capability evidence, global/connection write switch, idempotency, ETag, external-effect fence ve uzak read-back kapılarıyla fail-closed çalışır; dinamik ve Stage kabulü olmadan production etkinleştirilmez.

---

# 22. Web paneli bilgi mimarisi

Önerilen ana menü:

1. Dashboard
2. Ürünler
3. Katalog ve Eşlemeler
4. Stok ve Fiyat
5. Siparişler
6. Paket ve Kargo
7. İadeler
8. Faturalar
9. Entegrasyonlar
10. İşler ve Hatalar
11. Reconciliation
12. Dosya İçe Aktarma
13. Kullanıcılar ve Güvenlik
14. Sistem ve Yedekleme

## 22.1 Tasarım ilkeleri

- Kullanıcıya teknik JSON yerine iş anlamı gösterilir.
- Status badge'leri tek bir ortak sözlükten gelir.
- “Gönder” ve “tamamlandı” farklı durumlar olarak görünür.
- Tehlikeli aksiyonlar confirmation + neden + yetki gerektirir.
- Dış yazma kapalıysa buton gizlenmek yerine neden kapalı olduğunu açıklayabilir.
- Büyük tablolar server-side pagination ve filtre kullanır.
- Form validation hem istemci hem sunucu tarafında çalışır.
- Mobil kullanım temel izleme için desteklenir; yoğun ürün yönetimi masaüstü odaklıdır.

## 22.2 Ürün ekranı

Ürün detayında sekmeler:

- Genel bilgiler.
- Varyantlar.
- Özellikler.
- Görseller.
- Stok.
- Fiyat.
- Trendyol listing profili.
- Yayın geçmişi ve batch sonuçları.
- Audit.

## 22.3 Sipariş ve fatura ekranı

Sipariş detayında paketler, satır tahsisleri, finansal toplamlar, müşteri/adres snapshot'ı, durum geçmişi ve ilgili faturalar birlikte görünür. Fatura ekranı uzak statü, ETTN, invoice number, PDF, gönderim denemeleri ve Trendyol teslim durumunu gösterir.

---

# 23. Güvenlik mimarisi

## 23.1 Secret yönetimi

- Credentiallar şifreli saklanır.
- Ana şifreleme anahtarı source code veya appsettings içinde tutulmaz.
- Docker secret/file convention kullanılır.
- Production secretlar repository'ye girmez.
- Loglarda header/body redaction uygulanır.
- Credential görüntüleme endpointi yoktur.
- Secret rotasyonu bağlantı testinden sonra devreye alınır.

## 23.2 Dış yazma çift anahtarı

Dış yazma için iki kapı birlikte açık olmalıdır:

1. Global `FeatureFlags__ExternalWrites=true`.
2. İlgili connection/capability write izni.

Ek olarak production operasyon onayı ve evidence kaydı gerekir. Read capability, write yetkisi sağlamaz.

## 23.3 Web güvenliği

- HTTPS zorunlu.
- Secure, HttpOnly ve uygun SameSite cookie.
- CSRF token.
- AllowedHosts production alan adıyla sınırlandırılır.
- Güvenlik headerları Caddy/API üzerinden eklenir.
- Login/MFA rate limit.
- Session fixation ve stale session kontrolleri.
- Dosya upload tip/boyut doğrulaması.

## 23.4 PII ve mali veri

- Müşteri adresi ve iletişim bilgisi loglanmaz.
- Audit kaydı aksiyonu ve entity kimliğini tutar; tam hassas payload tutmaz.
- Fatura PDF private storage'dadır.
- Dosya indirme yetki ve tenant kontrolünden geçer.
- Veri retention ve silme politikası hukuki gereksinimlerle yazılı hale getirilmelidir.

## 23.5 SSRF ve dosya güvenliği

Dış sağlayıcının verdiği URL'ler güvenilmez input olarak değerlendirilir. Host allow-list, IP kontrolü, redirect kontrolü, boyut ve dosya imzası uygulanmadan indirme yapılmaz.

## 23.6 Audit

Audit kaydı en az:

- Tenant.
- Kullanıcı.
- Aksiyon.
- Entity türü ve id.
- Önce/sonra güvenli hash veya özet.
- Correlation id.
- Zaman.
- Kaynak IP/session.

bilgilerini içerir.

---

# 24. Deployment ve altyapı

## 24.1 Hedef sunucu

Hedef çalışma ortamı Ubuntu Server üzerinde Docker Engine ve Docker Compose'tur. Windows VPS ana hedef değildir; gerektiğinde yalnız değerlendirilmiş alternatif olarak tutulur.

## 24.2 Containerlar

- `postgres`
- `migrate`
- `api`
- `worker`
- `caddy`
- operasyon profili altında `backup`

API ve Worker aynı immutable application image'ını farklı command ile çalıştırabilir.

## 24.3 Production release

- Build ve test GitHub Actions üzerinde pinlenmiş sürümlerle çalışır.
- Application ve edge image digestleri alınır.
- Production Compose tag yerine digest kullanır.
- Migration ayrı `migrate` containerıyla API başlamadan yürür.
- API `/health/ready` PostgreSQL erişimini kontrol eder.
- Caddy yalnız API healthy olduktan sonra servis verir.
- Release notu migration, feature flag, backup ve rollback adımlarını içerir.

## 24.4 TLS ve domain

Caddy production alan adında otomatik TLS kullanır. `MARKETPLACEHUB_SITE_ADDRESS` ve `MARKETPLACEHUB_ALLOWED_HOSTS` zorunludur. Local internal CA ayarı production yapılandırmasına taşınmaz.

## 24.5 Kaynak limitleri

Mevcut Compose başlangıç limitleri küçük/orta pilot kurulum içindir. Gerçek yük testinden sonra API, Worker ve PostgreSQL CPU/RAM limitleri ayarlanır. OOM, database connection pool ve disk büyümesi alarm üretmelidir.

---

# 25. Gözlemlenebilirlik

## 25.1 Loglar

Structured JSON log kullanılır. Ortak alanlar:

- Timestamp.
- Level.
- Service.
- Environment.
- Correlation id.
- Tenant id.
- Connection id.
- Job id.
- Operation.
- Safe error code.
- Remote request id.

Credential, token, tam request body, müşteri adresi ve PDF içeriği loglanmaz.

## 25.2 Metrikler

Minimum metrik seti:

- HTTP request count/duration/error.
- Remote API request count/duration/status.
- Rate-limit ve retry sayısı.
- Queue depth.
- Oldest pending job age.
- Leased job ve lease-loss sayısı.
- Dead-letter sayısı.
- Sync cursor lag.
- Batch satır başarı/hata oranı.
- Reconciliation mismatch sayısı.
- Terminal olmayan fatura yaşı.
- Fatura link erişilebilirlik sonucu.
- Backup yaşı ve restore doğrulama sonucu.
- Disk, CPU, RAM ve PostgreSQL bağlantı/disk metrikleri.

## 25.3 Alarmlar

- Readiness fail.
- Queue yaşı eşik üstü.
- Dead job oluşması.
- Authentication capability kaybı.
- Rate-limit sürekli artışı.
- Sipariş sync gecikmesi.
- Fatura processing süresi eşik üstü.
- Fatura linki erişilemez.
- Backup başarısız veya restore kanıtı eski.
- Disk doluluk kritik.

---

# 26. Backup, restore ve felaket kurtarma

## 26.1 Backup kapsamı

- PostgreSQL logical backup.
- Private file storage.
- Data Protection keyleri.
- Gerekli deployment metadata ve image digest listesi.
- Şifreli secret envanteri; secret değeri ayrı güvenli kasada.

## 26.2 Off-host zorunluluğu

Aynı VPS diski üzerindeki backup, host kaybına karşı yeterli değildir. Backup şifreli olarak ikinci sunucu veya object storage'a aktarılmalıdır.

## 26.3 Retention önerisi

- Günlük: 14-30 kopya.
- Haftalık: 8-12 kopya.
- Aylık: hukuki ve operasyonel ihtiyaca göre.

Kesin süre mali/yasal onayla belirlenir.

## 26.4 Restore testi

Backup “başarılı” yalnız dosya oluştuğunda değil, temiz hedefte restore edildiğinde kabul edilir:

1. Yeni boş PostgreSQL volume.
2. DB restore.
3. File storage restore.
4. Data Protection key restore.
5. Uygulama migration kontrolü.
6. Login smoke.
7. Ürün/sipariş/fatura örnek kayıt okuma.
8. Checksum ve kayıt sayımı.
9. RPO/RTO ölçümü.

## 26.5 Hedefler

Pilot için başlangıç hedefi:

- RPO: 24 saatten iyi.
- RTO: 4 saatten iyi.

İş hacmi arttığında daha sık WAL/backup stratejisi değerlendirilebilir.

---

# 27. Test ve kalite stratejisi

## 27.1 Mevcut test katmanları

Repository içinde altı .NET test projesi ve web testleri vardır:

- Domain unit tests.
- Application tests.
- Persistence integration tests.
- API integration tests.
- Adapter contract tests.
- End-to-end tests.
- React/Vitest component tests.
- Playwright browser tests.

`.git`, `bin`, `obj`, `node_modules`, `dist` veya eski test sonuçlarının silinmesi test kaynaklarını silmez.

## 27.2 Zorunlu CI komutları

```bash
python3 scripts/verify-repository-cleanliness.py
dotnet restore MarketplaceHub.sln --locked-mode
dotnet build MarketplaceHub.sln --no-restore
dotnet test MarketplaceHub.sln --no-build --no-restore
dotnet format MarketplaceHub.sln --verify-no-changes --no-restore
cd src/MarketplaceHub.Web
npm ci --ignore-scripts
npm run typecheck
npm test -- --run
npm run build
```

Release image bu kapılar geçmeden yayınlanmaz.

## 27.3 Test piramidi

- Çok sayıda hızlı domain/application testi.
- PostgreSQL ile gerçek schema/integration testleri.
- Her adapter için anonim fixture tabanlı contract testleri.
- Fake adapter ile deterministik Worker E2E.
- Az sayıda gerçek Stage smoke ve safe-write testi.
- Production yalnız read-only smoke ve kontrollü canary.

## 27.4 Trendyol kabul senaryoları

- Geçerli ve geçersiz credential.
- Seller scope uyuşmazlığı.
- Kategori/marka pagination.
- Leaf kategori seçimi.
- Zorunlu özellik validation.
- Product create ve batch polling.
- Product update, duplicate barcode ve partial batch.
- Birleşik stok-fiyat.
- Timeout-after-success.
- Sipariş overlap ve duplicate.
- Out-of-order package event.
- İade boş liste, 404 ve gerçek claim.
- Fatura link duplicate, yanlış paket ve erişilemez URL.

## 27.5 E-Faturam kabul senaryoları

- Sign-in başarılı/auth hata/rate-limit/timeout.
- Mükellef kayıtlı ve kayıtsız.
- e-Fatura/e-Arşiv seçimi.
- Kuruş dönüşümü ve yuvarlama.
- Duplicate local reference.
- Submit timeout sonrası reconciliation.
- Processing/success/rejected statüler.
- PDF URL güvenlik kontrolleri.
- PDF checksum/private storage.
- İptal izinli/izinsiz/terminal durum.
- Trendyol teslim submitted/confirmed/duplicate.

## 27.6 Test verisi

- Gerçek credential, token, VKN/TCKN, müşteri adı/adresi fixture'a yazılmaz.
- Gerçek cevap anonimleştirilir.
- Fixture checksum ve kaynağın doğrulama tarihi tutulur.
- Contract değişiminde eski fixture korunur, yeni sürüm eklenir.

---

# 28. Faz planı ve yol haritası

Faz durumu “kod var/yok” yerine çıkış kapılarıyla yönetilir.

## F0 - Yetkili temel ve planlama

**Durum:** Tamamlandı.

Kapsam:

- Mimari kararlar.
- Bağımlılık sürümleri.
- Risk register.
- Threat model.
- Secret kataloğu.
- Operasyon kontrolleri.
- Traceability.

Çıkış: kaynak, bağımlılık ve risk temeli tanımlı.

## F1 - Güvenli temel

**Durum:** Yerel çekirdek hazır, production kanıtı eksik.

Kapsam:

- Identity ve session.
- Tenant sınırı.
- MFA altyapısı.
- PostgreSQL persistence.
- Job/inbox/idempotency.
- Audit.
- Private file storage.
- Docker/Caddy temel deployment.

Kalan: production permission matrisi, off-host backup ve runtime smoke.

## F2 - Katalog, import, stok ve fiyat çekirdeği

**Durum:** Yerel çekirdek hazır.

Kapsam:

- Ürün/varyant/kategori/marka/özellik.
- Dosya import staging.
- Mapping.
- Stok ledger.
- Channel offer.
- Yerel API/UI.

Kalan: dış publication ve birleşik stok-fiyat orkestrasyonu F3'e bağlı.

## F3 - Trendyol tamamlama

**Durum:** Aktif kapanış fazı.

Sıra:

1. Güncel Product V2 ve diğer endpoint sözleşmelerini doğrula.
2. Connection/capability Stage kabulü.
3. Kategori, marka, özellik, değer ve leaf testleri.
4. Approved product read ve identity mapping.
5. Create/update/archive komutlarını ayır.
6. Publication durable job ve batch polling.
7. Approved/unapproved read-back ile onay uzlaştırması ve uzak kimlik kaydı.
8. Satır bazlı partial failure ekranı.
9. Birleşik stok-fiyat job'ı.
10. Sipariş/package polling idempotency.
11. Webhook + polling reconciliation.
12. Return read ve izinli actionlar.
13. Invoice link `Submitted -> Confirmed` modeli.

Çıkış:

- İki ardışık read sync duplicate üretmez.
- Safe-write Stage kanıtı vardır.
- Batch satır sonuçları görünür.
- Timeout/retry/reconciliation geçer.
- Rollback ve capability kapatma adımı vardır.

## F4 - Trendyol E-Faturam tamamlama

**Durum:** Kod kapsamı tamamlandı ve statik doğrulandı; exact runtime, Stage/SIT ve production kabulü blokajlı.

Sıra:

1. Doğrudan API kullanıcı sign-in ve token kaynaklı company/user scope.
2. Trendyol siparişinden otomatik E-Fatura/E-Arşiv kararı.
3. Provider-managed mali hesap ve manuel policy onayı.
4. Canonical payload, otomatik internet satışı alanları ve kuruş testleri.
5. Submit ve duplicate koruması.
6. Status polling.
7. Güvenli permanent PDF indirme.
8. Private storage/checksum.
9. Cancellation guardları.
10. Trendyol link delivery reconciliation.

Güncel kod notu: doğrudan API_USER auth, token kaynaklı company/user scope, otomatik belge türü, E-Fatura/E-Arşiv create, E-Arşiv status/cancel, numeric durum kataloğu, güvenli permanent PDF, private storage ve Trendyol link tesliminde `Submitted -> ManualReview/Confirmed` modeli kodlanmıştır. Mali hesap/seri/senaryo/kargo/ödeme ayarları panelden kaldırılmıştır; E-Arşiv için zorunlu teknik alanlar sipariş ve resmî katalogdan otomatik üretilir. Giden E-Fatura UUID status yolu yalnız exact Stage/SIT kanıtı ile yapılandırılır; gerçek mali E2E ve teslim teyidi hâlâ dış doğrulama bekler.

Çıkış:

- Duplicate fatura yok.
- Uzak terminal durum olmadan yerel terminal başarı yok.
- PDF güvenli ve erişim kontrollü.
- Mali iptal/adjustment kuralları yazılı onaylı.
- Stage E2E kanıtı var.

## F5 - Production pilot

**Durum:** Planlandı, F3 ve F4 kapanmadan başlanmaz.

Kapsam:

- Temiz Ubuntu sunucu kurulumu.
- Domain/TLS.
- Production secret yükleme.
- Immutable image deployment.
- Migration ve restore kanıtı.
- İlk bağlantılar read-only.
- Kontrollü ürün veya fatura canary.
- Günlük operasyon runbook'u.

Çıkış:

- Read-only smoke başarılı.
- Küçük kontrollü write canary başarılı.
- Alert ve backup aktif.
- Rollback test edildi.

## F6 - Stabilizasyon ve operasyon kabulü

**Durum:** Planlandı.

Kapsam:

- 30 günlük pilot gözlem.
- Hata sınıfları ve retry tuning.
- Performans ve rate-limit ayarı.
- Operasyon ekranı iyileştirmeleri.
- Runbook ve eğitim.
- Restore drill.
- Güvenlik gözden geçirmesi.

Çıkış:

- Kritik P0/P1 açık yok.
- Queue ve sync SLA hedefleri sağlanıyor.
- Operasyon ekibi günlük akışı yürütebiliyor.

## F7 - Yeni platform ekleme altyapısının sertleştirilmesi

**Durum:** Planlandı; aktif entegrasyon değildir.

Kapsam:

- `IPlatformAdapterResolver/Registry`.
- Platform modül tanımı.
- Credential schema registry.
- Capability descriptor registry.
- Mapping UI'nın platform bağımsızlaştırılması.
- Job dispatch'in adapter resolver kullanması.
- Ortak contract test kit'i.
- Yeni platform bootstrap şablonu.

Çıkış:

- Yeni platform ana servislerde if/else yaymadan eklenebilir.
- Fake adapter şablonu ve kalite kapıları hazırdır.

## F8 ve sonrası - Platformların tek tek eklenmesi

Her platform ayrı faz ve ayrı ADR ile açılır. Önerilen varsayılan sıra:

1. Hepsiburada.
2. N11.
3. Pazarama.
4. PTTAVM.
5. Shopify.

Bu sıra iş ihtiyacı ve teknik uygunluk onayıyla güncellenebilir.

Her platform fazı aşağıdaki alt kapıları izler:

1. Resmî doküman ve sözleşme envanteri.
2. Test hesabı ve credential modeli.
3. Connection/capability.
4. Reference/category/attribute mapping.
5. Product read.
6. Product write ve batch sonucu.
7. Stock/price.
8. Order/package.
9. Return/cancel.
10. Invoice delivery veya ilgili mali akış.
11. Contract/Stage/E2E.
12. Pilot ve stabilization.

Bir platform tamamlanmadan sonraki platformun write geliştirmesi başlamaz.

---

# 29. Yeni platform ekleme standardı

Yeni adapter aşağıdaki klasör ve sözleşme yapısını izlemelidir:

```text
Infrastructure/Adapters/<Platform>/
  Contracts/
  Mapping/
  ErrorMapping/
  Fixtures/
  Ports/
  AuthenticationHandler
  HttpClient
  Options
  README
```

Gerekli belgeler:

- Platform ADR.
- Capability matrix satırları.
- Resmî endpoint envanteri.
- Credential ve secret tanımı.
- Rate-limit tablosu.
- Hata mapping tablosu.
- Anonim fixture seti.
- Stage evidence log.
- Reconciliation ve rollback runbook'u.

Adapter platform DTO'sunu canonical Application modellerine çevirir. Domain'e platform ismi veya ham JSON davranışı sızdırmaz.

---

# 30. Production kabul kriterleri

Sistem production-ready ilan edilmeden aşağıdaki maddelerin tamamı sağlanmalıdır.

## 30.1 Kod ve CI

- Locked restore başarılı.
- Backend build/test/format başarılı.
- Web typecheck/test/build başarılı.
- Repository cleanliness başarılı.
- Dependency ve container digestleri kayıtlı.
- Kritik security scan bulgusu yok veya onaylı risk kaydı var.

## 30.2 Trendyol

- Connection ve scope doğrulandı.
- Referans sync geçti.
- Product create/update batch geçti.
- Stock/price geçti.
- Order/package duplicate testleri geçti.
- Invoice link gerçek package ile geçti.
- Unsupported write capability kapalı.

## 30.3 E-Faturam

- Test firma ve sign-in geçti.
- Token kaynaklı company/user scope ve otomatik belge türü kararı geçti.
- e-Fatura/e-Arşiv doğru seçildi.
- Submit ve status terminale ulaştı.
- PDF güvenli indirildi ve checksum doğrulandı.
- İptal senaryosu test edildi veya capability açıkça kapalı.

## 30.4 Operasyon

- TLS ve AllowedHosts doğru.
- Secretlar dosya/kasa üzerinden.
- Backup off-host.
- Restore temiz hedefte geçti.
- Alertler çalışıyor.
- Rollback test edildi.
- Kullanıcı ve MFA politikası uygulandı.
- Audit ve log redaction kontrol edildi.

## 30.5 İş onayı

- Mali policy işletme/muhasebe tarafından onaylandı.
- Otomatik aksiyonların sınırı onaylandı.
- İlk canary ürün/sipariş/fatura listesi belirlendi.
- Production external-write açma kararı kayıtlı.

---

# 31. Riskler ve çözüm planları

| Öncelik | Risk | Etki | Çözüm |
|---|---|---|---|
| P0 | Ürün adapterı var fakat application publication orchestration eksik | UI'dan gerçek yayın tamamlanmaz | Durable publication job + batch poll + satır sonucu |
| P0 | Create/update ayrılmamış | Yanlış endpoint ve veri bozulması | Ayrı command/port sözleşmeleri |
| P0 | Stok ve fiyat ayrı modellenmiş | Trendyol birleşik isteğiyle uyumsuz | PriceInventoryBatch tasarımı |
| P0 | E-Faturam exact runtime/Stage mali E2E eksik | Sözleşme drift veya yanlış production aktivasyonu | Capability/write kapılarını kapalı tut; F4 Stage kabulünü tamamla |
| P1 | Provider URL'si güven sınırına alınmamış | SSRF veya hatalı dosya | Exact host/IP/redirect/PDF guard |
| P1 | Fatura linki uzun süre erişilebilirlik garantisi yok | Yasal/operasyonel teslim sorunu | Retention sözleşmesi + probe + private kopya |
| P1 | Backup aynı hostta kalabilir | Host kaybında veri ve yedek kaybı | Şifreli off-host ve restore drill |
| P1 | Gerçek Stage kanıtı eksik | Kod production'da çalışmayabilir | Tarihli E2E evidence |
| P1 | Platform seçimi bazı yerlerde sabit kodlu | Yeni adapter ekleme maliyeti | F7 resolver/registry |
| P2 | Uzun persistence metotları | Review ve hata ayıklama maliyeti | Use-case sınıflarına böl, formatter |
| P2 | Metrik ve alarm seti eksik | Hatalar geç fark edilir | Observability fazı |
| P2 | İnce taneli permission matrisi eksik | Fazla yetki | Role/permission tamamla |

---

# 32. Repository ve dosya temizliği politikası

Ana geliştirme repository'sinde Git geçmişi korunur. Temiz release paketinde `.git`, `bin`, `obj`, `node_modules`, `dist`, test sonucu, log, PostgreSQL data/WAL, secret, geçici PDF/PNG ve iç içe arşiv bulunmaz. Bu iki amaç birbirine karıştırılmaz.

Kaynak temizliği `scripts/verify-repository-cleanliness.py` ile; dokümantasyon işlemi `scripts/verify-documentation-transaction.py` ile doğrulanır. Uygulanmış migration dosyaları migration zincirinin sabit parçalarıdır; dosya adları veya içerikleri geriye dönük değiştirilmez.

Geliştirme paketi, geçmişi ve değişiklik takibini korumak için `.git` içerebilir; deployment artefaktı ve container image `.git` içermez.


# 33. Codex devralma talimatı

Codex işe başlamadan önce `AGENTS.md` içindeki zorunlu okuma sırasını uygular. Önce ana belgeyi, makine durum kaydını, güncel fazı, capability matrisini ve ilgili evidence logu okur.

Codex için bağlayıcı kurallar:

- Aktif kapsam yalnız Trendyol ve Trendyol E-Faturam'dır.
- Yeni platform adapterı F7 kapanmadan başlatılmaz.
- Test çalıştırılmadan sonuç başarılı gösterilmez.
- Geliştirme döngüsünde hedefli test, faz/release kapısında tam test kullanılır.
- Tam test logu prompta taşınmaz; özet ve evidence yolu tutulur.
- Bir işin durumunu değiştiren commit dokümantasyon transaction dosyalarını birlikte günceller.
- Uygulanmış migration silinmez veya yeniden adlandırılmaz.
- Dış write açık onay, supported capability ve rollback olmadan etkinleştirilmez.
- Git geçmişi korunur; release paketinden `.git` çıkarılması ana repository geçmişini silmek için kullanılmaz.


# 34. Teknik sürüm tabanı

Mevcut repository pinleri:

| Bileşen | Sürüm |
|---|---|
| .NET SDK | 10.0.302 |
| ASP.NET Core / EF Core | 10.0.10 |
| PostgreSQL image | 18.4, digest ile sabit |
| Npgsql | 10.0.3 |
| Node.js | 24.18.1 |
| npm | 11.12.1 |
| React | 19.2.8 |
| TypeScript | 6.0.3 |
| Vite | 8.1.5 |
| Vitest | 4.1.10 |
| Playwright | 1.62.1 |
| Docker Compose | 2.40.2 hedef doğrulama sürümü |

Sürüm yükseltmesi otomatik yapılmaz. Resmî release note, compatibility, locked restore, test ve ayrı dependency evidence ile yapılır.

---

# 35. Resmî dış kaynaklar ve doğrulama notları

Bu belge hazırlanırken 4 Ağustos 2026 itibarıyla aşağıdaki resmî kaynak sınıfları esas alınmıştır:

- Trendyol Developers: Integration Services Overview.
- Trendyol Product V2 API Endpoint ve Product Create V2.
- Trendyol Product Update - Approved Product V2.
- Trendyol Stock and Price Update.
- Trendyol Check Batch Request Result.
- Trendyol Get Shipment Packages.
- Trendyol Send Customer Invoice Link.
- Trendyol Service Limitations.
- Trendyol E-Faturam Entegrasyon Dokümanı.
- Trendyol E-Faturam sign-in, mükellef, e-Fatura/e-Arşiv create, status, document download ve cancel dokümanları.
- Trendyol E-Faturam statü kodları.

Dış API dokümanları değişebileceği için her Stage kabulünden önce endpoint, sürüm, header, limit ve zorunlu alanlar yeniden doğrulanır. Belgedeki endpoint bilgisi tek başına canlı çağrı yetkisi değildir.

---

# 36. Sözlük

| Terim | Açıklama |
|---|---|
| API | İki yazılımın kurallı biçimde veri ve işlem paylaşmasını sağlayan arayüz |
| Adapter | Dış platform sözleşmesini sistemin ortak modellerine çeviren katman |
| Port | Application katmanının dış yetenek için tanımladığı interface |
| DTO | Katmanlar veya sistemler arasında taşınan veri sözleşmesi |
| Worker | Arka planda job alıp uzun/dış işlemleri yürüten proses |
| Job | Sonradan Worker tarafından işlenecek kalıcı iş emri |
| Lease | Bir job'ın belirli süre tek Worker tarafından sahiplenilmesi |
| Heartbeat | Worker'ın job'ı hâlâ işlediğini belirten lease yenilemesi |
| Idempotency | Aynı komut tekrarlandığında ikinci yan etki üretmeme özelliği |
| Reconciliation | Yerel kayıt ile uzak gerçek durumu karşılaştırıp farkı bulma |
| Capability | Belirli bağlantının belirli işlemi kanıtlı biçimde yapabilmesi |
| Fixture | Dış API cevabının anonim ve testte kullanılan örneği |
| Stage | Canlıdan ayrı resmî test ortamı |
| Production | Gerçek işletme ve müşteri verisinin işlendiği canlı ortam |
| Migration | Veritabanı şemasını versiyonlu değiştiren kayıt |
| Immutable image | İçeriği digest ile sabitlenmiş container image |
| RPO | Kabul edilebilir veri kaybı süresi |
| RTO | Hizmeti geri getirme hedef süresi |
| ETTN/UUID | Elektronik belgenin tekil kimliği |
| Dead-letter | Otomatik denemeleri bitmiş ve manuel inceleme gereken job |

---

# 37. Nihai sistem özeti

1. Proje yalnız Trendyol ve Trendyol E-Faturam ile tamamlanacaktır.
2. Bu iki entegrasyon production stabilizasyonunu geçmeden başka platform açılmayacaktır.
3. Ortak katalog, mapping, job, audit, reconciliation ve güvenlik altyapısı korunacaktır.
4. Yeni platformlar güncel resmî API sözleşmeleriyle adapter standardına göre yazılacaktır.
5. Product V2, asenkron batch ve batch result takibi zorunludur.
6. Stok ve fiyat Trendyol'a birleşik komut olarak gönderilecektir.
7. Fatura submit sonucu uzak terminal durumla doğrulanacaktır.
8. Fatura PDF güvenli private storage'da tutulacaktır.
9. External write varsayılan kapalı ve çift anahtarlı olacaktır.
10. Test, Stage evidence, backup/restore ve rollback olmadan production-ready denmeyecektir.
11. F3/F4 sonrasında production pilot, stabilizasyon ve adapter registry fazları tamamlanacaktır.
12. Sonraki platformlar ayrı adapter kapsamı ve çıkış kapılarıyla tek tek eklenecektir.

**Bu belge Ravencia MarketplaceHub için nihai ürün ve teknik ana çerçevedir.**
