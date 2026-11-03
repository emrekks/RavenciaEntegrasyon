# Güvenlik Olayı Müdahale Yönergesi

Bir güvenlik olayından (sızan şifre ya da anahtar, yetkisiz giriş, sunucuya izinsiz erişim, veri sızıntısı
şüphesi) şüphelenildiğinde sırasıyla ne yapılacağı. Sunucu: Hostinger KVM 4 (`187.126.24.163`, Almanya),
repo kökü `~/RavenciaEntegrasyon`. Deploy ve geri alma ayrıntıları: [CI-CD-DEPLOY.md](CI-CD-DEPLOY.md),
[RUNBOOK-GERI-ALMA.md](RUNBOOK-GERI-ALMA.md). Plan: güvenlik planı P2.

**Altın kurallar**

1. **Önce kanıtı koru, sonra temizle.** Konteyneri silmek, sunucuyu yeniden kurmak ya da logları döndürmek
   olayın izini siler. Kanıt alınmadan (bölüm 3) hiçbir şey silinmez.
2. **Her adımı yaz.** Olay günlüğü tutun: saat (UTC ve İstanbul), kim, ne yaptı, ne gördü. KVKK bildirimi ve
   sonraki inceleme bu günlükle yapılır.
3. **Sızan bir sırrı kullanmaya devam etme.** Şüphe varsa döndür; döndürmek ucuz, sızmış anahtarla çalışmak pahalı.
4. **72 saat sayacı**, ihlalin *öğrenildiği* anda başlar (bölüm 6).

## 0. Önem derecesi

| Derece | Örnek | Hedef süre |
| --- | --- | --- |
| **S1 Kritik** | Sunucuda root/`ubuntu` oturumu açılmış, deploy anahtarı veya GitHub hesabı ele geçmiş, veritabanı dışarı aktarılmış | Hemen; ilk 1 saatte sınırlama |
| **S2 Yüksek** | Panel sahip/yönetici hesabına yetkisiz giriş, bir pazaryeri API anahtarı sızmış, `deploy/secrets/` içeriği görülmüş | 4 saat |
| **S3 Orta** | Başarısız giriş dalgası, tarama, tek bir IP'den kaba kuvvet (hesap kilitleri devrede) | 1 iş günü |

## 1. Tespit: nereye bakılır

| Kaynak | Komut / yer |
| --- | --- |
| Alarm kanalı | `send-alert.sh` bildirimleri (deploy, yedek, healthwatch, disk) |
| SSH girişleri | `journalctl -u ssh --since -24h \| grep -E 'Accepted\|Failed'`, `last -a \| head -30`, `sudo lastb \| head` |
| `sudo` kullanımı | `journalctl _COMM=sudo --since -24h` |
| Kenar erişim logu | `journalctl CONTAINER_NAME=marketplacehub-caddy-1 --since -2h` (JSON; `remote_ip`, `uri`, `status`) |
| Panel girişleri ve hatalar | `journalctl CONTAINER_NAME=marketplacehub-api-1 --since -2h \| grep -Ei 'sign.?in\|login\|mfa\|lock'` |
| Denetim kaydı | `ops.audit_logs` (aşağıdaki SQL) |
| Ban sistemi | CrowdSec kurulduktan sonra: `sudo cscli alerts list`, `sudo cscli decisions list`; şimdilik `sudo fail2ban-client status sshd` |
| GitHub | Repo → Settings → Security log / organization audit log; Actions → Deploy çalıştırmaları (beklenmeyen deploy?) |
| Hostinger / GoDaddy / Google | Hesap güvenlik sayfalarındaki giriş geçmişi |

Denetim kaydı ve oturumlar (salt okunur):

```bash
sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -c "
  select \"CreatedAt\", \"Action\", \"ActorUserId\", \"TargetType\", \"TargetId\"
  from ops.audit_logs order by \"CreatedAt\" desc limit 50;"
sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -c "
  select \"UserId\", \"State\", \"IssuedAt\", \"LastSeenAt\" from iam.user_sessions
  where \"State\" <> 'REVOKED' order by \"LastSeenAt\" desc nulls last limit 30;"
```

