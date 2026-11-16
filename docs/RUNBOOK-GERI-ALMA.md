# Geri Alma Runbook'u

Bir deploy sonrası panel bozulduğunda ne yapılacağı. Komutlar sunucuda, `~/RavenciaEntegrasyon`
kökünden çalıştırılır. Genel deploy akışı: [CI-CD-DEPLOY.md](CI-CD-DEPLOY.md). Güvenlik olayı
(sızıntı, yetkisiz erişim) şüphesinde önce [RUNBOOK-OLAY.md](RUNBOOK-OLAY.md).

## 0. Önce karar: yalnız kod mu, veritabanı da mı?

| Belirti | Yapılacak |
| --- | --- |
| Yeni sürümde hata var, veriler doğru (ekran çöküyor, bir uç 500 veriyor, iş kuyruğu takılıyor) | **Yalnız kodu geri al** (bölüm 2). Veri kaybı yok. |
| Hata küçük ve nedeni belli | Geri almak yerine **ileri düzelt**: düzeltmeyi `main`'e push et; Validate → Deploy olağan yoldan gider. |
| Migration yarıda kaldı / deploy "Database migration did not complete" ile durdu | Eski konteynerler hâlâ çalışıyor (deploy migration bitmeden onları değiştirmez). Panel çalışıyorsa acil iş yok: migration logunu incele, ileri düzelt. |
| Yeni migration veriyi **bozdu ya da sildi** (yanlış UPDATE/DELETE, kolon düşürüldü, veri dönüşümü hatalı) | **Veritabanını geri yükle** (bölüm 3). Yedekten sonraki tüm yazmalar kaybolur — son çare. |
| Eski kod yeni şemayla çalışamıyor (kolon adı değişti, NOT NULL kolon eklendi) | Kod geri alma tek başına yetmez: ya ileri düzelt ya da bölüm 3. |

Kural: **veri doğruysa veritabanına dokunma.** DB geri yüklemesi; yedekten sonra gelen siparişleri,
kesilen faturaları, webhook'ları ve pazaryerine yapılan yazmaların izini siler. Pazaryeri tarafındaki
değişiklikler (gönderilen fatura, onaylanan paket) geri alınmaz; DB onları "yapılmamış" sanır.

## 1. Durumu gör

```bash
tail -n 5 ~/deploy-logs/releases.tsv          # zaman, tür, SHA, imajlar
cat ~/deploy-logs/.last-success               # son doğrulanmış deploy
ls -t ~/deploy-logs/deploy-*.log | head -3    # son deploy logları
sudo docker image ls 'marketplacehub-*'       # mevcut ve N-1 manual-* imajları
journalctl CONTAINER_NAME=marketplacehub-api-1 --since -30min
journalctl CONTAINER_NAME=marketplacehub-worker-1 --since -30min
curl --resolve panel.ravencia.com:443:127.0.0.1 https://panel.ravencia.com/health/ready
curl --resolve panel.ravencia.com:443:127.0.0.1 https://panel.ravencia.com/health/queues
```

## 2. Yalnız kodu geri al (`rollback.sh`)

```bash
bash deploy/scripts/rollback.sh <önceki-sürümün-SHA'sı>     # 12+ karakter, ör. releases.tsv'den
```

Betik:

1. SHA'nın checkout edilmiş `main` geçmişinde olduğunu doğrular.
2. O sürümden bu yana `src/MarketplaceHub.Infrastructure/Persistence/Migrations` değiştiyse **durur**.
   Yeni migration'lar yalnızca ekleyici ise (yeni tablo, nullable kolon, index) eski kod sorunsuz
   çalışır → `--allow-schema-drift` ile tekrar çalıştır. Kolon silme/yeniden adlandırma, NOT NULL
   ekleme gibi değişikliklerde eski kod hata verir → ileri düzelt ya da bölüm 3.
3. `marketplacehub-{app,edge}:manual-<sha12>` imajlarını kullanır. Sunucuda yalnızca son sürüm ve bir
   öncekisi tutulur; daha eskisi için `--build` (o commit'in `git archive` çıktısından derler,
   checkout'a dokunmaz; 2 vCPU'da ~10 dk).
4. Taze, doğrulanmış bir yedek alır (veritabanı kapalıysa `--skip-backup`).
5. `api`, `worker`, `caddy`'yi eski imajla yeniden yaratır — **migration çalıştırmaz** (`--no-deps`).
6. Readiness, Worker sağlığı ve ön yüzü doğrular; `releases.tsv`'ye `rollback` satırı yazar,
   `.last-success`'i geri alınan SHA'ya çeker ve önceki HEAD'i `~/deploy-logs/.hold`'a yazar.

**`.hold` neden önemli**: Zamanlanmış sabah deploy'u 27 Ekim 2026'da kaldırıldı, ama elle başlatılan Deploy son
yeşil SHA'yı yeniden dener ve `main`'e gelen her push deploy edilir. `.hold`
dosyası, geri alınan sürümün (ve atalarının) otomatik olarak geri gelmesini engeller. `main`'e yeni bir
commit (düzeltme) geldiğinde o commit normal şekilde deploy olur ve `.hold` kalkar. Bozuk sürümü bilerek
yeniden açmak için: `rm ~/deploy-logs/.hold` ve Actions → Deploy → Run workflow (`redeploy` işaretli).

