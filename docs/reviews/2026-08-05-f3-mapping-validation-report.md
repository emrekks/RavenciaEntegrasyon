# Ravencia MarketplaceHub — F3 Eşleme ve Son Doğrulama Raporu

**İnceleme tarihi:** 2026-08-05

**İncelenen dal:** `production-hardening-v7`

**Başlangıç HEAD:** `b7acd9a`
**Kapsam:** F3 birleşik kategori–özellik–değer eşleme regression düzeltmesi, ana işleyiş belgesi uyumu ve çalıştırılabilir doğrulama kapıları

## 1. Sonuç özeti

`F3Pages.test.tsx` içindeki eski doğrudan bileşen bağımlılığı kaldırıldı. Test artık uygulamanın gerçek route girişinde kullandığı `MappingPage kind="attributes"` bileşenini çalıştırır ve aşağıdaki zinciri tek senaryoda doğrular:

1. ACTIVE Trendyol bağlantısı seçimi.
2. Doğrulanmış yerel yaprak kategori kapsamı seçimi.
3. Kategori snapshot'ından zorunlu uzak özellik seçimi.
4. Özellik eşleme PUT URL ve JSON payload doğrulaması.
5. Doğrulanmış özellik eşlemesinden sonra değer bölümünün açılması.
6. Özellik değeri snapshot'ı ve değer eşleme PUT URL/payload doğrulaması.

Playwright tarafında güncel uygulama kabuğu ve rol görünürlüğüyle uyumsuz eski beklentiler düzeltildi. Ayrıca `/mappings/attributes` route'u üzerinde aynı kategori kapsamı → özellik → değer zincirini tarayıcı seviyesinde doğrulayan ayrı senaryo eklendi.

Ana proje planı 6.1'e çıkarıldı. Birleşik eşleme işleyişi, marka eşlemesinin ayrı görünümü, F4 güvenli PDF sınırı, Trendyol fatura linkinin `Submitted`/manuel teyit modeli ve eksik `ManualReview` job durumu kodla uyumlu hale getirildi.

**Production kararı değişmemiştir: `BLOCKED`.** Kodlanan regression testleri exact toolchain ve dış Stage bağımlılıkları sağlanmadan dinamik başarı olarak kabul edilmemiştir.

## 2. Değiştirilen doğrulama kapsamı

| Alan | Dosya | Son durum |
| --- | --- | --- |
| Vitest birleşik eşleme akışı | `src/MarketplaceHub.Web/src/F3Pages.test.tsx` | Eski doğrudan `AttributeMappingPage` testi kaldırıldı; route bileşeni, kategori kapsamı, özellik ve değer payload zinciri kapsandı. |
| Playwright uygulama kabuğu | `src/MarketplaceHub.Web/e2e/f1-shell.spec.ts` | `İşlem Takibi`, Ayarlar alt menüsü ve `OWNER` için Faturalama görünürlüğü güncellendi. |
| Playwright F3 eşleme | `src/MarketplaceHub.Web/e2e/f3-mapping.spec.ts` | Gerçek route üzerinde iki PUT işlemini ve payload'ları doğrulayan browser senaryosu eklendi. |
| Ana plan | `docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md` | Sürüm 6.1; eşleme ve F4 mevcut kod davranışıyla hizalandı. |
| Durum/evidence/izlenebilirlik | `PROJECT-STATUS.yaml`, `CURRENT-PHASE.md`, `F3-evidence-log.md`, `traceability-matrix.md`, `CHANGELOG.md` | Dinamik çalıştırma sonucu ile statik kod durumunun birbirine karışması engellendi. |

## 3. Gerçekleştirilen kontroller