Şüpheli işaretler: tanımadığınız IP'den `Accepted publickey`, beklenmeyen `sudo` komutları, gece saatinde
Deploy çalıştırması, yeni eklenen kullanıcı/bağlantı/webhook, `deploy/secrets/` dosyalarının değişme zamanı
(`sudo ls -la --time-style=full-iso deploy/secrets`), bilinmeyen konteyner (`sudo docker ps -a`).

## 2. Sınırlama (containment)

Olayın türüne göre gereken satırları uygulayın; S1'de hepsi.

### 2.1 Saldırgan IP'lerini engelle

```bash
# CrowdSec kuruluysa (IPv4 ve IPv6, SSH ve 80/443):
sudo cscli decisions add --ip <IP> --duration 168h --reason "olay-<tarih>"
sudo cscli decisions add --range <CIDR> --duration 168h --reason "olay-<tarih>"
sudo cscli decisions list
# Yanlış ban kaldırma (VNC konsolundan da çalışır):
sudo cscli decisions delete --ip <IP>
# CrowdSec yoksa geçici olarak:
sudo ufw insert 1 deny from <IP>
```

Kendinizi kilitlerseniz giriş yolu Hostinger VNC konsoludur (root şifresi parola yöneticisinde).

### 2.2 Panel oturumlarını iptal et

Tek hesap için (şifreyi de sıfırlar, oturumları kapatır, kilidi kaldırır, tek kullanımlık şifre basar):

```bash
sudo docker exec -e MARKETPLACEHUB_BREAK_GLASS_AUTHORIZED=true marketplacehub-api-1 \
  dotnet api/MarketplaceHub.Api.dll identity reset-password <e-posta> "olay-<tarih>: <neden>"
# Doğrulama uygulaması da şüpheliyse (yeniden kurulum gerekir):
sudo docker exec -e MARKETPLACEHUB_BREAK_GLASS_AUTHORIZED=true marketplacehub-api-1 \
  dotnet api/MarketplaceHub.Api.dll identity reset-mfa <e-posta> "olay-<tarih>: <neden>"
```

Tüm kullanıcıların tüm oturumları (herkes yeniden giriş yapar):

```bash
sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -v ON_ERROR_STOP=1 -c "
  begin;
  update iam.user_sessions set \"State\" = 'REVOKED', \"RevokedAt\" = now() where \"State\" <> 'REVOKED';
  update iam.users set \"SessionVersion\" = \"SessionVersion\" + 1;
  commit;"
```

Panelde Kullanıcılar ekranından tanımadığınız kullanıcıyı pasifleştirin, rollerini kontrol edin.

### 2.3 Pazaryeri API anahtarlarını döndür

Pazaryeri kimlik bilgileri veritabanında Data Protection ile şifrelidir; **veritabanı dökümü ile birlikte
`dp_keys` volume'ü ve `dp_certificate.pfx` + şifresi** ele geçtiyse hepsi çözülebilir sayılır. Bu durumda:

1. Her pazaryerinin satıcı panelinde (Trendyol, Hepsiburada, N11, Pazarama, PTT AVM, Shopify, e-Faturam)
   API anahtarını/şifresini **yenileyin**; eskisi geçersiz olur.
2. Panelde Bağlantılar → ilgili bağlantı → kimlik bilgisini güncelleyin (`PUT /connections/{id}/credential`).
3. Bir sonraki senkronun hatasız geçtiğini İşlem Takibi'nde görün.

Yalnız tek bir anahtar sızdıysa yalnız onu döndürün.

### 2.4 `credential_key` değişimi

`credential_key.txt` şifreleme anahtarı **değildir**; oturum belirteçlerinin, kurtarma kodlarının, webhook
rota belirteçlerinin ve iş kiralarının HMAC anahtarıdır. Sızarsa saldırgan veritabanındaki özetlerle
eşleşen belirteç üretmeye yaklaşır; değiştirin. Sonuçları:

- Tüm panel oturumları kapanır (istenen sonuç), **kurtarma kodları geçersiz olur**: sahip yenilerini üretmeli.
- **Pazaryeri webhook adresleri geçersiz olur**: her bağlantının webhook aboneliği yeniden oluşturulmalı;
  o zamana kadar düzenli senkron siparişleri çeker, yalnız anlık bildirim gecikir.
- Çalışan işlerin kiraları düşer; worker onları kira süresi dolunca yeniden alır.

```bash
sudo cp -a deploy/secrets/credential_key.txt ~/olay-kanit/credential_key.eski   # bölüm 3'teki kanıt klasörü
openssl rand -base64 32 | tr -d '\r\n' | sudo tee deploy/secrets/credential_key.txt >/dev/null
sudo chown root:1654 deploy/secrets/credential_key.txt && sudo chmod 0640 deploy/secrets/credential_key.txt
sudo docker restart marketplacehub-api-1 marketplacehub-worker-1
```

Sonra emanet kopyasını yenileyin (RUNBOOK-GERI-ALMA.md bölüm 5).

### 2.5 Data Protection sertifikası

`dp_certificate.pfx`, `dp_keys` volume'ündeki anahtar halkasını şifreler. Uygulama **tek sertifika** okur:
sertifikayı yenisiyle değiştirmek eski halkayı açılamaz hale getirir ve tüm korunan veriler (pazaryeri
kimlik bilgileri, TOTP sırları, webhook doğrulama sırları) okunamaz olur. Bu yüzden:

- **Önerilen yol:** sertifika ile `dp_keys` birlikte sızdıysa asıl önlem 2.3'tür (anahtarları kaynağında
  yenilemek). Eski bilgiler yenilenince sızan halkanın değeri kalmaz.
- **Tam sıfırlama** (gerekirse): yeni sertifika üretin (`deploy/scripts/initialize-deployment.sh`'teki
  `openssl req … -days 1095` ve `openssl pkcs12 -export` adımları), `dp_keys` volume'ünü kanıt olarak
  yedekleyip boşaltın, API ve Worker'ı yeniden başlatın; ardından **tüm pazaryeri kimlik bilgileri yeniden
  girilir ve tüm kullanıcılar TOTP'yi yeniden kurar** (`identity reset-mfa`).
- Kesintisiz döndürme (eski sertifikayla çözüp yenisiyle şifrelemek) için kodda
  `UnprotectKeysWithAnyCertificate` desteği eklenmesi gerekir; bugün yok.

### 2.6 GitHub deploy anahtarı ve GitHub hesabı

GitHub hesabı ya da deploy anahtarı şüpheliyse sunucu da şüphelidir (GitHub `main` = sunucuda komut).

1. GitHub hesabında şifre ve 2FA'yı yenileyin, oturumları kapatın, tanımadığınız PAT / SSH anahtarı /
   OAuth uygulamasını silin.
2. Repo → Settings → Environments → `production`: deploy secret'larını (SSH özel anahtarı, known_hosts)
   yenileyin; sunucuda `~/.ssh/authorized_keys` içindeki eski deploy anahtarı satırını silip yenisini
   ekleyin (yeni anahtar `command=` kısıtlı biçimde; CI-CD-DEPLOY.md "Anahtarı yenileme / kurulum").
3. `git log origin/main` ile son commit'leri inceleyin; tanımadığınız commit varsa `main`'i
   iyi bilinen commit'e geri getiren yeni bir commit atın (force-push yok) ve deploy edin.
4. Yeniden kurulana kadar otomatik deploy'u durdurmak için Actions → Deploy iş akışını **Disable** edin.

### 2.7 Sunucu ele geçirildiyse (S1)

1. Hostinger panelinden **snapshot** alın (kanıt). Ağ erişimini Hostinger güvenlik duvarından yalnız
   kendi IP'nize 22'ye daraltın (müşteri trafiği kesilir; karar sahibin).
