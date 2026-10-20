# Geri Alma Runbook'u

Bir deploy sonrası panel bozulduğunda ne yapılacağı. Komutlar sunucuda, `~/RavenciaEntegrasyon`
kökünden çalıştırılır. Genel deploy akışı: [CI-CD-DEPLOY.md](CI-CD-DEPLOY.md).

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

**`.hold` neden önemli**: Deploy her sabah 06:20 UTC'de son yeşil SHA'yı yeniden dener. `.hold`
dosyası, geri alınan sürümün (ve atalarının) otomatik olarak geri gelmesini engeller. `main`'e yeni bir
commit (düzeltme) geldiğinde o commit normal şekilde deploy olur ve `.hold` kalkar. Bozuk sürümü bilerek
yeniden açmak için: `rm ~/deploy-logs/.hold` ve Actions → Deploy → Run workflow (`redeploy` işaretli).

Not: Çalışma ağacı (git checkout) geri alınan sürümde kalmaz, son çekilen commit'te kalır; bu
bilinçlidir — sonraki düzeltme commit'i fast-forward ile gelir.

## 3. Veritabanını geri yükle (son çare)

Yedekler `marketplacehub_backup_staging` volume'ündedir (`<UTC zaman damgası>/database.dump`,
`private-volumes.tar.gz`, `SHA256SUMS`). Her deploy ve geri alma öncesinde taze set alınır; saklama:
son 72 saat tümü, 7 gün günlük, 4 hafta haftalık. Disk %85'i geçecekse en eski haftalık setler önce
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
6. **Başlat ve doğrula**: `sudo docker start marketplacehub-api-1 marketplacehub-worker-1`, sonra bölüm 1'deki
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
| `credential_key.txt` | Pazaryeri kimlik bilgilerinin şifreleme anahtarı |
| `dp_certificate.pfx`, `dp_certificate_password.txt`, `dp_certificate_metadata.txt` | Data Protection anahtar halkasının sertifikası |
| `postgres_password.txt`, `app_db_connection.txt` | Veritabanı erişimi (yeniden üretilebilir ama dump sahipliğiyle uyum için saklayın) |
| `production.env`, `alert_webhook_url.txt` | Yapılandırma |

Emanet kuralı (dosyaları **bu repoya, sohbete ya da şifresiz bir yere koymayın**):

1. Sahip, kendi bilgisayarında `age` anahtarı üretir; özel anahtar parola yöneticisinde kalır.
2. Sunucuda: `sudo tar -C deploy -czf - secrets | age -r <age-public-key> > ~/ravencia-secrets-$(date +%F).tar.gz.age`
3. Şifreli dosyayı sunucu dışına (sahip bilgisayarı / ayrı bir hesapta S3) taşı ve sunucudan sil.
4. Her anahtar/sertifika değişiminde tekrarla; yılda bir kez açıp `restore-drill.sh` ile dene.

Not: yedek setleri şu anda yalnızca aynı sunucudadır; disk ya da instance kaybına karşı setlerin de
şifreli bir dış hedefe kopyalanması ayrı bir karardır (IAM rolü gerekir).

## 6. Aylık tatbikat

`ravencia-restore-drill.timer` her ayın ilk Pazar'ı 22:00 TR'de en yeni seti izole konteynerlere geri
yükler; başarısızlıkta alarm gelir. Sonucu görmek: `journalctl -u ravencia-restore-drill -n 50`.
Elle çalıştırmak: `sudo systemctl start ravencia-restore-drill.service`.
