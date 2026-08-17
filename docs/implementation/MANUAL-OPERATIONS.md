# Manuel Proje İlerletme ve Teslim Modeli

**Yürürlük:** 17 Ağustos 2026  
**Kapsam:** Yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM`

## Çalışma kuralı

Proje, ürün sahibinin manuel öncelikleriyle ilerler. Bir geliştirme ancak aşağıdaki zincir tamamlanınca **tamamlandı** sayılır:

1. Kaynak değişikliği uygulanır ve riskine uygun hedefli kontrol çalıştırılır.
2. Durum, evidence ve changelog belgeleri aynı transaction içinde güncellenir.
3. Değişiklik commit edilir ve `main` dalına push edilir.
4. GitHub source CI başarılı olur.
5. Dağıtılabilir değişiklikte immutable app/edge image üretilir; digest kayda alınır.
6. Ubuntu hedefine, doğrulanmış backup sonrasında yalnız bu digest'ler deploy edilir; migration, container health ve dış `/health/ready` kontrol edilir.
7. Sonuç, dağıtılan commit/digest, kontroller ve `NOT_RUN`/dış blokajlar kullanıcıya raporlanır.

Yalnız yerel dosya düzenlenmesi, commit, push veya GitHub Actions başarısı teslim değildir. UI veya davranış değişikliği istenmişse sonuç hedef panel ortamında görünür olmalıdır. Kullanıcı açıkça yalnız taslak/dokümantasyon ya da deploy edilmemesini isterse istisna raporda yazılır.

## GitHub Actions gerekli mi?

Uygulamanın çalışması için GitHub Actions teknik olarak zorunlu değildir; Ubuntu üzerindeki Docker Compose doğrulanmış image ile çalışır. Ancak bu proje için güvenilir dağıtım zincirinin **zorunlu release kapısıdır**: kaynak doğrulaması, immutable app/edge image üretimi, digest ve release izlenebilirliğini sağlar. Normal teslimde Actions atlanmaz. Acil manuel müdahalede de yerel/etiketsiz image ile production'a çıkılmaz; aynı commit'in doğrulanmış digest'i kullanılır ve işlem belgelenir.

## Aktif teknik durum

| Alan | Durum | Manuel ilerleme için anlamı |
| --- | --- | --- |
| F2 katalog ve ürün çalışma alanı | Kodlandı, hedefli kontroller geçti; dinamik tarayıcı/Stage kabulü açık | Her UI değişikliği deploy sonrası panelden kontrol edilmeli. |
| F3 Trendyol | Çekirdek kod tamam; Stage yeniden kabulü açık | Ürün onay işi `PRODUCT_APPROVAL_PENDING`; provider terminal sonucu bekleniyor, duplicate create gönderilmez. |
| F4 Trendyol E-Faturam | Çekirdek kod tamam; Stage mali E2E açık | Direct API_USER giriş testi başarılı; gerçek create endpointi `401 EFATURAM_ACCESS_TOKEN_REJECTED` veriyor. Yetki bypass edilmez. |
| F5 production pilotu | Başlamadı | F3/F4 dış kabul ve dinamik revalidation kapanmadan başlatılmaz. |
| F6 stabilizasyon | Planlandı | Pilot sonrası operasyon kabulü, alarm/geri dönüş ve günlük kullanım doğrulanır. |
| F7 adapter registry | Planlandı | F5/F6 sonrası ele alınır; yeni pazaryeri eklenmez. |

## Sistemin çalışması için gerekenler

| Sistem | Gerekli işlev | Durum |
| --- | --- | --- |
| GitHub repository + `main` | Kaynak kaydı ve izlenebilir geçmiş | Zorunlu |
| GitHub Actions | Source CI ve immutable release publish | Normal deploy için zorunlu kapı |
| GHCR | Digest-pinned app/edge image saklama | Zorunlu |
| Ubuntu + Docker Compose | API, Worker, PostgreSQL ve edge çalıştırma | Zorunlu |
| PostgreSQL volume | Uygulama ve durable job verisi | Zorunlu; deploy öncesi backup |
| Caddy/DNS/TLS | Panel HTTPS erişimi | Zorunlu; deploy sonrası readiness |
| Trendyol Stage | Ürün, iade, sipariş ve etiket Stage kabulü | Aktif, dış kabul işleri açık |
| E-Faturam Stage API_USER | Mali belge işlemleri | Giriş doğrulandı; protected create yetkisi açık |
| Şifreli credential/secrets | Provider kimlik bilgileri | Zorunlu; secret loglanmaz |
| Backup + restore doğrulaması | Geri dönüş ve veri güvenliği | Zorunlu |

## Tamamlanması gereken dış sistem/erişimler

1. E-Faturam sağlayıcısında mevcut Stage API_USER hesabı için invoice-create/protected endpoint yetkisi.
2. Trendyol tarafında mevcut ürün denemesinin terminal approval/rejection sonucu; yalnız read-back polling sürer.
3. Production Docker socket'ini kullanmayan sınırlı PostgreSQL/Testcontainers test runner; bu test kanıtı altyapısıdır, güvenlik azaltması değildir.

## Ürün sahibine öneriler

- Talepleri küçük, deploy edilebilir parçalara bölüp her parça sonunda panel kabulü vermek.
- F5 öncesinde Trendyol product approval, iade read, etiket/sipariş ve E-Faturam belge zincirini Stage'de tek tek kapatmak.
- Off-host şifreli backup kopyası, düzenli restore drill, uptime/worker/backup/provider hata alarmları eklemek.
- Credential rotasyonlarını kaydetmek; parola, token ve secret'ları iş kaydı veya loglara yazmamak.
- Production pilotunda küçük kapsam ve açık rollback listesi kullanmak.

## Korunan güvenlik sınırları

Stage manuel işlemleri capability/evidence/fixture SHA, fiscal-policy, connection-write switch, `AUTO_*`, re-auth veya ek onayla bloke edilmez. Stage/Production endpoint-credential eşleşmesi, yetkilendirme, input doğrulama, idempotency, provider response validation ve reconciliation korunur.

Production manuel write zinciri değişmez: authorization → master external-write switch → aktif connection/authentication → validation → duplicate koruması → provider → reconciliation/audit. Otomatik write bunlara ek ilgili `AUTO_*` bayrağını ister. Bu belge hiçbir Production kontrolünü kaldırmaz.