2. Bölüm 3'teki kanıtları alın.
3. Sunucuya güvenmeyin: yeni bir sunucu kurun, temiz kod (`main`'in iyi bilinen commit'i) ve **olay
   öncesine ait** yedek setle geri yükleyin (bölüm 4). Tüm sırlar (bölüm 2.3–2.6, Postgres şifresi,
   alarm adresi, rclone yetkisi) yenilenir; eski sunucu kapatılır.

## 3. Kanıt saklama

Kanıtlar sunucuda tek bir klasörde toplanır, sonra **şifreli** olarak sunucu dışına alınır. Kişisel veri
içerebilirler: yalnız sahip saklar, paylaşılmaz.

```bash
K=~/olay-kanit/$(date -u +%Y%m%dT%H%M%SZ); mkdir -p "$K"
date -u > "$K/zaman.txt"; uptime >> "$K/zaman.txt"
sudo journalctl --since -7d -o export | gzip > "$K/journal.export.gz"            # tüm loglar (SSH, sudo, konteynerler)
for c in api worker caddy postgres; do
  sudo docker inspect marketplacehub-$c-1 > "$K/inspect-$c.json"
done
sudo docker ps -a --no-trunc > "$K/docker-ps.txt"; sudo docker image ls --digests > "$K/docker-images.txt"
sudo ss -tupan > "$K/ss.txt"; ps auxww > "$K/ps.txt"; last -aF > "$K/last.txt"; sudo lastb -aF > "$K/lastb.txt" 2>/dev/null || true
sudo cat /home/*/.ssh/authorized_keys /root/.ssh/authorized_keys > "$K/authorized_keys.txt" 2>/dev/null || true
sudo ls -la --time-style=full-iso deploy/secrets > "$K/secrets-ls.txt"
cp ~/deploy-logs/releases.tsv "$K/" 2>/dev/null || true
git -C ~/RavenciaEntegrasyon log -20 --format='%H %ci %an %s' > "$K/git-log.txt"
sudo docker exec marketplacehub-postgres-1 pg_dump -U marketplacehub -Fc marketplacehub > "$K/database.dump"
sha256sum "$K"/* > "$K/SHA256SUMS"
# Sahip açık anahtarıyla şifreleyip sunucu dışına alın (age kurulumu: CI-CD-DEPLOY.md "Sunucu dışı yedek"):
sudo tar -C "$(dirname "$K")" -cf - "$(basename "$K")" | age -R deploy/secrets/backup_age_recipient.txt > "$K.tar.age"
```

- Hostinger snapshot'ı kanıt olarak tutulur; silinmez.
- Konteynerleri silmeyin; gerekirse `docker stop` ile durdurun (dosya sistemi incelenebilir kalır).
- Olay günlüğüne kanıtın alındığı saati ve `SHA256SUMS` özetini yazın.

## 4. Kurtarma

1. Kök neden bulunup kapatılmadan (sızan anahtar döndürüldü, açık yamalandı) servis geri açılmaz.
2. Veri bozulmadıysa: sırlar döndürülmüş haliyle mevcut sistem çalışır; 2.2–2.6'daki doğrulamalar yapılır.
3. Veri bozulduysa ya da sunucu yeniden kurulduysa: **olay öncesine ait** en yeni doğrulanmış yedek seti
   seçilir ve [RUNBOOK-GERI-ALMA.md](RUNBOOK-GERI-ALMA.md) bölüm 3 uygulanır (önce `restore-drill.sh`
   ile izole tatbikat, sonra geri yükleme, iş karantinası). Sunucudaki setler de şüpheliyse sunucu dışı
   şifreli kopya kullanılır (`<set>.tar.age`; açma: `age -d -i <özel-anahtar> <set>.tar.age | tar -x`).
4. Geri yüklemeden sonraki siparişler senkronla gelir; kesilmiş faturalar ve pazaryerine yazılan durumlar
   elle karşılaştırılır.
5. Panel girişi, webhook'lar, deploy, yedek, healthwatch ve senkronlar kontrol edilir; 72 saat yakın izleme.
6. Olay sonrası inceleme (1 hafta içinde): zaman çizelgesi, kök neden, kalıcı önlem, bu belgeye düzeltme.

