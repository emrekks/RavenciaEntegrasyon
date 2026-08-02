# ADR-012: Ubuntu Server 24.04 LTS Container Runtime

- Durum: Accepted - kullanıcı onaylı v3.3 bağlayıcı revizyon
- Tarih: 2026-08-02
- Faz: Dağıtım hedefi değişikliği; ürün fazı açmaz
- Üstün geldiği karar: ADR-009 hedef-host bölümü

## Bağlam

v3.2, Linux container bileşenlerini Windows VPS üzerinde Hyper-V/WSL2/Docker runtime ile çalıştırmayı bağlamıştı. Kullanıcı 2026-08-02 tarihinde hedefi Ubuntu Server 24.04 LTS, 4 vCPU, 8 GB RAM ve 100-120 GB NVMe olarak değiştirdi. Bu değişiklik WSL2/nested virtualization/Docker Desktop katmanlarını kaldırır; uygulama topolojisini değiştirmez.

## Karar

Production hedefi Ubuntu Server 24.04 LTS x86_64 üzerinde doğrudan Docker Engine ve exact Docker Compose v2.40.2'dir. API ve Worker ayrı container/process, PostgreSQL ve private dosyalar kalıcı named volume, Caddy 2.11 public edge olarak kalır. Yalnız Caddy 80/443 yayınlar; API, Worker ve PostgreSQL internal Docker network'tedir.

Başlangıç kapasitesi 4 vCPU, 8 GB RAM ve 100-120 GB NVMe'dir. Sunucuda statik public IPv4 ve production domain bulunur. SSH yönetimi yalnız yönetici IP allow-list veya VPN üzerinden anahtar tabanlı yapılır. DB, private files, Data Protection key ring ve Caddy verisi hedef reboot/restore kanıtına dahildir.

## Güvenlik ve operasyon sonucu

- WSL, Hyper-V, nested virtualization, Docker Desktop ve native Windows container uygulanmaz.
- Application/edge imajları yalnız linux/amd64 ve immutable `name@sha256:...` referanslarıyla çekilir.
- Docker Engine, systemd reboot, restart policy ve named-volume checksum hedefte doğrulanır.
- PostgreSQL dump ile private files/Data Protection birlikte temiz hedefe restore edilir.
- Off-host backup hedefi Ubuntu sunucuyla aynı failure domain'de olamaz.
- Gerçek platform capability ve dış yazma kapıları bu kararla açılmaz.

## Değişiklik kapısı

Başka dağıtım işletim sistemi, container orkestratörü, managed database, farklı ağ topolojisi veya ikinci host ayrı şartname revizyonu ve ADR ister. Kubernetes, mikroservis, cache/broker veya aktif multi-tenant bu kararın kapsamında değildir.
