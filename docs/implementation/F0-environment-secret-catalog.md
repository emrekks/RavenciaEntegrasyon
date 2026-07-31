# F0 Environment ve Secret Kataloğu

## İlkeler

- Aşağıdaki adlar ve varsayılanlar yetkili şartnamenin Tablo 42 kaydıdır; adapter credential adları resmî auth sözleşmesi doğrulanana kadar uydurulmaz.
- Secret değerleri repository, image, log, fixture veya bu belgeye yazılmaz.
- `_FILE` değişkenleri read-only, repository dışı secret dosyasına işaret eder; secret dosyasının içeriği ve hassas yolu loglanmaz.
- `.env.example` yalnız adları ve güvenli development örneklerini taşır. Production, development secret algılarsa fail-fast olur.

## Uygulama ve altyapı değişkenleri

| Değişken | Amaç | Sınıf | Şartname varsayılanı / kuralı |
| --- | --- | --- | --- |
| `APP_ENVIRONMENT` | Development/Test/Staging/Production | Config | Production açık yazılır. |
| `APP_BASE_URL` | Canonical dış URL | Config | Local: `http://localhost` |
| `APP_DISPLAY_NAME` | Panel ürün adı | Config | `Ravencia Entegrasyon` |
| `APP_TIME_ZONE` | Gösterim timezone | Config | `Europe/Istanbul` |
| `ASPNETCORE_URLS` | Container listen | Config | `http://+:8080` |
| `DB_HOST` / `DB_PORT` | PostgreSQL network adresi | Config | `postgres` / `5432` |
| `DB_NAME` / `DB_USER` | Uygulama DB/rol | Config | `marketplacehub` / app role |
| `DB_PASSWORD_FILE` | DB parola dosyası | SECRET | Zorunlu; repository dışında. |
| `DB_COMMAND_TIMEOUT_SECONDS` | DB command timeout | Config | `30`; job türü ayrıca ayarlanabilir. |
| `DATA_PROTECTION_KEYS_PATH` | Kalıcı key ring | Sensitive path | `/app/keys` |
| `DATA_PROTECTION_CERT_PATH` | Key ring koruma sertifikası | Sensitive | Production zorunlu. |
| `DATA_PROTECTION_CERT_PASSWORD_FILE` | Sertifika parola dosyası | SECRET | Production zorunlu. |
| `BOOTSTRAP_ENABLED` | İlk Owner bootstrap | Config | `false`; yalnız one-shot ilk kurulumda `true`, sonra operator `false` yapar. |
| `BOOTSTRAP_OWNER_USERNAME` | İlk Owner kullanıcı adı | Config | `RavenciaAdmin`; source/migration/image/Compose sabiti değildir. |
| `BOOTSTRAP_OWNER_PASSWORD_FILE` | Tek kullanımlık başlangıç parolası | SECRET | Read-only; repository dışında; kurulumdan sonra kaldırılır. |
| `BOOTSTRAP_FORCE_PASSWORD_CHANGE` | İlk oturumu kısıtlama | Config | `true`; başarılı değişimden önce business erişimi yok. |
| `MFA_DEFAULT_ENABLED` | İlk Owner TOTP durumu | Config | `false`; kullanıcı tercihiyle açılır. |
| `BOOTSTRAP_ADMIN_CIDRS` | Sabit pilot parolası için kurulum ağı | Sensitive | Varsayılan yalnız loopback; public ingress yok. |
| `FILES_ROOT` | Private dosya kökü | Config | `/app/data/files` |
| `MAX_UPLOAD_BYTES` | Genel upload üst sınırı | Config | `10 MiB`; endpoint override olabilir. |
| `JOB_WORKER_ID` | Lease owner kimliği | Config | Container/instance benzersiz. |
| `JOB_POLL_INTERVAL_MS` | Boş queue polling | Config | `1000` |
| `JOB_BATCH_SIZE` | Tek claim adedi | Config | `10` |
| `JOB_DEFAULT_LEASE_SECONDS` | Varsayılan lease | Config | `120` |
| `JOB_HEARTBEAT_SECONDS` | Heartbeat aralığı | Config | `30` |
| `LOG_LEVEL` | Minimum log seviyesi | Config | `Information` |
| `CORRELATION_HEADER` | İstek correlation header | Config | `X-Correlation-ID` |
| `EXTERNAL_WRITES_ENABLED` | Global dış yazma kill switch | Config | `false`; production açılışı onaylı. |
| `AUTO_INVOICE_ENABLED` | Otomatik fatura | Config | `false` |
| `PLATFORM_{CODE}_WRITES_ENABLED` | Platform kill switch | Config | Başlangıç `false`. `{CODE}` yalnız bağlayıcı platform kaydıyla somutlaştırılır. |
| `BACKUP_SCHEDULE` | DB backup cron/schedule | Config | 6 saatte bir. |
| `BACKUP_RETENTION_6H_DAYS` | 6 saatlik PostgreSQL setleri | Config | Son 7 gün. |
| `BACKUP_RETENTION_WEEKLY` | Son başarılı haftalık set | Config | 4 adet. |
| `BACKUP_RETENTION_MONTHLY` | Son başarılı aylık set | Config | 3 adet. |
| `BACKUP_RETENTION_PRERELEASE` | Release öncesi set | Config | Bir sonraki başarılı release tamamlanana kadar. |
| `BACKUP_PROFILE` | `PILOT_LOCAL` / `PRODUCTION_RESILIENT` | Config | `PILOT_LOCAL`; risk kaydı zorunlu. |
| `RESTIC_REPOSITORY` | Off-host repo URL | Sensitive | Yalnız `PRODUCTION_RESILIENT` profilde zorunlu. |
| `RESTIC_PASSWORD_FILE` | Restic repo parolası | SECRET | Resilient profilde; backup setinden ayrı. |
| `S3_ACCESS_KEY_ID_FILE` | Off-host erişim anahtarı | SECRET | Resilient profilde; minimum yetki. |
| `S3_SECRET_ACCESS_KEY_FILE` | Off-host secret | SECRET | Resilient profilde; minimum yetki. |
| `CADDY_DOMAIN` | TLS domain | Config | Localhost; production gerçek domain. |
| `ACME_EMAIL` | Sertifika bildirim e-postası | Personal config | Production zorunlu; log redaction uygulanır. |