**Otomatik geri alma**: `fast-deploy.sh` yeni sürüm ayağa kalkmazsa ya da `verify_stack` (readiness,
Worker sağlığı, ön yüz) başarısız olursa, yukarıdaki betiği kendisi `rollback.sh <önceki-sha12>
--skip-backup` ile çalıştırır — yalnız şu durumlarda: önceki sürüm sunucuda tutulan bir
`manual-<sha12>` app+edge çiftidir, aradaki commit'lerde migration klasörü **değişmemiştir** ve deploy
`production.env`'in yazma ayarlarıyla çalışmıştır. Yedek, migration'dan dakikalar önce alındığı ve şema
değişmediği için atlanır. Sonuç `send-alert.sh` ile bildirilir; deploy her durumda başarısız (çıkış 1)
sayılır ve geri alınan SHA `.hold`'a yazılır. Şema değiştiyse otomatik geri alma yapılmaz, alarm gider;
karar bu belgeye göre elle verilir. Kapatmak için: `RAVENCIA_AUTO_ROLLBACK=false`.

Not: Çalışma ağacı (git checkout) geri alınan sürümde kalmaz, son çekilen commit'te kalır; bu
bilinçlidir — sonraki düzeltme commit'i fast-forward ile gelir.

## 3. Veritabanını geri yükle (son çare)

Yedekler `marketplacehub_backup_staging` volume'ündedir (`<UTC zaman damgası>/database.dump`,
`private-volumes.tar.gz`, `SHA256SUMS`). Her deploy ve geri alma öncesinde taze set alınır; saklama:
son 72 saatin en yeni 5 seti + son 7 günün her yarım gününün en yeni seti + daha eski 4 haftanın ve 6 ayın
her birinin en yeni seti; en yeni set her durumda kalır (`deploy/backup/backup.sh`, `BACKUP_KEEP_*`). Disk
%85'i geçecekse en eski haftalık setler önce
silinir — yani bir haftadan eski bir noktaya dönüş her zaman garanti değildir.

1. **Hangi set?** Bozan deploy'un hemen öncesindeki set: `releases.tsv`'deki deploy zamanından hemen
   önceki zaman damgası (deploy logunda "Backup set created at /backup/<set>" satırı).
   ```bash
   sudo docker run --rm -v marketplacehub_backup_staging:/b:ro alpine ls -1 /b
   ```
2. **Önce tatbikat**: seti izole ortamda dene (üretime dokunmaz):
   ```bash
   sudo bash deploy/backup/restore-drill.sh <set> "$(sudo docker inspect --format '{{.Image}}' marketplacehub-api-1)" deploy/secrets
   ```
3. **Yazmaları durdur** ve kodu da setle uyumlu sürüme al (eski şemayı bekleyen kod):
   ```bash
   bash deploy/scripts/rollback.sh <setle-uyumlu-SHA> --allow-schema-drift --skip-backup
   sudo docker stop marketplacehub-worker-1 marketplacehub-api-1
   ```
4. **Mevcut durumun yedeğini al** (geri dönüş için; adli inceleme için de gerekir):
   ```bash
   sudo docker exec marketplacehub-postgres-1 sh -c 'pg_dump -U marketplacehub -Fc marketplacehub' > ~/pre-restore-$(date -u +%Y%m%dT%H%M%SZ).dump
   ```
5. **Geri yükle** (veritabanını bırakıp yeniden oluşturur):
   ```bash
   SET=<set>
   sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d postgres -v ON_ERROR_STOP=1 \
     -c "select pg_terminate_backend(pid) from pg_stat_activity where datname='marketplacehub' and pid<>pg_backend_pid();" \
     -c "drop database marketplacehub;" -c "create database marketplacehub owner marketplacehub;"
   sudo docker run --rm --network marketplacehub_backend -v marketplacehub_backup_staging:/backup:ro \
     -e PGHOST=postgres -e PGUSER=marketplacehub -e PGDATABASE=marketplacehub \
     -e PGPASSWORD="$(sudo cat deploy/secrets/postgres_password.txt)" \
     postgres:18.6@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336 \
     pg_restore --exit-on-error --no-owner --dbname=marketplacehub "/backup/$SET/database.dump"
   ```
   Dosya/anahtar volume'leri (`app_files`, `dp_keys`) gerekiyorsa `private-volumes.tar.gz`'den aynı
   şekilde açılır; Data Protection anahtarları değişmediyse dokunmayın.
   Uygulama rollerine geçildiyse (bölüm 7) `--no-owner` geri yükleme nesneleri yine `marketplacehub`'a yazar:
   API'yi başlatmadan önce `bash deploy/scripts/install-app-db-roles.sh --apply` sahipliği ve yetkileri yeniden kurar
   (parolalar `deploy/secrets/` içindeki dosyalardan korunur).
