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
| Deploy "Migration rehearsal failed … production was not migrated" ile durdu | Canlıya hiçbir şey olmadı; eski sürüm çalışıyor, `.hold` yok. Neden geçiciyse `gh workflow run deploy.yml --ref main` aynı sha'yı yeniden dener; migration hatalıysa ileri düzelt ([CI-CD-DEPLOY.md](CI-CD-DEPLOY.md) "Migration provası"). |
| Yeni migration veriyi **bozdu ya da sildi** (yanlış UPDATE/DELETE, kolon düşürüldü, veri dönüşümü hatalı) | **Veritabanını geri yükle** (bölüm 3). Yedekten sonraki tüm yazmalar kaybolur — son çare. |
| Eski kod yeni şemayla çalışamıyor (kolon adı değişti, NOT NULL kolon eklendi) | Kod geri alma tek başına yetmez: ya ileri düzelt ya da bölüm 3. |
| `20261127170948_ProductFamilyRestrict` (kart→model bağı `ON DELETE RESTRICT`, KATALOG-14 D5 B1 incelemesi) kilit zaman aşımıyla durdu ya da yarıda kaldı | Veri değişmedi. Her adım yeniden çalışabilir: `gh workflow run deploy.yml --ref main` tamamlar. Yarıda kalırsa eski `SET NULL` bağın yanında `FK_products_product_models_TenantId_FamilyId_restrict` adlı geçici bağ kalabilir; zararsızdır (kartı olan modeli ikisi de silmez). Kod geri alınırsa şema kalabilir: önceki sürüm kartı olan modeli zaten silmez; tek fark, eşzamanlı yarışta not_null_violation yerine restrict_violation almasıdır. Şemayı da geri çevirmek gerekirse migration'ın `Down`'ı aynı üç adımla `SET NULL ("FamilyId")`'e döner. |
| `20261127111010_ProductFamilyRequired` (kartta `FamilyId` NOT NULL, KATALOG-14 D5 B1) "modeli olmayan N ürün kartı var" diye durdu ya da yarıda kaldı | Veri değişmedi, geri yükleme gerekmez. Modelsiz kart yalnız B1 aşama 1'den eski kodla yazılmış olabilir; Worker açılış geçişi / gece taraması onları kendi modellerine bağlar, sonra `gh workflow run deploy.yml --ref main` migration'ı yeniden dener (her adım yeniden çalışabilir). Kalan `CK_products_FamilyId_NotNull` zararsızdır, yalnız NULL eklemeyi reddeder; yeniden çalıştırma onu doğrulayıp düşürür. Aşama 1'den **eski** bir koda dönülecekse önce şemayı geri çevir: `ALTER TABLE catalog.products ALTER COLUMN "FamilyId" DROP NOT NULL;` (migration'ın `Down`'ı; satır değişmez, model bağına dokunulmadı). |

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

### 2a. Kuyruk Faz 1A anahtarları (işlerin ne zaman oluştuğu)

Faz 1A (27 Kas 2026) yalnız işlerin ne zaman oluştuğunu değiştirir; her davranış bir anahtarla kapatılır,
kapalı hali eski davranışın aynısıdır. Değerler `src/MarketplaceHub.Worker/appsettings.json` içinde
`Worker` altında açıkça yazılıdır. Üretim compose dosyası bunları ortam değişkeni olarak geçirmez (ve
o dosya bunun için değiştirilmez), yani **sunucuda deploy'suz kapatma yoktur**: değeri `false` yapıp
commit edin ve normal yayınlayın (`ship`). Worker yeni sürümle açılınca geçerli olur. Kod geri almak
gerekirse yukarıdaki `rollback.sh` de olur; veritabanı değişikliği yoktur.

| Anahtar | Açıkken | Kapatınca |
|---|---|---|
| `ReferenceReads:OneAtATime` | Bağlantının kategori ve marka listesi sırayla, kiracıda tek referans okuması | Bütün bayat listeler aynı geçişte |
| `ReferenceReads:Spread` (+ `WindowStartHour` 6, `WindowHours` 4) | Her bağlantının günlük referans okuması 06:00–10:00 İstanbul içinde kendi sabit dakikasında | Öğleden sonra günlük penceresi (14:00–17:00) |
| `StartupSpread:Enabled` (+ `Seconds` 180) | Worker açılışında vadesi gelmiş arka plan okumaları 3 dk'ya yayılır | Hepsi aynı anda |
| `HoldPollsWhileFailing:Enabled` | Zamanlanmış bir okuma pazaryeri kesintisiyle tekrar beklerken o bağlantıya yeni arka plan okuması açılmaz | Açılır (eski davranış) |
| `JobFailureAlerts:Enabled` | Ölü / incelemedeki / bir gündür engelli iş zile düşer (günde en çok 30 + bir özet) | Uyarı yazılmaz |

