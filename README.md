# Ravencia MarketplaceHub

> **v10.32 arayüz notu:** Güvenlik ekranı sonlandırılmış oturum kayıtlarını tekil veya toplu temizleyebilir; aktif/mevcut oturum koruması devam eder. Sipariş ürün görselleri büyütülebilir. Eşleştirme merkezi özellik kartı seçimi, seçili özelliğe değer ekleme ve eksik Trendyol kategori özellik verisini yeniden eşitleme akışını aynı ekranda sunar. Kullanılmayan genel faturalama ayar sayfası menüden kaldırılmıştır.

> **Codex/devralma başlangıç noktası:** Önce [`AGENTS.md`](AGENTS.md), [`RAVENCIA-NIHAI-PROJE-BELGESI.md`](docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md), [`PROJECT-STATUS.yaml`](docs/implementation/PROJECT-STATUS.yaml) ve [`CURRENT-PHASE.md`](docs/implementation/CURRENT-PHASE.md) dosyalarını okuyun. Aktif durum `F3_CORE_CODE_COMPLETE_VALIDATION_PENDING / F4_CODE_COMPLETE_VALIDATION_PENDING / PRODUCTION_BLOCKED` olarak işaretlenmiştir.

Ravencia MarketplaceHub, tek işletmenin Trendyol satış, ürün, sipariş, iade ve Trendyol E-Faturam süreçlerini aynı panelden yönetmesi için geliştirilen modüler monolit uygulamadır. v10 panel arayüzü ortak sayfa, form ve kart düzeni kullanır; masaüstü menüsü kalıcı ikon görünümüne daraltılabilir. Güvenlik ekranı Authenticator kurulumunu ve server-side oturum sonlandırmayı mevcut güvenli API akışlarıyla yönetir. Sipariş satırında müşteri, adres, ürün, tutar ve kargo ayrıntıları birlikte gösterilir; ayrı sipariş detay sayfası kullanılmaz. “Fatura Oluştur” önce API kaynaklı müşteri, adres, ürün, KDV ve toplam taslağını gösterir; gerçek E-Faturam gönderimi parola ve açık onay kapısında kalır. Sipariş işlem menüleri görünür alana göre yönlenir; mikro ihracat resmî platform alanları ve belgelenmiş tarihsel partner sinyaliyle ayırt edilir, mavi satır çizgisiyle ve yalnız fatura sütunundaki kısa rozetle gösterilir. Uzak termin alanı yoksa tarih uydurulmaz. Katalogda ürün ekleme ve düzenleme aynı çalışma alanı hiyerarşisini kullanır: temel bilgiler, kategori özellikleri, varyant stok/fiyat, ölçü/desi, görsel ve yayın alanları birlikte yönetilir. Eşleştirme merkezi yalnız aktif Trendyol kapsamını gösterir; kategori ve marka seçimleri aranabilir karşılıklı kartlarda yapılır, yerel panel kategorisi aynı ekrandan oluşturulabilir.


## Yetkili nihai belge

Ana proje planı, kullanıcı paneli işleyişi, mimari, güvenlik, test, production kabulü ve gelecekte yeni platform ekleme planı için ana kaynak: [RAVENCIA-NIHAI-PROJE-BELGESI.md](docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md). Makinece okunabilir durum `PROJECT-STATUS.yaml`, güncel faz ve anlık blokajlar `CURRENT-PHASE.md` içinde tutulur. Kronolojik değişiklik özeti `docs/CHANGELOG.md` içindedir.

## Aktif kapsam

Yeni geliştirme ve doğrulama yalnız iki entegrasyon kodunda yapılır:

- `TRENDYOL`
- `TRENDYOL_EFATURAM`

Diğer pazaryerlerine ait adapter, UI seçeneği, Worker yönlendirmesi ve job türleri mevcut kapsamda etkin değildir. Yeni platform ancak bu iki entegrasyonun tamamlanma kapıları geçildikten sonra ayrı adapter fazı ve kabul kapılarıyla eklenir. Ayrıntılı kapsam: [current-scope.md](docs/specification/current-scope.md).

## Mimari

- .NET `10.0.302`: API, Worker, Application, Domain ve Infrastructure katmanları
- React + TypeScript: yönetim paneli
- PostgreSQL `18.4`: kalıcı veri, idempotency, inbox ve job lease kayıtları
- Docker Compose + Caddy: Linux container üretim çalıştırması
- Tek işletme / tek aktif tenant; ikinci tenant ve aktif multi-tenant yüzeyi yok
- Dış yazmalar iki ayrı kapı ile varsayılan kapalı: global `FeatureFlags__ExternalWrites=false` ve bağlantı ayarı

## Uygulama durumu

