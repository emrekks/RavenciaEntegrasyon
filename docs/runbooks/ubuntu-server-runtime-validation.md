# Ubuntu Server 24.04 LTS Runtime Doğrulama Runbook'u

## Amaç ve bağlayıcı hedef

Bu runbook yalnız hedef production sunucusunda çalıştırılır. Bağlayıcı hedef Ubuntu Server 24.04 LTS x86_64, 4 vCPU, 8 GB RAM, 100-120 GB NVMe, doğrudan Docker Engine ve exact Docker Compose v2.40.2'dir. Yerel Windows geliştirme makinesi target kanıtı değildir.

## Salt-okunur ön kontrol

Hedefte aşağıdaki alanları tarih/saat ve operatörle kaydet:

```bash
cat /etc/os-release
uname -m
nproc
free -b
lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINTS
df -hT
systemctl is-enabled docker
systemctl is-active docker
docker version
docker info --format '{{json .}}'
docker compose version
```

Beklenen minimumlar:

| Alan | Kabul |
| --- | --- |
| OS | Ubuntu Server `24.04` LTS |
| Mimari | `x86_64` / Docker `linux/amd64` |
| CPU | En az 4 vCPU |
| RAM | En az 8 GB |
| Disk | 100-120 GB NVMe hedefi; uygulama/DB/backup staging için ölçülmüş boş alan |
| Docker | Linux Engine; boot sırasında enabled/active |
| Compose | Exact `v2.40.2`, Linux x86_64 SHA-256 `6c964d9655cd629ef43c5dc75d9612c2da319237debee54a7aef217e9f362b88` |
| Ağ | Sabit public IPv4; yalnız 80/443 public uygulama portu |

## Kalıcılık ve reboot kanıtı

1. Digest-pinned küçük Linux image ile mimari/ağ smoke çalıştır.
2. İsimli test volume'una rastgele marker yazıp SHA-256 al.
3. `restart: unless-stopped` test container'ını oluştur.
4. Kontrollü sunucu reboot'u yap.
5. Docker'ın systemd ile başladığını, test container'ının döndüğünü ve marker checksum'unun değişmediğini doğrula.
6. Test volume'unu production volume'larıyla karıştırma; production üzerinde `down -v` veya prune çalıştırma.

## Uygulama ve kurtarma kanıtı

1. GHCR app/edge imajlarını immutable digest ile çek; `linux/amd64` inspect sonucunu kaydet.
2. `install-marketplacehub.sh` hazırlık doğrulamasını çalıştır.
3. PostgreSQL 18, API, Worker ve Caddy servis durumlarını kaydet; yalnız Caddy 80/443 publish etmelidir.
4. DB, private files, Data Protection key ring ve Caddy volume'larını kapsayan backup seti üret.
5. Dump ve archive checksum'larını boş, izole DB/volume setine restore ederek doğrula.
6. Restore süresini ölç; pilot RPO/RTO ve off-host hedef kaydına bağla.
7. Public HTTPS, HTTP→HTTPS, `/health/ready`, güvenli cookie/header ve kontrollü login smoke yap.

## Sonuç durumu

Sunucu henüz kiralanmadığı için `NOT_RUN / BLOCKED_EXTERNAL`dır. Ubuntu hedefi sağlandığında bu runbook tamamlanmadan production kabulü verilmez; bu durum yerel geliştirmeyi veya F6A fail-closed çekirdeğini durdurmaz.