6. **Bekleyen işleri karantinaya al** (worker'ı başlatmadan önce, zorunlu). Yedek alındığı anda kuyrukta
   bekleyen ya da çalışan işler arasında fatura gönderimi, kargo/iade aksiyonu ve fiyat-stok yazımı
   olabilir. Bunlar yedekten sonra zaten gönderilmiş olabilir; worker onları yeniden çalıştırırsa
   çift fatura veya pazaryerine eski stok yazılır. Hepsini manuel incelemeye çek:
   ```bash
   sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -v ON_ERROR_STOP=1 -c "
     update integration.jobs set \"Status\" = 'MANUAL_REVIEW', \"LastErrorCode\" = 'RESTORE_QUARANTINE',
       \"LeaseTokenHash\" = null, \"LeaseExpiresAt\" = null, \"HeartbeatAt\" = null
     where \"Status\" in ('PENDING', 'LEASED', 'RETRY_SCHEDULED', 'BLOCKED');"
   ```
   Zamanlanmış okumalar (sipariş, iade, soru) worker açılınca kendiliğinden yeniden kuyruğa girer;
   karantina onları etkilemez. Karantinadaki yazma işleri İşlem Takibi'nde `RESTORE_QUARANTINE` koduyla
   görünür: her birini pazaryeri ve fatura sağlayıcısı panelinde kontrol et, yalnız **gerçekten
   gönderilmemiş** olanları yeniden çalıştır.
7. **Başlat ve doğrula**: `sudo docker start marketplacehub-api-1 marketplacehub-worker-1`, sonra bölüm 1'deki
   kontroller. Yedek ile şimdi arasındaki pazaryeri siparişleri sonraki senkronda yeniden çekilir;
   kesilmiş faturalar ve pazaryerine gönderilmiş durumlar **elle** karşılaştırılmalıdır.

## 4. Sağlıksız API (elle müdahale)

`ravencia-healthwatch` iki ardışık kontrolde `unhealthy` kalan API'yi (PostgreSQL sağlıklıysa)
kendisi yeniden başlatır ve alarm gönderir (en çok 30 dakikada bir). Döngüye girerse:

```bash
sudo docker inspect --format '{{json .State.Health}}' marketplacehub-api-1 | jq '.Log[-3:]'
journalctl CONTAINER_NAME=marketplacehub-api-1 --since -15min
sudo docker inspect --format '{{.State.Health.Status}}' marketplacehub-postgres-1
sudo docker restart marketplacehub-api-1      # elle
journalctl -u ravencia-healthwatch --since -1h
```

PostgreSQL de sağlıksızsa önce onu çöz (disk dolu mu: `df -h /`, bağlantı sayısı:
`sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -c "select count(*) from pg_stat_activity"`).

## 5. Gizli bilgilerin emaneti (yedekler bunlarsız açılmaz)

Yedek setleri veritabanını ve volume'leri içerir ama **`deploy/secrets/` içermez**. Sunucu kaybolursa
aşağıdakiler olmadan pazaryeri kimlik bilgileri ve Data Protection ile korunan veriler çözülemez:

| Dosya | Neden |
| --- | --- |
| `credential_key.txt` | Oturum, kurtarma kodu ve webhook rota belirteçlerinin HMAC anahtarı (kaybolursa herkes yeniden giriş yapar, kurtarma kodları ve webhook adresleri yenilenir) |
| `dp_certificate.pfx`, `dp_certificate_password.txt`, `dp_certificate_metadata.txt` | Data Protection anahtar halkasının sertifikası; pazaryeri kimlik bilgileri ve TOTP sırları bu halkayla şifreli, sertifika olmadan **çözülemez** |
| `postgres_password.txt`, `app_db_connection.txt` | Veritabanı erişimi (yeniden üretilebilir ama dump sahipliğiyle uyum için saklayın) |
| `production.env`, `alert_smtp.txt`, `alert_webhook_url.txt` | Yapılandırma |

Emanet kuralı (dosyaları **bu repoya, sohbete ya da şifresiz bir yere koymayın**):

1. Sahip, kendi bilgisayarında `age` anahtar çifti üretir; özel anahtar parola yöneticisinde ve sunucu
   dışında kalır, sunucuya yalnız açık anahtar (`deploy/secrets/backup_age_recipient.txt`) konur
   ([CI-CD-DEPLOY.md](CI-CD-DEPLOY.md) "Sunucu dışı yedek").
2. Sahibin bilgisayarında (sunucuda `age` ve `backup_age_recipient.txt` kurulu olmasa da çalışır; arşiv SSH içinde
   gelir, diske yalnız şifreli hali yazılır). `age1…` açık anahtarı panelde Sistem Ayarları › Google Drive Yedeği'nde
   kayıtlı olandır (özel anahtarı `ravencia-yedek-anahtari.txt`):
   ```bash
   ssh ubuntu@187.126.24.163 'cd <deploy checkout> && sudo tar -C deploy -czf - secrets' \
     | age -r age1... > ravencia-secrets-$(date +%F).tar.gz.age
   age -d -i ravencia-yedek-anahtari.txt ravencia-secrets-*.tar.gz.age | tar -tzf - | head   # açılıyor mu
   ```
   Sunucuda `deploy/secrets/backup_age_recipient.txt` kuruluysa eski yol da olur:
   `sudo tar -C deploy -czf - secrets | age -R deploy/secrets/backup_age_recipient.txt > ~/ravencia-secrets-$(date +%F).tar.gz.age`.
3. Şifreli dosyayı parola yöneticisine ve Drive'a koy (sunucuda yazdıysan `scp` ile al ve sunucudan `rm`).
4. Her anahtar/sertifika değişiminde tekrarla; yılda bir kez açıp `restore-drill.sh` ile dene.

**Otomatik emanet (VERITABANI-DAGITIM-9, sahibin tek adımı):** `deploy/backup/secrets-escrow.sh` dört dosyayı
(`credential_key.txt`, `dp_certificate.pfx`, `dp_certificate_password.txt`, `production.env`) panelin Google Drive
yedeğindeki age **açık** anahtarına şifreler (sunucu açamaz; düz kopya hiçbir yere yazılmaz) ve yedek biriminde
`offsite-extra/escrow/ravencia-secrets.tar.age` olarak bırakır. Worker'ın Drive kopyası bunu her sete
`<set>/extra/escrow/` olarak ekler (yalnız age başlıklı `.age` dosyaları alınır). Emanet, dosyaların ya da age anahtarının
parmak izini tutar; bir sertifika yenilenir, anahtar değişir ya da emanet yoksa "bayat" sayılır ve günlük zamanlayıcı
yeniden üretir. Açmak, sunucuda repo kökünde:
```bash
sudo touch deploy/secrets/secrets_escrow.enabled && bash deploy/scripts/install-backup-timer.sh
sudo systemctl start ravencia-secrets-escrow.service && journalctl -u ravencia-secrets-escrow -n 5   # "refreshed"
```
Sonra `ravencia-secrets-escrow.timer` her gün 08:40 UTC'de (08:59 setinden önce) çalışır, başarısızlıkta alarm gelir;
health watch `secrets-escrow.sh --check` ile emanet bayatsa uyarır. Doğrulama: bir sonraki Drive kopyasının worker logunda
`extras: extra/escrow/ravencia-secrets.tar.age` (`/prod-logs logs worker --grep "Off-site backup"`).
**Emanetle açılış tatbikatı:** Drive kopyasını sahibin bilgisayarında açıp (`age -d -i ravencia-yedek-anahtari.txt
<set>.tar.age | tar -x`) sunucuya ya da Docker'lı bir makineye getirin; özel anahtar yalnız tatbikat süresince orada
(tercihen `/dev/shm`) durur:
`sudo bash deploy/backup/restore-drill.sh --escrow <set>/extra/escrow/ravencia-secrets.tar.age /dev/shm/anahtar.txt <set> <app imajı>`
(canlı `deploy/secrets` yerine emanetteki sertifika ve anahtarla, yeni bir veritabanı parolasıyla açar; bitince
`shred -u /dev/shm/anahtar.txt`). Yılda bir kez yapın.

Yedek setlerinin kendisi de sunucu dışına şifreli kopyalanır: `deploy/backup/offsite-copy.sh` en yeni
doğrulanmış seti aynı açık anahtarla şifreleyip rclone ile Google Drive'a yükler (yapılandırılana kadar
"not configured" yazıp hiçbir şey yapmaz). Kurulum ve geri açma: [CI-CD-DEPLOY.md](CI-CD-DEPLOY.md)
"Sunucu dışı yedek". Sunucu kaybında geri yükleme için hem sunucu dışı set hem bu emanet gerekir.