## 5. İletişim

| Rol | Kişi | İletişim |
| --- | --- | --- |
| Olay sorumlusu / veri sorumlusu temsilcisi | _(doldurulacak)_ | _(telefon, e-posta)_ |
| Teknik müdahale | _(doldurulacak)_ | _(telefon, e-posta)_ |
| Hukuk / KVKK danışmanı | _(doldurulacak)_ | _(telefon, e-posta)_ |
| Hostinger destek | Hostinger panel → Destek (7/24 sohbet) | — |
| GitHub destek | support.github.com | — |
| Pazaryeri satıcı destekleri | Trendyol, Hepsiburada, N11, Pazarama, PTT AVM, Shopify | _(her birinin hattı)_ |
| Kişisel Verileri Koruma Kurumu | ihlalbildirim.kvkk.gov.tr | — |
| Dışa açık güvenlik iletişimi | `destek@ravencia.com` (`/.well-known/security.txt`) | — |

## 6. KVKK: 72 saatte ihlal bildirimi

Kişisel veri (müşteri adı, adresi, telefonu, sipariş ve fatura bilgisi, kullanıcı hesapları) yetkisiz
kişilerce elde edildiyse ya da edildiğinden makul şüphe varsa (KVKK m.12/5 ve Kurul'un 2019/10 sayılı
kararı):

1. **Saat 0**: ihlalin öğrenildiği an olay günlüğüne yazılır.
2. **72 saat içinde** Kişisel Verileri Koruma Kurulu'na bildirim: `ihlalbildirim.kvkk.gov.tr` üzerinden
   "Kişisel Veri İhlal Bildirim Formu". Bilgilerin tamamı yoksa eldekiyle süre içinde bildirilir, eksikler
   gecikmeksizin tamamlanır; gecikme varsa nedeni yazılır.
3. Formda istenenler (hazırlık listesi): ihlalin başlangıç ve tespit zamanı, nasıl tespit edildiği,
   etkilenen veri kategorileri ve yaklaşık kişi/kayıt sayısı (salt okunur sayım SQL'i ile), olası sonuçlar,
   alınan ve alınacak önlemler, veri sorumlusu ve irtibat kişisi bilgileri, yurt dışı aktarım bilgisi
   (sunucu Almanya'da, yedek/e-posta Google'da).
4. **Etkilenen kişilere** makul en kısa sürede, doğrudan (e-posta/SMS) mümkün değilse uygun yolla (web
   sitesi) bildirim: ne oldu, hangi veriler, ne yapıldı, kişinin ne yapması gerektiği, iletişim adresi.
5. Pazaryerlerinin sözleşmelerindeki bildirim yükümlülükleri ayrıca kontrol edilir (müşteri verisi
   pazaryerinden geliyor).
6. Tüm ihlaller (bildirilmeyenler dahil) ihlal kayıt defterine yazılır ve Kurul istediğinde sunulur.

Kişisel veri etkilenmediyse (ör. yalnız bir tarama, başarısız deneme) bildirim gerekmez; değerlendirme ve
gerekçe yine olay günlüğüne yazılır.

## 7. Kontrol listesi (kısa)

- [ ] Olay günlüğü başladı, saat 0 yazıldı
- [ ] Snapshot ve kanıt klasörü alındı, şifreli olarak sunucu dışında
- [ ] Saldırgan IP'leri engellendi
- [ ] Panel oturumları iptal edildi, şüpheli hesaplar pasif
- [ ] Sızan sırlar döndürüldü (pazaryeri anahtarları, `credential_key`, deploy anahtarı, GitHub)
- [ ] Kök neden kapatıldı
- [ ] Gerekirse yedekten geri yüklendi, karantina işleri incelendi
- [ ] KVKK değerlendirmesi yapıldı; gerekiyorsa 72 saat içinde bildirildi
- [ ] Emanet kopyaları yenilendi (RUNBOOK-GERI-ALMA.md bölüm 5)
- [ ] Olay sonrası inceleme yazıldı
