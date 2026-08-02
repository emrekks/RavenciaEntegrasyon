# ADR-013: AWS Ubuntu 26.04 Mevcut Host Profili

- Durum: Accepted - kullanıcı onaylı v3.4 bağlayıcı revizyon
- Tarih: 2026-08-02
- Faz: Dağıtım hedefi değişikliği; ürün fazı açmaz
- Üstün geldiği karar: ADR-012 içindeki OS/CPU/disk hedefleri

## Bağlam

AWS EC2 hedefinde salt-okunur ölçüm; Ubuntu Server 26.04 LTS (Resolute) x86_64, 2 vCPU, 8.153.141.248 byte RAM ve 80.530.636.800 byte NVMe aygıtı göstermiştir. Kullanıcı bu mevcut sunucunun bağlayıcı hedef olarak kabul edilmesini istemiştir. Docker'ın resmî Ubuntu kurulum belgesi Resolute 26.04 LTS ve x86_64 hattını desteklenen hedef olarak listeler.

## Karar

Production başlangıç hostu Ubuntu Server 26.04 LTS x86_64, 2 vCPU, 8 GB sınıfı RAM ve 80 GB NVMe sınıfıdır. Docker Engine doğrudan Docker'ın resmî Ubuntu apt repository'sinden kurulur; exact Engine/CLI/containerd sürümü kurulum kanıtında kaydedilir. Exact Docker Compose v2.40.2 Linux x86_64 checksum kapısı korunur.

Bu karar yalnız host profilini değiştirir. Modüler monolit, API/Worker ayrımı, tek PostgreSQL, Caddy edge, internal network, named volume, immutable image digest, backup/restore, capability ve faz kapıları değişmez.

## Kapasite ve operasyon sonucu

- 1.000 ürün, 15.000 sipariş/yıl ve x5 test profili değişmez.
- 2 vCPU kabulü x5 performans kanıtı değildir; ölçüm production kabul kapısında kalır.
- 80 GB disk için doluluk ölçümü, backup staging temizliği ve off-host kopya production operasyon kanıtına dahildir.
- Swap bulunmaması tek başına blocker değildir; memory pressure ölçülmeden swap davranışı uydurulmaz.
- Gerçek platform capability'leri `UNKNOWN`, bütün dış write anahtarları kapalı kalır.

## Değişiklik kapısı

Host kapasitesi ölçülmüş x5 yük, disk büyümesi veya restore hedefini karşılamazsa aynı mimari korunarak AWS instance/volume büyütme kararı kullanıcıya sunulur. Başka işletim sistemi, orkestratör, managed database veya topoloji ayrı şartname revizyonu ve ADR ister.