## Henüz adlandırılmayan secret'lar

Platform credential, webhook secret ve mağaza/merchant scope alanlarının environment adları F0'da `UNKNOWN`dur. İlgili adapter fazında güncel resmî platform kaynağı ve test hesabıyla doğrulanmadan değişken adı veya auth alanı oluşturulmaz.

## F3 Trendyol somutlaştırması

- Trendyol seller/store kimliği connection kaydıdır; secret environment değişkeni değildir.
- API key + API secret, UI/API üzerinden yalnız write-only alınır ve Data Protection purpose `MarketplaceHub.PlatformCredential.v1` ile şifreli `integration.platform_credentials` kaydında tutulur. API/log/fixture geri göstermez; yalnız son dört karakter maskesi saklanır.
- Webhook `API_KEY` veya `BASIC_AUTHENTICATION` verifier değeri Data Protection purpose `MarketplaceHub.WebhookVerifier.v1` ile şifreli tutulur. Opaque route token'ın yalnız HMAC-SHA256 özeti saklanır ve açık token yalnız oluşturma yanıtında bir kez döner.
- Trendyol base URL'leri resmî production/stage sabitleridir. Credential environment'a bağlıdır; production/stage credential birbirinin yerine kullanılmaz.
- Global `FeatureFlags__ExternalWrites=false` korunur. Connection içi `ExternalWritesEnabled=false` ikinci kill switch'tir ve F3'te bunu açan kullanıcı API'si yoktur.

## F4 E-Faturam somutlaştırması

- Provider firma/partner kapsamı connection kaydıdır; secret environment değişkeni değildir.
- Resmî sign-in sözleşmesindeki e-posta + parola, UI/API üzerinden write-only alınır ve aynı Data Protection purpose ile şifreli `EMAIL_PASSWORD` credential kaydında tutulur. E-posta yalnız maskeli hint olarak kalır; parola, access token ve refresh token API/log/fixture'da gösterilmez.
- API `1.0.0`, environment ve connection scope değişirse bütün F4 capability kanıtları `UNKNOWN`a döner.
- `AUTO_INVOICE_ENABLED=false`, global external-writes, connection external-writes ve capability kapıları birlikte uygulanır. F4'te auto/write anahtarlarını açan kullanıcı API'si yoktur.
- Legal entity tax identity ve invoice party snapshot'ları ayrı Data Protection purpose'larıyla korunur; API yalnız maskeli tax identity döndürür. Invoice XML/PDF private FileAsset sınırındadır.