## 6. Aylık tatbikat

`ravencia-restore-drill.timer` her ayın ilk Pazar'ı 22:00 TR'de en yeni seti izole konteynerlere geri
yükler; başarısızlıkta alarm gelir. Sonucu görmek: `journalctl -u ravencia-restore-drill -n 50`.
Elle çalıştırmak: `sudo systemctl start ravencia-restore-drill.service`.
**Sahibin kontrol listesi:** 3 Kasım 2026 Hostinger taşınmasından beri tatbikat hiç çalışmadı (zamanlayıcının ilk
çalışması 6 Aralık 2026); sessiz bir saatte bir kez elle başlatın (`sudo systemctl start ravencia-restore-drill.service`)
ve `journalctl -u ravencia-restore-drill -n 50`'de sonucun başarılı olduğunu görün.

## 7. Uygulama veritabanı rolleri (sahibin adımı, VERITABANI-DAGITIM-1)

**Durum (15 Kasım 2026):** API, Worker ve migrate `marketplacehub` ile bağlanıyor ve bu rol **SUPERUSER**
(`rolsuper = t`, canlıda doğrulandı). Bir SQL enjeksiyonu `COPY ... TO PROGRAM` ile konteynerde komut çalıştırabilir.
Hedef: migrate `marketplacehub_owner`, API/Worker `marketplacehub_app` (ikisi de süper değil); `marketplacehub` yalnız
yedek, healthwatch ve salt-okunur rol betiği için kalır. Deploy bunu **yapmaz**; adımlar sessiz bir saatte, deploy
kilidi tutulurken, sırayla:

1. Durumu gör (salt okur): `bash deploy/scripts/install-app-db-roles.sh --check`; çalışacak SQL:
   `bash deploy/scripts/install-app-db-roles.sh --print-sql`.