| Kontrol | Sonuç | Kanıt / sınır |
| --- | --- | --- |
| Değiştirilen TS/TSX dosyalarının syntax transpile kontrolü | `PASS_STATIC` | Global TypeScript 5.8.3 ile `transpileModule`; üç değiştirilmiş test dosyası sözdizimi hatası üretmedi. Exact proje TypeScript sürümü değildir ve tam typecheck yerine geçmez. |
| JSON parse | `PASS_STATIC` | Repository içindeki kaynak JSON dosyaları parse edildi. |
| YAML parse | `PASS_STATIC` | 7 YAML/YML dosyası parse edildi. |
| XML/MSBuild parse | `PASS_STATIC` | 15 `.csproj`/`.props`/`.targets`/XML dosyası parse edildi. |
| Shell syntax | `PASS_STATIC` | 5 shell dosyası `bash -n` kontrolünden geçti. |
| Python syntax | `PASS_STATIC` | 2 script `py_compile` ile kontrol edildi; oluşan `__pycache__` temizlendi. |
| Repository cleanliness | `PASS` | `scripts/verify-repository-cleanliness.py`. |
| Dokümantasyon transaction | `PASS` | `scripts/verify-documentation-transaction.py`. |
| Git whitespace/diff kontrolü | `PASS` | `git diff --check`. |
| `npm ci`, exact TypeScript typecheck, Vitest, Vite build | `BLOCKED_ENVIRONMENT` | Ortam Node 22.16.0/npm 10.9.2; proje Node 24.18.1/npm 11.12.1 ister. İç npm aynası `zod@4.4.3` paketinde 404 üretmiştir; dış registry DNS erişimi yoktur. |
| Playwright dinamik browser çalıştırması | `BLOCKED_ENVIRONMENT` | Exact npm bağımlılıkları ve Playwright browser kurulumu yapılamadı. Senaryo yalnız statik/sözdizimsel olarak doğrulandı. |
| `.NET restore/build/test/format` | `BLOCKED_ENVIRONMENT` | .NET SDK kurulu değildir; proje tarafından sabitlenen SDK çalıştırılamadı. |
| Docker Compose config/build/smoke | `BLOCKED_ENVIRONMENT` | Docker CLI/daemon kurulu değildir. |
| Trendyol/E-Faturam Stage testleri | `BLOCKED_EXTERNAL` | Stage credential, capability scope, kontrollü test verisi ve erişilebilir dış ağ gerektirir. |

## 4. Belge denetiminde kapatılan tutarsızlıklar

- Eşleme ekranı artık belgede ayrı ve eski özellik ekranı gibi anlatılmıyor; kategori kapsamı seçildikten sonra özellik ve değer adımlarının aynı çalışma alanında açıldığı açıkça yazıldı.
- Özellik değeri eşlemesinin, doğrulanmış özellik eşlemesinden önce açılamayacağı belirtildi.
- Marka eşlemesinin aynı snapshot güvenlik kurallarını kullandığı fakat ayrı görünüm olduğu netleştirildi.
- Güvenli PDF indirme sınırı “henüz yapılmadı” durumundan çıkarıldı; exact host allow-list, DNS/IP, redirect, boyut, MIME ve PDF imza kontrollerinin kodlandığı, Stage doğrulamasının eksik olduğu belirtildi.
- Trendyol fatura linkinde HTTP 2xx'in terminal başarı olmadığı; önce `Submitted`, doğrulama yoksa `ManualReview` olduğu işlendi.
- Job durumlarına kodda bulunan `ManualReview` eklendi.

## 5. Devam eden açık konular

Aşağıdaki maddeler bu değişiklik paketinde yanlış biçimde “tamamlandı” işaretlenmemiştir:

1. Trendyol ürün yayınlama application orchestration ve durable job akışı.
2. Product create/update/archive komutlarının ayrılması ve batch result polling.
3. Birleşik fiyat–stok uzak komutu ve satır bazlı partial-result yönetimi.
4. Sipariş/paket/iade Stage read/write kabul testleri ve capability kanıtları.
5. E-Faturam taxpayer sorgusu, provider status polling ve cancel akışı.
6. Gerçek Stage PDF hostu, private storage/kalıcı erişim ve fatura teslim teyidi.
7. Off-host backup ve temiz volume restore tatbikatı.
8. Exact Node/.NET/Docker toolchain ile CI ve yerel dinamik testlerin başarılı çalıştırılması.

## 6. Son karar

F3 frontend test eskimesi kaynak düzeyinde kapatılmış ve mevcut birleşik kullanıcı akışına göre yeniden tasarlanmıştır. Repository ve dokümantasyon statik kapıları geçmiştir. Ancak exact Vitest, Playwright, .NET, Docker ve Stage koşuları üretilemediği için teslim **“kod ve belge uyumu tamamlandı; dinamik yeniden doğrulama zorunlu”** durumundadır. Production açma kararı verilmemiştir.
