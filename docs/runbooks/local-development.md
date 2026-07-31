# F1 Yerel Geliştirme Runbook'u

## Secret hazırlığı

`deploy/secrets/` Git tarafından ignore edilir. Aşağıdaki dosyaların her birini kriptografik rastgele ve birbirinden bağımsız değerlerle oluştur:

- `postgres_password.txt`: PostgreSQL parolası.
- `app_db_connection.txt`: aynı parolayı kullanan tam Npgsql connection string; host `postgres`, port `5432`.
- `credential_key.txt`: tam 32 rastgele byte'ın Base64 gösterimi.
- `bootstrap_owner_password.txt`: 15–64 karakter, Identity politikasını karşılayan geçici Owner parolası.

Secret içeriğini terminale veya log'a basma. `pgpass.txt` kullanılmaz. PILOT_LOCAL dışında ayrıca `dp_certificate.pfx` ve `dp_certificate_password.txt` gerekir.

## Temiz başlatma ve doğrulama

```powershell
$compose = "$env:LOCALAPPDATA\Ravencia\tools\docker-compose-v2.40.2.exe"
& $compose -f deploy/compose/compose.yaml config --quiet
& $compose -f deploy/compose/compose.yaml build api caddy
& $compose -f deploy/compose/compose.yaml up -d postgres migrate api worker caddy
& $compose -f deploy/compose/compose.yaml ps
curl.exe -k https://localhost/health/ready
```

Beklenen: `migrate` exit code `0`; PostgreSQL/API/Caddy healthy; Worker running; HTTPS readiness `200`. `docker compose down -v` yasaktır. Normal durdurma `stop`, kaldırma ise volume silmeden `down` ile yapılır.

## Initial bootstrap

Migration hiçbir kullanıcı veya parola seed etmez. İlk Owner bootstrap yalnız bir operatör kararıyla ve owner e-postası environment'ta açıkça verilerek çalıştırılır:

```powershell
$env:MARKETPLACEHUB_BOOTSTRAP_OWNER_EMAIL = '<approved-owner-email>'
& $compose -f deploy/compose/compose.yaml run --rm -e Bootstrap__Enabled=true migrate api/MarketplaceHub.Api.dll bootstrap
```

Komut advisory lock alır; Tenant, Owner, OWNER membership, UserSecurity, kapalı `external-writes` flag'i ve fingerprint marker'ını tek transaction'da oluşturur. Aynı yapılandırmayla tekrar no-op; farklı fingerprint fail-closed'dur. İlk oturumun wire durumu `PASSWORD_CHANGE_REQUIRED` olur ve business/tenant claim'i alamaz.

## Yerel sınır

PILOT_LOCAL self-signed Caddy CA ve dosya izinleriyle kalıcı Data Protection volume'u kullanabilir. Production, PFX certificate ile key-at-rest encryption yoksa başlangıçta fail-closed olur. Yerel sonuç hedef VPS kabulü değildir.

## Docker olmadan Windows localhost

Docker Desktop bulunmayan geliştirme bilgisayarında PostgreSQL 18'in ayrı bir local cluster'ı kullanılabilir. Mevcut Windows PostgreSQL service/data dizinine müdahale etme; proje cluster'ını ignore edilen `tmp/local-panel/pgdata` altında farklı bir portta çalıştır. Secret dosyaları repository dışında `%LOCALAPPDATA%\Ravencia\local-panel-secrets` altında tutulur.

API için `MARKETPLACEHUB_ENVIRONMENT=PILOT_LOCAL`, file-backed credential key, ayrı DB connection, Data Protection ve private-file köklerini ayarla; migration ve explicit Owner bootstrap komutlarını çalıştır. API'yi yalnız `http://127.0.0.1:5080`, Vite'ı `VITE_API_PROXY=http://127.0.0.1:5080 npm.cmd run dev -- --host 127.0.0.1 --port 5173` ile başlat. Panel `http://localhost:5173` adresindedir.

PILOT_LOCAL HTTP'de session/CSRF cookie geliştirme amacıyla Secure olmadan yazılabilir; başka her environment'ta Secure zorunludur. Bu çalışma biçimi VPS/production TLS, container, reboot, volume, backup veya restore kanıtı değildir.