2. Yedek: `sudo systemctl start ravencia-backup.service` ve `journalctl -u ravencia-backup -n 5` ("verified").
3. Rolleri kur: `bash deploy/scripts/install-app-db-roles.sh --apply`. Tek işlem: iki rol, veritabanı/şema/tablo/
   dizi/fonksiyon sahipliği `marketplacehub_owner`'a, satır yetkileri `marketplacehub_app`'e, sonraki migration'lar
   için varsayılan yetkiler. Her tablo için kısa ACCESS EXCLUSIVE kilit alır (birkaç saniye API yazmaları bekler);
   `lock_timeout 5s` dolarsa hiçbir şey değişmez, tekrar çalıştırın. `deploy/secrets/migrate_db_connection.txt` ve
   `deploy/secrets/app_db_connection.app-role.txt` (root:1654 0640) yazılır. Bu adımdan sonra da her şey eskisi gibi
   `marketplacehub` ile çalışır (o rol süper olduğundan sahiplik değişimi onu etkilemez). Bu adımdan sonraki yedekler
   iki role `GRANT` taşır; migration provası ve aylık tatbikat izole veritabanında iki rolü `NOLOGIN` olarak önceden
   yaratır (`restore-drill.sh`; bu değişiklik adım 3'ten önce canlıda olmalı), yoksa geri yükleme ilk `GRANT`'ta durur
   ve migration'lı her deploy provada kalır.
   Varsayılan yetkiler `marketplacehub`'ın yarattığı tabloları da kapsar: 3 ile 5 arasında migration'lı bir deploy
   olursa yeni tablolar `marketplacehub_app`'e açıktır; sahiplikleri için adım 5'ten önce `--apply`'ı bir kez daha
   çalıştırın. Rol ayarı: `marketplacehub_app` için `idle_in_transaction_session_timeout = 5min`.
4. Kod/yapılandırma değişikliği: hazır, **uygulanmamış** yama
   [`docs/patches/VERITABANI-DAGITIM-1-app-db-roles-switch.patch`](patches/VERITABANI-DAGITIM-1-app-db-roles-switch.patch).
   Deploy onu kendiliğinden almaz; dosyalar sunucuda var olmadan **gönderilmez**, yoksa Compose secret dosyasını
   bulamaz ve deploy durur. Bir oturum (ya da sahip) yeni bir dalda `git apply docs/patches/VERITABANI-DAGITIM-1-app-db-roles-switch.patch`
   ile uygular, yamayı ve bu maddeyi "uygulandı" diye günceller, Validate'i bekler ve gönderir. Yama:
   - `deploy/compose/compose.yaml`: `secrets:` altına `migrate_db_connection: { file: ../secrets/migrate_db_connection.txt }`;
     `migrate` servisi `secrets: [{ source: migrate_db_connection, target: app_db_connection }, credential_key]`
     (aynı hedef yol, `ConnectionStrings__AppDb_FILE` değişmez); `compose.production.yaml`'da `migrate` için
     `secrets: !override [{ source: migrate_db_connection, target: app_db_connection }, credential_key, dp_certificate,
     dp_certificate_password]`, `worker` kendi listesini açıkça yazar (`*production_secrets` kalkar).
   - `deploy/backup/restore-drill.sh` (migration provası ve aylık tatbikat): yalnız `marketplacehub` süper kullanıcısı
     olan izole bir veritabanı açar; bağlantıyı `app_db_connection.txt`'nin sunucu/veritabanı/ayarlarından,
     `Username=marketplacehub` ve `postgres_password.txt` parolasıyla kurar (yoksa her deploy'un provası
     "password authentication failed" ile durur).
   - `deploy/scripts/lib.sh` `lib_required_secret_files`'a `migrate_db_connection.txt`; `ci.yml` Compose doğrulamasının
     yer tutucu dosyalarına da.
   `tests/ops/test-app-db-roles.sh` yamanın hâlâ temiz uygulandığını denetler; o dosyalarda başka bir değişiklik yamayı
   bozarsa test kırmızı olur: yamayı bu maddedeki tarife göre yeniden üretin (`git diff > …patch`, dosyaları geri alın).
5. Sunucuda geçiş (yukarıdaki değişiklik canlıya çıktıktan hemen sonra):
   `sudo cp deploy/secrets/app_db_connection.txt deploy/secrets/app_db_connection.superuser.txt` (geri dönüş için),
   `sudo install -o root -g 1654 -m 0640 deploy/secrets/app_db_connection.app-role.txt deploy/secrets/app_db_connection.txt`,
   sonra `gh workflow run deploy.yml --ref main` (API/Worker yeni bağlantıyla yeniden yaratılır).
6. Doğrula: `/prod-sql "select usename, count(*) from pg_stat_activity where datname='marketplacehub' group by 1"` →
   API/Worker oturumları `marketplacehub_app`; `/prod-sql "select rolname, rolsuper from pg_roles where rolname like
   'marketplacehub%'"` → `_app` ve `_owner` için `f`; `/prod-logs errors --since 30m` boş (özellikle `42501
   permission denied`). Bir sonraki migration'lı deploy'da migrate `marketplacehub_owner` ile çalışır.

**Geri dönüş:** `sudo install -o root -g 1654 -m 0640 deploy/secrets/app_db_connection.superuser.txt
deploy/secrets/app_db_connection.txt` ve yeniden deploy (süper kullanıcı her nesneye erişir; sahipliği geri almak
gerekmez). Migrate değişikliği geri alınırsa `migrate_db_connection.txt` dosyası durabilir.
**Riskler:** (a) kodda bugün görülmeyen süper kullanıcı gerektiren bir yol (ör. `ALTER`, `TRUNCATE` gerçek tabloda)
`42501` ile düşer, hata loglarında görünür — geri dönüşle kapanır; (b) yeni şema/tablo açan migration'lar
varsayılan yetkilerle kapsanır (`marketplacehub_owner`'ın ve `marketplacehub`'ın yarattıkları), ama `marketplacehub`
ile yaratılan bir nesnenin sahibi o kalır: `--apply`'ı yeniden çalıştırın (geçişten sonra da güvenli; kullanılan
parolaları değiştirmez); (c) geri yükleme sonrası bölüm 3'teki not; (d) `idle_in_transaction_session_timeout=5min`
5 dakikadan uzun boşta bekleyen bir uygulama işlemini keser (bugün bilinen yok; kesilen iş hata alıp yeniden dener).

## 8. PostgreSQL ayar paketi (sahibin adımı, VERITABANI-DAGITIM-6 ve -7)

Canlı (15 Kasım 2026): `log_lock_waits=off`, `track_io_timing=off`, `max_wal_size=1GB`, `pg_stat_statements` yüklü değil;
uygulama rollerinde `idle_in_transaction_session_timeout` yok (yalnız `ravencia_ro`'da 60 sn); postgres konteynerinin
kapanma süresi Docker varsayılanı 10 sn. Değişiklik postgres'i **yeniden başlatır** (`shared_preload_libraries`
yeniden başlatma ister; birkaç saniye kesinti, API/Worker yeniden bağlanır), bu yüzden deploy'a otomatik konmadı.
Sessiz bir saatte, tek seferde:

1. Hazır, **uygulanmamış** yama: [`docs/patches/VERITABANI-DAGITIM-6-7-postgres-settings.patch`](patches/VERITABANI-DAGITIM-6-7-postgres-settings.patch).
   `compose.yaml` postgres servisine `stop_grace_period: 120s` ve `command` listesine (her `-c` ile değer ayrı satır)
   `idle_in_transaction_session_timeout=10min`, `log_lock_waits=on`, `deadlock_timeout=1s`, `log_autovacuum_min_duration=10s`,
   `log_temp_files=65536`, `track_io_timing=on`, `shared_preload_libraries=pg_stat_statements`,
   `pg_stat_statements.track=top`, `max_wal_size=4GB`; `ci.yml` "Validate merged Compose manifests" adımına bu ayarların
   ve `stop_grace_period`'ın kaybolmadığını denetleyen `jq` kontrolü. Uygulamak: yeni dalda
   `git apply docs/patches/VERITABANI-DAGITIM-6-7-postgres-settings.patch` (başka bir yama önce uygulandıysa `--3way`),
   yama dosyasını silip bu maddeyi "uygulandı" diye güncelleyin, Validate'i bekleyip gönderin. Gönderilen ilk deploy
   postgres'i yeniden yaratır (birkaç saniye kesinti).
2. Uzantıyı **süper kullanıcıyla** kurun: `pg_stat_statements` "trusted" bir uzantı değildir, `CREATE EXTENSION`
   süper kullanıcı ister. Bölüm 7'den sonra migrate `marketplacehub_owner` ile çalışacağından bunu migration'a koymayın
   (migration orada `permission denied to create extension` ile düşer ve deploy durur). Adım 1 canlıya çıktıktan sonra
   sunucuda bir kez:
   `sudo docker exec marketplacehub-postgres-1 psql -X -v ON_ERROR_STOP=1 -U marketplacehub -d marketplacehub -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements"`
   (yedek ve geri yükleme `marketplacehub` ile çalıştığı için uzantı yedekten de geri gelir).
3. Sık güncellenen tabloların autovacuum ayarı **sahip adımı değil, migration ile geliyor**:
   `20261116090200_HotTableAutovacuum` (`integration.jobs`, `job_attempts`, `rate_limit_usage`, `inbox_messages`,
   `inventory.inventory_items`, `channel_offers` için `autovacuum_vacuum_scale_factor` ve
   `autovacuum_analyze_scale_factor` 0.02; yazmaları bloke etmez, postgres yeniden başlamaz). Adım 1'den bağımsızdır.
   Doğrulama: `/prod-sql "select relname, reloptions from pg_class where relname in ('jobs','channel_offers') and relkind='r'"`.
4. Salt-okunur rol: `GRANT pg_read_all_stats, pg_read_all_settings TO ravencia_ro` artık
   `deploy/scripts/install-readonly-db-role.sh` içinde. Sunucuda repo kökünde tek komut
   `bash deploy/scripts/install-readonly-access.sh` (adım 1'den bağımsız, hemen yapılabilir) hem rolü yeniler hem güncel
   `ravencia-ro` komutunu kurar; yeni `top-queries` isteği (`/prod-logs top-queries 20`) uzantı yokken bunu söyler,
   varken en çok süren 20 sorguyu listeler. `pg_read_all_stats` diğer rollerin
   sorgu metnini `pg_stat_statements`'ta görmek için, `pg_read_all_settings` doğrulamadaki
   `show shared_preload_libraries` için gerekir (süper kullanıcıya özel ayar; o olmadan "permission denied").
5. Doğrula: `/prod-sql "show idle_in_transaction_session_timeout"` → `10min`; `/prod-sql "show
   shared_preload_libraries"` → `pg_stat_statements`; `/prod-sql "select count(*) from pg_stat_statements"` > 0;
   `docker inspect -f '{{.Config.StopTimeout}}' marketplacehub-postgres-1` → 120;
   `/prod-logs logs postgres --grep "shut down"` → bir sonraki yeniden başlatmada "database system is shut down".
**Risk:** `idle_in_transaction_session_timeout=10min` 10 dakikadan uzun boşta bekleyen bir uygulama işlemini keser
(bugün bilinen yok; kesilirse ilgili iş hata alıp yeniden dener). Geri dönüş: satırları silip yeniden deploy.

## 9. WAL arşivi ve zamana dönük geri yükleme (plan, VERITABANI-DAGITIM-8)

Bugün yalnız `pg_dump` var (günde iki kez + her deploy'da): sunucu kaybında son yedekten sonraki siparişler, kesilen
faturalar ve stok düşümleri kaybolur (RPO ≤ 12 saat); "14:37'den hemen önceki ana dön" yapılamaz. Canlı:
`archive_mode=off`, `wal_level=replica`. Gerekenler sahibin kararı ve hesabıdır:

1. **Depo:** S3 uyumlu, sunucu dışı (Backblaze B2, Cloudflare R2 ya da Hetzner Storage Box), mümkünse Object Lock;
   erişim anahtarı ve şifreleme anahtarı `deploy/secrets/walg.env` (root 0600) ve bölüm 5'teki emanete girer.
2. **İmaj:** `deploy/postgres/Dockerfile`, `postgres:18.6@sha256:…` üzerine sürümü ve sha256'sı sabit WAL-G ikili
   dosyası; `tests/ops/test-postgres-digest-sync.sh` yeni dosyayı da kapsayacak şekilde genişletilir.
3. **Ayar** (postgres yeniden başlar): `-c archive_mode=on -c archive_timeout=60 -c archive_command='wal-g wal-push %p'`
   ve `WALG_LIBSODIUM_KEY` ile şifreleme; haftalık `wal-g backup-push` için `ravencia-basebackup.timer`.
4. **İzleme:** healthwatch'a `select last_archived_time, failed_count from pg_stat_archiver` (son arşiv 10 dk'dan
   eskiyse ya da `failed_count` arttıysa alarm).
5. **Tatbikat:** `restore-drill.sh --pitr <zaman>`: izole konteynerde `wal-g backup-fetch` + `recovery_target_time`,
   aylık; sonuç `send-alert.sh`. Hedef RPO ≈ 1 dk.
**Risk:** arşiv komutu başarısız olursa WAL dosyaları diskte birikir (disk dolar): 4. maddedeki alarm olmadan açılmaz.

### 9a. Ara çözüm: yerel WAL arşivi + Drive kopyasıyla dışarı (hazır, uygulanmamış)

Depo kararı verilene kadar, yukarıdaki planın yerel yarısı tek yamayla açılır:
[`docs/patches/VERITABANI-DAGITIM-8-wal-archive.patch`](patches/VERITABANI-DAGITIM-8-wal-archive.patch).

- **Yama:** `compose.yaml` postgres'e `archive_mode=on`, `archive_timeout=300` ve gzip'leyen `archive_command`; ayrı
  volume `pg_wal_archive` (`/var/lib/postgresql/wal-archive`, `wal/` ve `base/`) ve onu postgres kullanıcısına veren
  tek seferlik `pg-wal-archive-volume` servisi; `install-backup-timer.sh`'a `ravencia-wal-basebackup.timer` (her gün
  02:30 UTC, `pg_basebackup`, son 2 temel yedek ve onların gerektirdiği WAL kalır) ve `ravencia-wal-ship.timer`
  (08:45 / 20:45 UTC); `ci.yml`'e arşiv ayarlarının kaybolmadığını denetleyen `jq` satırı.