Kontrol: `.local-docs/kuyruk-faz1a-verify.sql` (salt okunur, `prod-sql` ile) önce/sonra sayıları verir.

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

**Drive kopyası (zamanlı kısım, anahtarsız):** aynı tatbikat sonunda `deploy/backup/offsite-drill.sh record` çalışır.
Sunucuda ne Google yetkisi (yenileme belirteci veritabanında şifreli, yalnız uygulama kullanır) ne de age **özel** anahtarı
vardır; bu yüzden zamanlı kısım Drive'dan **indirmez ve çözmez**. Yaptığı: worker'ın yükleme kaydını (Sistem Ayarları ›
Google Drive Yedeği durumu, salt okunur) okur; kopya kapalıysa, son başarılı yükleme 36 saatten eskiyse, uygulamanın alarm eşiği kadar (3) art arda hata ya
da geçersiz yetki varsa, kaydın boyutu/MD5'i yoksa ya da kopyadaki set burada yoksa veya sağlama toplamları tutmuyorsa
tatbikat başarısız olur (alarm gelir). Başarılı satır `Off-site record check passed: set=... NOT tested: download from
Drive and decryption ...` der: indirme ve çözme bu satırla kanıtlanmış **değildir**.

**Drive kopyası tatbikatı (sahibin aylık elle adımı, özel anahtarla):** zamanlı tatbikatın ardından, ayda bir:
1. Kendi bilgisayarınızda Google Drive'daki yedek klasöründen en yeni `<set>.tar.age` dosyasını indirin (adını
   değiştirmeyin) ve sunucuya kopyalayın: `scp 20261125T213011Z.tar.age ubuntu@187.126.24.163:/dev/shm/`.
2. Özel anahtarı yalnız tatbikat süresince RAM'deki bir dosyaya koyun (diske yazılmaz, hiçbir yere kopyalanmaz):
   `scp ravencia-yedek-anahtari.txt ubuntu@187.126.24.163:/dev/shm/anahtar.txt`.
3. Sunucuda repo kökünde:
   ```bash
   sudo bash deploy/backup/offsite-drill.sh restore /dev/shm/20261125T213011Z.tar.age /dev/shm/anahtar.txt
   ```
   Betik age başlığını, boyut ve MD5'i worker'ın kaydıyla (set son gönderilen setse), çözmeyi (uygulama imajındaki
   `age`, ağsız konteyner, anahtar salt okunur bağlanır), arşivdeki yolları, `SHA256SUMS`'u, sunucudaki aynı setle
   aynılığı denetler ve seti geçici bir volume'den `restore-drill.sh` ile atılacak konteynerlere geri yükler (şemalar,
   migration'lar, API ve Worker sağlığı). Arşivde normal dosya ve dizin dışında bir üye (sembolik/sert bağlantı, aygıt)
   varsa açmadan durur; dosyalar `--no-same-owner` ile açılır. **Çözülmüş set diske yazılır:** `mktemp -d` ile
   `$TMPDIR`'ın (verilmezse `/tmp`, sunucuda kök diskte) altında yalnız root'un okuyabildiği (0700) bir dizine; en yüksek
   anda kopyanın yaklaşık iki katı (bugün ~140 MB'lık kopya için ~300 MB), ayrıca geçici Docker volume'ünde bir kat
   daha (`/var/lib/docker`). Başka bir diske almak için: `sudo TMPDIR=/mnt/bos-disk bash deploy/backup/offsite-drill.sh
   restore …`; `df -h /tmp /var/lib/docker` ile yeri önce görün. Çözülmüş dosyalar, geçici volume ve konteynerler her
   durumda silinir.
   Beklenen son satır: `Off-site restore drill passed: set=... decrypted=yes checksums=ok restore=ok`.
4. Ne çıkarsa çıksın anahtarı ve kopyayı silin: `shred -u /dev/shm/anahtar.txt && rm -f /dev/shm/*.tar.age`.
Başarısızlık "wrong key or a damaged copy", "does not match the worker's upload record" ya da bir sağlama hatası
diyorsa Drive kopyasına güvenilemez: Sistem Ayarları › Google Drive Yedeği'nde **Şimdi Gönder** ile yeni kopya alın ve
adımı yeni kopyayla tekrarlayın; yine başarısızsa age açık anahtarının sizdeki özel anahtarla eşleştiğini kontrol edin.

## 7. Uygulama veritabanı rolleri (sahibin adımı, VERITABANI-DAGITIM-1)

**Durum (15 Kasım 2026):** API, Worker ve migrate `marketplacehub` ile bağlanıyor ve bu rol **SUPERUSER**
(`rolsuper = t`, canlıda doğrulandı). Bir SQL enjeksiyonu `COPY ... TO PROGRAM` ile konteynerde komut çalıştırabilir.
Hedef: migrate `marketplacehub_owner`, API/Worker `marketplacehub_app` (ikisi de süper değil); `marketplacehub` yalnız
yedek, healthwatch ve salt-okunur rol betiği için kalır. Deploy bunu **yapmaz**; adımlar sessiz bir saatte, deploy
kilidi tutulurken, sırayla. Komutlar sunucuda `ubuntu` olarak deploy checkout'unun kökünde (`git pull` ile `main`'de)
çalışır.