| Alan | Durum |
| --- | --- |
| Kimlik, oturum, MFA altyapısı, tenant sınırı | Yerel çekirdek hazır |
| Yerel ürün/katalog/stok/fiyat modeli ve içe aktarım | v9 katalog, kategori özellikleri ve varyant çalışma alanı kodlandı; statik doğrulama geçti |
| Trendyol bağlantı, kategori/marka/özellik/değer okuma | Kodlandı; gerçek hesapla tekrar doğrulanmalı |
| Trendyol ürün ve sipariş okuma | Kodlandı; gerçek hesapla kabul testi gerekli |
| Trendyol Product V2 create/update/archive + approval | Kodlandı ve statik doğrulandı; dynamic/Stage kabulü gerekli |
| Trendyol birleşik stok + fiyat yazma | Kodlandı; sürüm korumalı batch, dynamic/Stage kabulü gerekli |
| Trendyol Order V2, paket, etiket ve iade | Kodlandı; capability evidence ve Stage fixture gerekli |
| E-Faturam doğrudan API_USER giriş, token kaynaklı mali kapsam, otomatik E-Fatura/E-Arşiv seçimi, fatura gönderimi, numeric durum, permanent PDF ve E-Arşiv iptal | Kod kapsamı tamamlandı; exact runtime ve Stage mali E2E gerekli |
| Giden E-Fatura UUID durum sorgusu | Exact Stage/SIT endpoint kanıtı gelene kadar yapılandırma düzeyinde fail-closed |
| Fatura linkini Trendyol’a iletme | Kodlandı; gerçek package ile Stage kabul testi gerekli |
| Production kabulü | Engelli; dış hesap, mali karar, backup ve E2E kanıtları gerekiyor |

v9 katalog işleyişi: [v9-catalog-workspace.md](docs/implementation/v9-catalog-workspace.md).

“Adapter kodu var” ifadesi “production’da tamamen çalışıyor” anlamına gelmez. Güncel capability tablosu [capability-matrix.md](docs/platform-rules/capability-matrix.md), detaylı inceleme [2026-08-04-project-review.md](docs/reviews/2026-08-04-project-review.md) içindedir.

## Gereksinimler

- .NET SDK `10.0.302`
- Node.js `24.18.1`
- npm `11.12.1`
- PostgreSQL `18.4`
- Docker Engine ve Compose `2.40.2`

## Doğrulama

Günlük geliştirmede bu tam komut listesi otomatik çalıştırılmaz. UI/metin değişikliklerinde ekran önizlemesi, işlevsel değişikliklerde ise etkilenen proje için en küçük build veya hedefli test yeterlidir. Aşağıdaki tam doğrulama; kullanıcı istediğinde, release/tag veya production deploy öncesinde çalıştırılır.

Kaynak ağacını derlemeden önce:

```bash
python3 scripts/verify-repository-cleanliness.py
# Teslim için oluşturulan kopyada ayrıca:
python3 scripts/verify-repository-cleanliness.py --package
```

Backend:

```bash
dotnet restore MarketplaceHub.sln --locked-mode
dotnet build MarketplaceHub.sln --no-restore
dotnet test MarketplaceHub.sln --no-build --no-restore
dotnet format MarketplaceHub.sln --verify-no-changes --no-restore
```

Web:

```bash
cd src/MarketplaceHub.Web
npm ci --ignore-scripts
npm run typecheck
npm test -- --run
npm run build
```

## Container çalıştırma

Secret dosyaları `deploy/secrets/` altında yerel olarak oluşturulur ve Git’e eklenmez. Yalnız Caddy host portu açar; API, Worker ve PostgreSQL backend ağında kalır.

```bash
docker compose -f deploy/compose/compose.yaml up -d
```

Production override kullanılırken aşağıdakiler zorunludur:

- `MARKETPLACEHUB_ALLOWED_HOSTS`
- `MARKETPLACEHUB_SITE_ADDRESS`
- digest ile sabitlenmiş `MARKETPLACEHUB_APP_IMAGE`
- digest ile sabitlenmiş `MARKETPLACEHUB_EDGE_IMAGE`
- Data Protection sertifikası ve secret dosyaları

API container sağlık kontrolü gerçek `/health/ready` endpoint’ini çağırır; yalnız prosesin varlığı başarı sayılmaz. Caddy production alan adında varsayılan otomatik TLS akışını kullanır; `tls internal` ile özel CA sertifikasına zorlanmaz.

## Git geçmişi ve paketler

Ana geliştirme repository'sinde `.git` geçmişi korunur. Codex ve geliştirici commit, tag, diff ve blame bilgilerini buradan kullanır. Temiz release/deployment paketinde ise `.git`, secret, runtime veri ve üretilmiş çıktılar bulunmaz. Production teslimi tercihen CI tarafından üretilen immutable image digestleriyle yapılır.

## Depo temizliği

Kaynak paketine `.git`, `bin`, `obj`, `node_modules`, `dist`, test çıktısı, log, PostgreSQL data/WAL, secret, PDF üretim çıktısı veya arşiv dosyası eklenmez. Temiz paket üretmeden önce temizlik doğrulayıcısı çalıştırılır.

Uygulanmış migration dosyaları geçmiş zinciridir ve adında eski bir faz kodu bulunsa bile silinmez veya yeniden adlandırılmaz. Migration kimliğinin değiştirilmesi mevcut veritabanlarının yükseltme geçmişini bozabilir.