- **Zaten canlıya giden ve yama gelene kadar hiçbir şey yapmayan parçalar:** `deploy/backup/wal-archive.sh`
  (`basebackup | ship | status`), `deploy/backup/restore-drill.sh --pitr '<zaman>'` (`restore-drill-pitr.sh`: izole,
  ağsız konteynerde temel yedek + WAL ile o ana döner, sonucu ve süreyi yazar), health watch'ta `pg_stat_archiver`
  denetimi (`archive_mode=on` olunca devreye girer: arşivleme hata veriyorsa ya da 20 dk'dır segment arşivlenmediyse
  alarm; `RAVENCIA_WAL_ARCHIVE_MAX_AGE_MINUTES`).
- **Dışarı çıkış:** `wal-archive.sh ship` en yeni temel yedeği ve ondan sonraki WAL'ı panelin age açık anahtarına
  şifreleyip `offsite-extra/wal/wal-archive.tar.age` olarak bırakır; worker'ın Google Drive kopyası onu sete
  `<set>/extra/wal/` olarak ekler. Sunucuda kurtarma noktası en çok 5 dakika geride; sunucu tamamen kaybolursa son
  Drive kopyası (günde iki kez). Drive'da her kopya bir temel yedek taşır (bugün veritabanı 1,35 GB; sıkıştırılmış temel
  yedek bunun bir kısmı): Drive saklama gün sayısını buna göre seçin.

Uygulama (sessiz bir saatte; postgres yeniden başlar, birkaç saniye kesinti):

1. Diskte yer: arşiv günde birkaç yüz MB ile birkaç GB arası (yoğunluğa bağlı) ve iki temel yedek. `df -h /` %70'in
   altında olmalı.
2. Yeni dalda `git apply docs/patches/VERITABANI-DAGITIM-8-wal-archive.patch` (başka bir yama önce uygulandıysa
   `--3way`), yama dosyasını silip bu bölümü "uygulandı" diye güncelleyin, Validate'i bekleyip gönderin. Deploy postgres'i
   yeniden yaratır ve zamanlayıcıları kurar.
3. Sunucuda: `sudo bash deploy/backup/wal-archive.sh basebackup` (ilk temel yedek), sonra
   `sudo bash deploy/backup/wal-archive.sh status` (`archive_mode=on`, `archived_count` artıyor, `failed_count` 0).
4. Tatbikat: 15 dakika sonra `sudo bash deploy/backup/restore-drill.sh --pitr "$(date -d '-10 min' '+%F %T%z')"`
   → `PITR drill passed`; çıkan `durationSeconds` geri dönüş süresidir. Ayda bir tekrarlayın.
5. `sudo bash deploy/backup/wal-archive.sh ship` ve bir sonraki Drive kopyasının worker logunda
   `extras: ... extra/wal/wal-archive.tar.age`.

**Gerçek bir "ana dön" işi:** önce tatbikatla doğru zamanı bulun (`--pitr` ile birkaç deneme), sonra bölüm 3'teki gibi
yazmaları durdurup canlı veritabanını o zamana döndürmek ayrı bir karardır (veritabanı yeniden kurulur, sonrası kaybolur).
**Geri dönüş:** yamanın compose satırlarını geri alıp deploy; `pg_wal_archive` volume'ü elle silinene kadar kalır.

## 10a. Bekleyen takip işleri

