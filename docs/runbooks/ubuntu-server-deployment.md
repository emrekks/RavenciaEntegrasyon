# Ubuntu Server Kurulum ve Taşıma Runbook'u

Bu akış modüler monolit, Docker Compose, PostgreSQL, API/Worker ve Caddy sınırlarını değiştirmez. GitHub'da üretilen aynı immutable Linux/amd64 imajlarını v3.4 hedefi Ubuntu Server 26.04 LTS üzerinde doğrudan Docker Engine ile çalıştırır.

## Sunucu önkoşulları

- Ubuntu Server 26.04 LTS x86_64, 2 vCPU, 8 GB sınıfı RAM ve 80 GB NVMe sınıfı disk.
- Statik public IPv4; domain A/AAAA kaydı hedef sunucuya yönelmiş.
- SSH yalnız yönetici IP allow-list veya VPN üzerinden anahtar tabanlı.
- Docker Engine/CLI kurulu ve systemd üzerinde enabled/active; daemon komutları `sudo` ile yürütülür. `ubuntu` hesabı root-eşdeğeri `docker` grubuna eklenmez.
- GHCR package private ise read-only package token ile registry oturumu açık.
- Yalnız 80/443 public uygulama portu; API/Worker/PostgreSQL host portu yok. API ve Worker dış platform çağrıları için ayrı outbound `egress` ağına bağlanır; PostgreSQL ve backup yalnız `internal` backend ağında kalır.

Docker Engine kurulumu hedefte resmî Docker Ubuntu repository yöntemiyle yapılır. Convenience script production için kullanılmaz. Exact Docker Engine/CLI paketi hedef doğrulama kaydına yazılmadan production kabul edilmez.

## En kolay kurulum

Repository'yi sunucuya clone et, onaylı commit'e geç ve kökten çalıştır:

```bash
chmod +x deploy/scripts/*.sh
sudo -H ./deploy/scripts/install-marketplacehub.sh --host-only
```

Hazırlık sihirbazı:

- Ubuntu 26.04, x86_64, en az 2 vCPU, 8 GB sınıfı RAM ve en az 70.000.000.000 byte kullanılabilir root filesystem kapasitesini doğrular.
- Linux/amd64 Docker Engine erişimini doğrular.
- Exact Compose v2.40.2 eksikse resmî binary'yi kullanıcı plugin dizinine indirip sabit SHA-256 ile doğrular.
- Uygulama/edge digest'i, HTTPS panel adresi ve ilk Owner e-postasını sorar.
- PostgreSQL parolası, credential key, geçici Owner parolası ve Data Protection PFX'i ayrı üretir.
- Secret içeriklerini yazdırmadan production Compose çözümlemesini doğrular.

İlk çalıştırma servisleri değiştirmez. Doğrulama geçince ilk boş kurulumu çalıştır:

```bash
sudo -H ./deploy/scripts/install-marketplacehub.sh --host-only
sudo -H ./deploy/scripts/install-marketplacehub.sh --deploy --bootstrap
```

Sonraki release'lerde `deploy/secrets/production.env` içindeki iki digest'i onaylı değerlerle değiştir ve yalnız `--deploy` kullan. Bootstrap tekrarı varsayılan değildir.

Immutable release imajları GitHub Actions ekranından elle veya yalnız onaylı commit üzerinde `release-*` etiketi gönderilerek üretilebilir. Normal branch push'ları imaj yayımlamaz. İş akışı özetindeki iki `name@sha256:...` değeri değişmeden production kaydına alınır.

## Secret ve backup zorunluluğu

`deploy/secrets/` Git tarafından ignore edilir ve dizin `0700` hazırlanır. Host-only deployment kayıtları `0600` kalır; yalnız root olmayan, pinli ASP.NET runtime GID `1654` tarafından okunması gereken uygulama secret'ları `root:1654 / 0640` olur. Bu grup host üzerinde oturum yetkisi taşımaz. Otomatik üretilen `dp_certificate.pfx`, `dp_certificate_password.txt` ve `dp_certificate_metadata.txt` dosyalarını kurulumdan hemen sonra repository dışında şifreli, erişim kontrollü off-host secret hedefine kopyala.

Mevcut veri taşınacaksa:

1. Kaynakta `database.dump`, `private-volumes.tar.gz`, `SHA256SUMS` ve `manifest.json` üret.
2. Seti şifreli kanalla Ubuntu hedefe aktar ve bütünlüğü doğrula.
3. Boş, izole PostgreSQL/volume setine restore et; mevcut production volume üzerine açma.
4. DB, private files ve Data Protection keys birlikte doğrulanmadan DNS trafiğini açma.
5. Readiness, kontrollü login, backup ve ölçülmüş restore kanıtından sonra Go/No-Go kaydı oluştur.

## Rollback ve yasaklar

- Migration uyumluysa önceki onaylı app/edge digest çiftine dön.
- Şema uyumsuzsa write kapalı tutulur ve onaylı backup yeni boş hedefe restore edilir.
- `down -v`, prune, floating tag, `latest`, public DB/API portu veya secret overwrite yasaktır.
- Bu dağıtım değişikliği F6B/F6C/F7 fazlarını veya dış platform write capability'lerini açmaz.
