# Manuel Proje İlerletme ve Teslim Modeli

**Yürürlük:** 18 Ağustos 2026
**Kapsam:** Yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM`

## Çalışma kuralı

Proje, ürün sahibinin panelden ilettiği manuel önceliklerle ilerler:

1. Ürün sahibi panelde gördüğü düzenlemeyi Codex'e iletir.
2. Codex gerekli dosya değişikliğini doğrudan uygular; yalnız riskin gerektirdiği en dar kontrolü kullanır.
3. Codex değişikliği commit eder ve `main` dalına push eder.
4. Talep sunucu güncellemesini içeriyorsa Codex mevcut sunucu/deployment bilgileriyle güncellemeyi uygular.
5. Codex yapılan değişikliği, commit/push ve varsa sunucu sonucunu kısa biçimde bildirir.

Faz kanıtı, evidence log, dokümantasyon transaction, zorunlu tam test, Stage kabulü, immutable release ve CI sonucu ürün sahibi yeniden açana kadar günlük işin teslim kapısı değildir. Bu kayıtlar istenirse ayrıca üretilebilir; eksiklikleri düzenlemeyi veya commit/push işlemini bekletmez.

## GitHub Actions gerekli mi?

GitHub Actions günlük geliştirme, commit, push veya manuel sunucu güncellemesi için zorunlu değildir. Mevcut workflow dosyaları isteğe bağlı doğrulama ve ileride yeniden açılabilecek release otomasyonu olarak repository'de korunur. Bir Actions koşusunun beklenmesi veya başarılı olması günlük teslim şartı değildir.

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
| GitHub Actions | İsteğe bağlı source CI ve immutable release publish | Askıda; günlük teslim kapısı değil |
| GHCR | İsteğe bağlı digest-pinned app/edge image saklama | Manuel deployment yöntemine göre kullanılır |
| Ubuntu + Docker Compose | API, Worker, PostgreSQL ve edge çalıştırma | Zorunlu |
| PostgreSQL volume | Uygulama ve durable job verisi | Zorunlu; deploy öncesi backup |
| Caddy/DNS/TLS | Panel HTTPS erişimi | Zorunlu; deploy sonrası readiness |
| Trendyol Stage | Ürün, iade, sipariş ve etiket Stage kabulü | Aktif, dış kabul işleri açık |
| E-Faturam Stage API_USER | Mali belge işlemleri | Giriş doğrulandı; protected create yetkisi açık |
| Şifreli credential/secrets | Provider kimlik bilgileri | Zorunlu; secret loglanmaz |
| Backup + restore doğrulaması | Geri dönüş ve veri güvenliği | Veri/migration etkili sunucu işlemlerinde korunur |

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