- **VERITABANI-DAGITIM-11, `billing.invoices."SequenceNumber"` sütununu silmek (genişlet-daralt 2. aşama):**
  hiç yazılmayan ve okunmayan sütun 16 Kasım 2026'da modelden çıkarıldı (`20261116090000_StopMappingInvoiceSequenceNumber`,
  boş `Up`; canlıda 2 faturanın hiçbirinde değer yok). **Yazıldı:** `20261116180000_DropInvoiceSequenceNumber`
  (`DROP COLUMN IF EXISTS`, `SET LOCAL lock_timeout = '5s'`, `Down` boş nullable sütunu geri ekler). Yazıldığı an
  canlı `ce8d854e99b4` ve bir önceki sürüm sütunu hiç anmıyor (16 Kasım, salt okuma: 7 faturanın 0'ında değer var);
  imaj geri alındığında da sütun okunmaz. Canlıya çıkınca: `/prod-sql --columns billing.invoices` listesinde
  `SequenceNumber` olmamalı.

## 10. Kesintisiz deploy: iki API kopyası (tasarım, VERITABANI-DAGITIM-4)

**Bugün (16 Kasım 2026):** tek `api` konteyneri var; tam deploy eskisini durdurup yenisini başlatır. Caddy iki
`reverse_proxy` bloğunda `lb_try_duration 45s` / `lb_try_interval 500ms` ile yeni API dinleyene kadar isteği bekletir:
bağlanamayan istek hemen 502 almaz, en çok 45 sn bekler. **Ölçüm:** Caddy erişim logu journald'da deploy'lar arasında
kalır (`compose.production.yaml`, `journald` sürücüsü; `docker logs` yalnız o anki konteyneri gösterir, ölçüm için
onu kullanmayın): `/prod-logs journal caddy --since <deploy zamanı> --grep '"status":50[234]|connection refused'`.
Canlı (salt okuma): 15 Kasım 22:22Z deploy'unda bu logda `connection refused` satırları var; 16 Kasım 00:17Z
deploy'undan sonra hiç yok (gece, trafik azdı; birkaç gündüz deploy'undan sonra yeniden sayın). Bu yeterli değilse
aşağıdaki tasarım kesintiyi sıfıra indirir; deploy akışını değiştirdiği için **hiçbiri uygulanmadı**.

**Akış (yeni önce, eski sonra):** migrate bugünkü gibi önce çalışır. Sonra `api` iki kopyaya çıkar: yeni imajla ikinci
konteyner açılır, sağlıklı olunca Caddy ona da istek gönderir, eski konteyner 45 sn'lik `stop_grace_period` ile
kapanır, tek kopyaya dönülür. Worker ve Caddy bugünkü gibi yeniden yaratılır.

1. `deploy/caddy/Caddyfile.production` ve `Caddyfile`, iki `reverse_proxy` bloğu: `api:8080` yerine Docker DNS'inden
   bütün kopyalar ve sağlık denetimi (`tests/ops/test-caddy-proxy.sh` buna göre güncellenir):
   ```caddyfile
   reverse_proxy {
       dynamic a {
           name api
           port 8080
           refresh 2s
       }
       lb_policy round_robin
       lb_try_duration 45s
       lb_try_interval 500ms
       health_uri /health/live
       health_interval 2s
       health_fails 2
       fail_duration 10s
   }
   ```
2. `deploy/scripts/fast-deploy.sh`, `release_services=(api worker caddy)` satırının yerine (`web_only` değilse):
   ```bash
   api_ids() { "${sudo_cmd[@]}" docker ps -q --filter label=com.docker.compose.project=marketplacehub --filter label=com.docker.compose.service=api; }
   old_api="$(api_ids)"
   "${compose[@]}" up -d --no-build --no-deps --no-recreate --scale api=2 api || release_status=$?
   new_api="$(api_ids | grep -vxF "$old_api" || true)"
   healthy=false
   for _ in $(seq 1 60); do
     if [[ "$("${sudo_cmd[@]}" docker inspect -f '{{.State.Health.Status}}' "$new_api")" == healthy ]]; then healthy=true; break; fi
     sleep 2
   done
   if [[ "$healthy" == true ]]; then
     sleep 5   # Caddy'nin DNS yenilemesi ve sağlık denetimi yeni kopyayı görsün
     "${sudo_cmd[@]}" docker stop --time 45 "$old_api"
     "${sudo_cmd[@]}" docker rm "$old_api"
   else
     "${sudo_cmd[@]}" docker rm -f "$new_api"   # eskisi hizmette kalır
     release_status=1
   fi
   release_services=(worker caddy)
   ```
   Konteyner adı bundan sonra `marketplacehub-api-2`, `-3`... olur.
3. Ada bağlı yerler etikete (`com.docker.compose.service=api`) geçer: `deploy/scripts/readonly-entrypoint.sh` (`images`,
   `logs`, `journal`: journald'da aynı alanın birden çok değeri VEYA'dır, `docker ps` ile bulunan adlar
   `CONTAINER_NAME=<ad>` olarak art arda verilir), `deploy/scripts/rollback.sh` ve `verify_stack` (`lib.sh`), bu
   runbook'taki `docker stop marketplacehub-api-1` komutları, `docs/RUNBOOK-OLAY.md`, `.claude/skills/prod-logs`.
   `healthwatch.sh` zaten etiketle buluyor.
4. Bellek: iki kopya birkaç saniye için 2 × 1,5 GB sınır; 16 GB sunucuda sorun değil.
5. Yan etkiler: iki kopya birkaç saniye birlikte çalışır. API'nin arka plan servisleri (idempotency ve silinmiş dosya
   saklaması, kategori değişikliği dinleyicisi, canlı yayın) aynı işi iki kez yapabilir; hepsi tekrarlanabilir silme ya
   da okuma. SignalR bağlantıları eski kopya kapanınca yeniden bağlanır (bugün de öyle). Data Protection anahtarları
   ortak volume'de (`dp_keys`).
6. Test: `tests/ops/test-fast-deploy-web-only.sh` gibi sahte `docker` ile "yeni sağlıklı → eski durdu" ve "yeni sağlıksız
   → eski kaldı, deploy 1 ile çıktı"; canlıda bir deploy sırasında `while curl -fsS https://<site>/health/live >/dev/null;
   do sleep 0.2; done` hiç kesilmemeli, Caddy logunda 502 olmamalı.