0. Ön koşullar (hepsi sağlanmadan adım 3'e geçmeyin):
   - Süren deploy yok: kendi bilgisayarınızda `gh run list --workflow deploy.yml --limit 3` → hiçbiri `in_progress`
     değil; adımlar bitene kadar `main`'e gönderim yapılmasın (oturumlara söyleyin).
   - Canlı sürüm, izole tatbikatta iki rolü `NOLOGIN` önceden yaratan `restore-drill.sh`'ı taşıyor (repo'da var;
     sunucudaki kök kopyada kontrol: `grep -c marketplacehub_app /usr/local/lib/ravencia/deploy/backup/restore-drill.sh`
     → 1'den büyük). Değilse önce bir deploy.
   - Disk ve sağlık: `df -h /` (%80'in altı), `sudo docker ps` (dört konteyner `healthy`), `sudo systemctl --failed`
     (ravencia-* yok).
1. Durumu gör (salt okur): `bash deploy/scripts/install-app-db-roles.sh --check`; çalışacak SQL:
   `bash deploy/scripts/install-app-db-roles.sh --print-sql`. Beklenen: `marketplacehub` `rolsuper = t`, nesnelerin
   sahibi `marketplacehub`, oturumlar `marketplacehub` (ve `ravencia_ro`).
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
   Deploy bitince API ve Worker'ın yeni bağlantıyla açıldığını görün: `sudo docker ps` (ikisi `healthy`) ve
   `curl -fsS https://panel.ravencia.com/health/live` (hazırlık `/health/ready` yalnız sunucudan: `sudo docker exec marketplacehub-api-1 dotnet api/MarketplaceHub.Api.dll healthcheck && echo ready`).
6. Doğrula (salt okur). Sunucuda: `bash deploy/scripts/install-app-db-roles.sh --check` → `marketplacehub_app` ve
   `marketplacehub_owner` için `rolsuper = f`, nesnelerin sahibi `marketplacehub_owner` (pg_trgm'in birkaç nesnesi
   `marketplacehub`'ta kalır), oturumlar `marketplacehub_app`. Ya da kendi bilgisayarınızda `/prod-sql` ile şu dosya:
   ```sql
   SELECT rolname, rolsuper FROM pg_roles WHERE rolname LIKE 'marketplacehub%' ORDER BY 1;
   SELECT usename, application_name, count(*) FROM pg_stat_activity WHERE datname = 'marketplacehub' GROUP BY 1, 2 ORDER BY 1, 2;
   SELECT has_table_privilege('marketplacehub_app', 'ops.audit_logs', 'UPDATE') AS audit_update,
          has_table_privilege('marketplacehub_app', 'public."__EFMigrationsHistory"', 'DELETE') AS history_delete;
   ```
   → `_app` ve `_owner` `f`; API/Worker oturumları `marketplacehub_app`; iki yetki `f`. Sonra
   `/prod-logs errors --since 30m` boş olmalı (özellikle `42501 permission denied`). Bir sonraki migration'lı deploy'da
   migrate `marketplacehub_owner` ile çalışır: o deploy'un logunda migrate'in başarılı olduğunu ve `--check`'te yeni
   tabloların sahibinin `marketplacehub_owner` olduğunu görün.

**Geri dönüş** (adım 5'ten sonra herhangi bir anda; `42501` hataları ya da sağlıksız API/Worker):
1. `sudo install -o root -g 1654 -m 0640 deploy/secrets/app_db_connection.superuser.txt deploy/secrets/app_db_connection.txt`
2. `gh workflow run deploy.yml --ref main` (API/Worker süper kullanıcıyla yeniden yaratılır; süper kullanıcı her nesneye
   erişir, sahipliği geri almak gerekmez).
3. Doğrula: `--check`'te oturumlar yeniden `marketplacehub`, `/prod-logs errors --since 30m` boş.
Migrate de geri alınacaksa yamanın geri alınışı (`git revert <yama commit'i>`) gönderilir; `migrate_db_connection.txt`
dosyası durabilir. Adım 3'ten sonra ama 5'ten önce geri dönmek gerekmez: o sırada her şey `marketplacehub` ile çalışır.
Rolleri tamamen kaldırmak gerekmez ve önerilmez (yedekler onlara `GRANT` taşır).
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
  boş `Up`; 1. aşama yazılırken canlıda 2 faturanın hiçbirinde değer yoktu). **Yazıldı:** `20261116180000_DropInvoiceSequenceNumber`
  (`DROP COLUMN IF EXISTS`, `SET LOCAL lock_timeout = '5s'`, `Down` boş nullable sütunu geri ekler). Yazıldığı an
  canlı `ce8d854e99b4` ve bir önceki sürüm sütunu hiç anmıyor (16 Kasım, salt okuma: 7 faturanın 0'ında değer var);
  imaj geri alındığında da sütun okunmaz. `6802b4706b64` öncesi bir imaja ELLE dönülecekse önce bu migration'ın
  `Down`'ı çalıştırılır: o imajlar sütunu eşler, sütun yokken her fatura sorgusu 42703 ile düşer (otomatik geri alma
  bu durumu üretmez). **Canlıda uygulandı:** 17 Kasım 2026, salt okuma: `billing.invoices`'ta `SequenceNumber`
  sütunu yok, son uygulanan migration `20261117181003_ProductModelCodeKeyPerBrand`.
- **KATALOG-14 model anahtarı:** `20261117181003_ProductModelCodeKeyPerBrand` `(TenantId, ModelCodeKey)` indeksini
  yeniden düz yaptı (model = marka + model kodu; tekil indeks `(TenantId, BrandId, ModelCodeKey)` kalır). Önceki sürüm
  düz indeksle sorunsuz çalışır, yalnız kod geri alma güvenlidir. `Down` aynı kodu iki markada taşıyan model varsa
  sayıyla durur, hiçbir şey birleştirmez.

## 10. Kesintisiz deploy: iki API kopyası (hazır yama, uygulanmamış; VERITABANI-DAGITIM-4)

**Bugün (16 Kasım 2026):** tek `api` konteyneri var; tam deploy eskisini durdurup yenisini başlatır. Caddy iki
`reverse_proxy` bloğunda `lb_try_duration 45s` / `lb_try_interval 500ms` ile yeni API dinleyene kadar isteği bekletir:
bağlanamayan istek hemen 502 almaz, en çok 45 sn bekler. **Ölçüm:** Caddy erişim logu journald'da deploy'lar arasında
kalır (`compose.production.yaml`, `journald` sürücüsü; `docker logs` yalnız o anki konteyneri gösterir, ölçüm için
onu kullanmayın): `/prod-logs journal caddy --since <deploy zamanı> --grep '"status":50[234]|connection refused'`.
Canlı (salt okuma): 15 Kasım 22:22Z deploy'unda bu logda `connection refused` satırları var; 16 Kasım 00:17Z
deploy'undan sonra hiç yok (gece, trafik azdı; birkaç gündüz deploy'undan sonra yeniden sayın). Bu yeterli değilse
aşağıdaki hazır yama kesintiyi sıfıra indirir; deploy akışını değiştirdiği için **uygulanmadı**.

Hazır, **uygulanmamış** yama:
[`docs/patches/VERITABANI-DAGITIM-4-api-rollover.patch`](patches/VERITABANI-DAGITIM-4-api-rollover.patch).
`tests/ops/test-owner-patches.sh` yamanın temiz uygulandığını ve henüz uygulanmadığını, `tests/ops/test-api-rollover.sh`
yamadaki geçiş mantığını (yamayı geçici bir kopyaya uygulayıp, sahte `docker`/`compose` ile) her Validate'te dener.

**Akış (yeni önce, eski sonra):** migrate bugünkü gibi önce çalışır. Sonra `api_rollover` (yeni
`deploy/scripts/api-rollover.sh`, `fast-deploy.sh` yükler): `compose up -d --no-build --no-deps --no-recreate
--scale api=2 api` çalışan kopyaya dokunmadan yeni imajla ikinci konteyneri açar; Docker onu `healthy` bildirene kadar
(en çok 60 × 2 sn) beklenir; 5 sn sonra (Caddy'nin DNS yenilemesi yeni kopyayı görsün) eski kopya
`docker stop --time 45` ile (istekleri bitsin) durdurulur ve silinir. Yeni kopya açılmaz, `unhealthy` olur ya da çıkarsa
silinir, **eskisi hizmette kalır** ve sürüm başarısız sayılır (bugünkü gibi uyarı; otomatik geri alma yalnız worker ve
Caddy'yi önceki imajla yeniden yaratır, onlar zaten eskidir). Worker ve Caddy sonra bugünkü gibi yeniden yaratılır.
Web-only sürüm değişmez (yalnız Caddy). Konteyner adı bundan sonra `marketplacehub-api-2`, `-3`... olur.

Yamanın içeriği:

1. `deploy/caddy/Caddyfile.production` ve `Caddyfile`, iki `reverse_proxy` bloğu: `api:8080` yerine Docker DNS'inin
   `api` adına verdiği bütün kopyalar, her saniye yeniden okunur:
   ```caddyfile
   reverse_proxy {
       dynamic a api 8080 {
           refresh 1s
       }
       lb_policy round_robin
       fail_duration 10s
       lb_try_duration 45s
       lb_try_interval 500ms
   }
   ```
   **Önceki tasarımdan fark:** Caddy belgesine göre etkin sağlık denetimi (`health_uri` ...) dinamik upstream'lerde
   çalışmaz ("active health checks do not run for dynamic upstreams", caddyserver.com reverse_proxy). Bu yüzden
   `health_uri` yok: yeni kopyanın sağlığını deploy betiği Docker'ın healthcheck'iyle bekler; bağlantıyı reddeden kopya
   (kapanmakta olan eski) edilgen denetimle 10 sn atlanır, istek diğerine yeniden denenir (bağlantı kurulamadığında her
   yöntem için yeniden deneme serbest). `tests/ops/test-caddy-proxy.sh` iki dosyada da bunu arar.
2. `deploy/scripts/fast-deploy.sh`: `release_services=(api worker caddy)` yerine `api_rollover`, ardından
   `release_services=(worker caddy)`. `tests/ops/test-fast-deploy-web-only.sh` yeni betiği de kopyalar.
3. `deploy/compose/compose.production.yaml`: api'nin journald seçeneklerine `labels: "com.docker.compose.service"`
   (kayıtlar `COM_DOCKER_COMPOSE_SERVICE=api` alanını taşır). `compose.yaml` değişmez: api'de `container_name` ve
   yayımlanan port yok (ölçeklenebilir), `stop_grace_period: 45s` zaten var.
4. `marketplacehub-api-1` adını anan yerler (16 Kasım taraması):
   - Yamada değişenler: `deploy/scripts/readonly-entrypoint.sh` (`images`, `logs`, `errors`, `journal`: konteynerler
     Compose etiketiyle bulunur; `journal` eski `-1` adını, güncel adları ve `+` ile `COM_DOCKER_COMPOSE_SERVICE` alanını
     birlikte sorar), `deploy/scripts/healthwatch.sh` (uyarı metnindeki `journalctl` komutu; konteyneri zaten etiketle
     buluyor), `compose.production.yaml` baştaki yorum.
   - Yama uygulandığında elle güncellenecek belgeler: `CLAUDE.md` (Live satırı), `.claude/skills/prod-logs/SKILL.md`,
     `docs/CI-CD-DEPLOY.md` (journald örnekleri, MFA doğrulaması `docker exec`, `--pid container:` satırı),
     `docs/ISLEYIS-KILAVUZU.md` (konteyner tablosu), `docs/RUNBOOK-OLAY.md` (`journalctl`, `docker exec`,
     `docker restart`), bu runbook'un 1., 3. ve 4. bölümlerindeki `docker stop/start/restart/inspect
     marketplacehub-api-1` komutları: hepsinde ad yerine
     `$(sudo docker ps -q --filter label=com.docker.compose.service=api)`.
   - Değişmesi gerekmeyenler: `rollback.sh` (`compose up -d --no-deps api worker caddy` tek kopyayı yeniden yaratır;
     geri alma bugünkü gibi birkaç saniye Caddy'nin 45 sn beklemesiyle geçer), `lib.sh` `verify_stack` ve
     `running_service_image` (etiketle buluyor), `ship.sh` (`ravencia-ro images` aynı üç satırı verir).
5. Bellek: iki kopya birkaç saniye için 2 × 1,5 GB sınır; 16 GB sunucuda sorun değil.
6. Yan etkiler: iki kopya birkaç saniye birlikte çalışır. API'nin arka plan servisleri (idempotency ve silinmiş dosya
   saklaması, kategori değişikliği dinleyicisi, canlı yayın) aynı işi iki kez yapabilir; hepsi tekrarlanabilir silme ya
   da okuma. SignalR bağlantıları eski kopya kapanınca yeniden bağlanır (bugün de öyle). Data Protection anahtarları
   ortak volume'de (`dp_keys`). Kalan küçük risk: eski kopyaya açık bir keep-alive bağlantısında tam kapanış anında
   giden yazma isteği 502 alabilir (Go yalnız idempotent istekleri yeniden dener); 45 sn'lik kapanış süresi içinde
   Kestrel açık istekleri bitirir.

**Uygulama (sahip):**
1. Yeni dalda `git apply docs/patches/VERITABANI-DAGITIM-4-api-rollover.patch` (başka bir yama önce uygulandıysa
   `--3way`), yama dosyasını silip bu bölümü "uygulandı" diye güncelleyin, 4. maddedeki belgeleri düzeltin, Validate'i
   bekleyip gönderin. Readonly erişim betiği sunucuya `install-readonly-access.sh` ile yeniden kurulur (kurulu kopya
   deploy'la değişmez).
2. Bir sonraki tam deploy sırasında: `while curl -fsS https://<site>/health/live >/dev/null; do sleep 0.2; done` hiç
   kesilmemeli; sonra `/prod-logs journal caddy --since <deploy zamanı> --grep '"status":502|connection refused'` boş,
   `/prod-logs status`'ta tek `marketplacehub-api-<n>`; `journalctl -o verbose COM_DOCKER_COMPOSE_SERVICE=api -n 1`
   alanı gösteriyor olmalı.

**Geri dönüş:** yamanın commit'ini `git revert` edip gönderin; Caddy yeniden `api:8080` tek upstream'ine, deploy
yeniden "eskiyi durdur, yeniyi başlat" akışına döner; readonly erişim betiği eski hâliyle yeniden kurulur. O deploy
api'yi tek kopya olarak yeniden yaratır ama adı `marketplacehub-api-2` gibi kalabilir; geri alınmış betikler `-1` adını
aradığından o durumda bir kez (birkaç saniyelik kesinti, bugünkü bir deploy kadar) Compose ile api'yi sıfıra indirip
yeniden açın: `... up -d --no-build --no-deps --scale api=0 api`, ardından `... up -d --no-build --no-deps api`
(`...` = `fast-deploy.sh`'daki `compose` dizisinin komutu); yeni konteyner `marketplacehub-api-1` olur.

## 11. Marka listesi fark yazımı (Kuyruk Faz 1B, `References:BrandDiffWrites`)

Günlük marka okuması (Trendyol, Pazarama) güncel listeyi yerinde, yalnız farkı yazarak günceller
(`ReferenceSyncService.BrandDiff`). Eski yol (her farkta tüm listeyi yeni snapshot olarak yazmak) iki hafta yedek olarak
kodda kalır.

**Geri dönüş (eski tam kopya yoluna):** anahtar compose'tan geçmez; `production.env` yalnız compose'un `${...}`
değerlerini doldurur, oraya yazılan `References__BrandDiffWrites=false` worker'a ulaşmaz. Geri dönüş bir sürümdür:
`src/MarketplaceHub.Worker/appsettings.json` içinde `"References": { "BrandDiffWrites": false }` yapıp gönderin (tam
deploy, worker yeniden başlar). Tablo iki yolda da aynı biçimde kalır; geçiş istenen anda güvenlidir. Eski yolun ilk
okuması listeyi bir kez baştan yazar (fark yolundan sonra sıra numaraları ardışık değildir), sonrası eskisi gibidir.

**Küçülme kuralı:** bir okuma güncel listeden en çok `max(50 kayıt, 100.000'e kadar: min(%20, 2.000 kayıt), daha büyük
listelerde %2)` eksik olabilir (`ReferenceSyncService.AllowedBrandListDrop`; Trendyol ~820 bin markada ~16 bin, Pazarama
~103 bin markada ~2 bin). Fazlası güvenilmez okumadır: hiçbir şey yazılmaz ve silinmez, iş `Blocked REFERENCE_LIST_SHRANK`
olur, `reference-list-shrank:<bağlantı>:BRANDS` anahtarlı sorun açılır.

**Gerçek büyük küçülme (sahibin adımı, sürüm gerekmez):** önce pazaryerinin panelinde marka sayısının gerçekten düştüğünü
doğrulayın. Gerçekse Eşleştirmeler › Marka Eşleme'de o platformun kartındaki uyarıda **Küçülmeyi Onayla** (yalnız sahip ve
yönetici; denetim kaydına `REFERENCE_LIST_SHRINK_APPROVED` yazılır). Onay 48 saat geçerlidir ve bağlantının bir sonraki
marka okumasında bir kez kullanılır; beklemek istemezseniz aynı ekranda Senkronize Et. Kaybolan eşli markaların eşlemesi
küçük bir `RETIRED-` snapshot'ta kalır, yeniden eşlenmeleri gerekir. Onayın süresi dolarsa uyarı yeniden açılır.

**Ölçülen değerler (özel PG18, 27 Kas 2026, fsync kapalı):** 1 milyon satır, %0,1 değişiklik (400 ad, 300 yeni, 301
kaybolan):

| Yol | Süre | Eklenen / güncellenen / silinen | WAL |
| --- | --- | --- | --- |
| Eski, değişiklik yok | 1,9 s | 0 / 0 / 0 | 0 |
| Eski, %0,1 değişiklik | 62,9 s | 999.999 / 0 / 0 (+ ertesi gün 1 M silme) | 1.454 MiB |
| Yeni, değişiklik yok | 2,5 s | 0 / 0 / 0 | 0 |
| Yeni, %0,1 değişiklik | 3,3 s (tüm çalışma) | 300 / 400 / 301 | 12,5 MiB |

Uygulama sırasında arama beklemez: 60 bin satırda 30 bin değişiklik 1,0 s, en yavaş arama 23 ms; arama ya eski ya yeni
listenin tamamını görür. Taze 993 bin satırlık tablo 530 MB (canlıda 1.067 MB); tablo artık büyümez.

Canlı kontrol sorguları (salt okuma, boyut ve sayaçlar): `.local-docs/kuyruk-faz1b-verify.sql` (git dışı); boşalan
alanın geri kazanılması (VACUUM FULL / pg_repack / REINDEX CONCURRENTLY, ~500 MB) sahibin ayrı kararıdır, otomatik yapılmaz.

## 12. Model alanlarının kart kopyaları (KATALOG-14 D5 B2b, `Catalog:MirrorModelFields`)

Açıklama, KDV oranı, KDV dahil mi, kategori, garanti, teslimat süresi ve desi modelin alanlarıdır; bütün okuyucular modelden
okur (B2a, `EffectiveCatalog`). Renk kartı ve varyantlardaki aynı adlı sütunlar yalnız geri alma güvencesi olan
**kopyalardır**. Anahtar açıkken (bu sürümün değeri `true`) kayıtlar kopyaları modelle birlikte yazar; kapalıyken hiçbir
kayıt onları yazmaz. Ad (her rengin kendi adı) ve marka (kartın modele bağı) iki konumda da yazılır. Kısıt gereği kalan
yazımlar: yeni kartın açıklaması (sütun NOT NULL) ve Marka Listesi / kategori sıfırlamasının `"BrandId"`/`"CategoryId"` = NULL
adımları (RESTRICT bağları).

**Flip (kopyaları yazmayı bırakmak):** anahtar compose'tan geçmez; `production.env`e yazılan değer API'ye ve Worker'a
ulaşmaz. Flip bir sürümdür: `src/MarketplaceHub.Api/appsettings.json` **ve** `src/MarketplaceHub.Worker/appsettings.json`
içinde `"Catalog": { "MirrorModelFields": false }` yapıp gönderin (tam deploy; iki dosya aynı değeri taşımalı, bir test
bunu denetler). Önce `.local-docs/model-b2b-verify.sql` (git dışı) ile modellerin kendi içinde tutarlı olduğunu görün.

**Açık konum (bu sürüm):** davranış main ile aynıdır; kopya-model farkı uyarıları (`ModelMirror.ReportGapsAsync`, renkler
arası KDV uyarısı, `PRODUCT_MODEL_VALUES_DIFFER`) ve Worker'ın açılıştaki tek seferlik garanti onarımı eskisi gibi çalışır.
Bu sürümün düzeltmeleri iki konumda da geçerlidir: yazıcılar "değişti mi" sorusunu kopyaya değil modele sorar, editörün
kayıt sonrası model yazımı yalnız "editör kaydetti" işaretini (ve boşaltılan garantiyi) yazar, başarısız bir kayıt aynı
bağlamda yeniden denenince uygulanır.

**Geri alma:** iki dosyada `true` yapıp gönderin. Kendiliğinden hiçbir şey yeniden yazılmaz (geri doldurma yok, sahip
kuralı): kapalıyken düzenlenen satırların kopyaları bayat kalır; hiçbir okuyucu onları okumaz. Açık konumda gece kontrolü,
kapalıyken KDV'si değişen modeller için bilgi amaçlı `PRODUCT_MODEL_VALUES_DIFFER` uyarısı açar (main'in her kopya farkında
açtığı uyarı); fatura ve ekranlar modelin değerini kullanır. Kapatmak için sahibin o ürünün renklerini düzenleyicide bir kez
(hiçbir şey değiştirmeden) kaydetmesi yeter: kayıt bayat kopyayı modelden iyileştirir (alan değişikliği sayılmaz; pazaryeri
bağı, iş ya da outbox olarak aynı kaydın main'de yaptığından fazlası olmaz) ve sonraki kontrol uyarıyı kapatır
(`ModelFieldCopiesReviewPostgreSqlTests`). Kaydedilmeyen satırların kopyası bayat ve okunmadan kalır.

**Kapalı konumda değişenler:** sahibin kaydı (düzenleyici, Excel, Toplu Değer Ata, API) değeri modele yazar ve modelin
bütün renklerine uygular; pazaryeri bağları açık konumdakiyle aynı biçimde "düzenlendi" işaretlenir (aynı gönderimler
sıraya girer, `ModelFieldCopiesPostgreSqlTests`). Yazıcılar "değişti mi" sorusunu kopyaya değil modele sorar (iki konumda).
Kopya-model farkını ölçen denetimler (`ModelMirror.ReportGapsAsync`, renkler arası KDV uyarısı) yalnız kapalıyken fark aramaz
(hiçbir okuyucu kopyayı okumadığı için fark yoktur); garanti onarımı yalnız kapalıyken hiçbir şey yazmaz, modele göre sayar. C (sütunları düşürmek) ayrı sahip onayı ister ve geri alınamaz.

**Testleri iki konumda koşmak:** testler varsayılan olarak gönderilen konumda (açık) koşar;
`MARKETPLACEHUB_TEST_MIRROR_MODEL_FIELDS=false` bütün koşuyu kapalı konumda çalıştırır. Koşunun konumu test çıktısında
`[model-field-copies] Catalog:MirrorModelFields = … (asked: …)` satırıyla görünür (ayrıca `%TEMP%\marketplacehub-model-field-copies.log`);
istenen konum tutmazsa fixture hata verir. Ham kopya sütununu denetleyen testler onu yalnız açık konumda denetler
(`ModelFieldCopiesTestPosition.Cards`/`VariantRows`: açıkken ham satır, kapalıyken modelin değeri). Kopyalardan etkilenen
sınıflar (B2b incelemesinde iki konumda koşulan küme) şu süzgeçle seçilir; boş bir veritabanına karşı her konum için bir kez:

```bash
export MARKETPLACEHUB_TEST_CONNECTION="Host=localhost;Port=<port>;Username=postgres;Database=<boş-db>"
export MARKETPLACEHUB_TEST_MIRROR_MODEL_FIELDS=false   # sonra true
dotnet test tests/MarketplaceHub.Application.Tests --filter "(FullyQualifiedName~Catalog|FullyQualifiedName~ProductModel|FullyQualifiedName~Family|FullyQualifiedName~ProductTransfer|FullyQualifiedName~Import|FullyQualifiedName~Bulk|FullyQualifiedName~Composer|FullyQualifiedName~Golden|FullyQualifiedName~StaleCardCopy|FullyQualifiedName~EffectiveCatalog|FullyQualifiedName~ModelSingleSource|FullyQualifiedName~ModelFieldCopies|FullyQualifiedName~ModelBrandCode|FullyQualifiedName~Warranty|FullyQualifiedName~ProductJob|FullyQualifiedName~Publication|FullyQualifiedName~StockPrice|FullyQualifiedName~ModelRecord|FullyQualifiedName~ContractTests|FullyQualifiedName~ProductEditor|FullyQualifiedName~Variant|FullyQualifiedName~PanelFactoryReset|FullyQualifiedName~Report|FullyQualifiedName~Product)&PostgreSqlLane!=Load"
```
