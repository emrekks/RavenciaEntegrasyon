# Windows VPS Üzerinde Linux Container Doğrulama Runbook'u

## Amaç ve sınır

Bu runbook hedef Windows VPS üzerinde uygulanacaktır. Yerel geliştirme makinesindeki sonuç hedef kanıtı sayılmaz. Komutlar salt-okunur doğrulamayla başlar; volume silen `down -v`, prune veya benzeri işlemler yasaktır.

## Yerel-makine-önce uygulama kararı

Kullanıcı 2026-07-31 tarihinde geliştirme ve ön container doğrulamalarının önce yerel Windows 11 Pro bilgisayarda yapılmasını, Windows VPS'in daha sonra kiralanmasını onayladı. Aşağıdaki doğrulama sırası yerelde `LOCAL_PRECHECK` etiketiyle uygulanır; VPS kiralandığında aynı sıra `TARGET_EVIDENCE` etiketiyle yeniden uygulanır. Yerel başarı target/production kanıtı değildir ve hedefteki nested virtualization, restart, volume veya restore kapısını kapatmaz.

Yerel doğrulama durumu: Windows 11 Pro build `26200.8875`, 64-bit, `31,1 GiB` RAM ve firmware sanallaştırma açık; WSL `2.7.11`, Docker Desktop `4.84.0` ve Linux/amd64 Engine `29.6.2` çalışıyor. Bundled Compose `v5.3.1` yerine checksum doğrulanmış `C:\Users\emrek\AppData\Local\Ravencia\tools\docker-compose-v2.40.2.exe` kullanılmıştır.

## Önkoşul kaydı

| Alan | Hedef değer / kanıt |
| --- | --- |
| VPS sağlayıcı ve plan | UNKNOWN |
| Windows sürüm/build | UNKNOWN |
| CPU/RAM/disk/IOPS | UNKNOWN |
| Sanallaştırma desteği | UNKNOWN |
| Hyper-V/WSL2 uygunluğu | UNKNOWN |
| Docker Engine/Desktop ve lisans/production desteği | UNKNOWN |
| Linux container modu | UNKNOWN |
| Hedef CPU mimarisi | UNKNOWN |
| Volume ve off-host yolları | UNKNOWN |

## Doğrulama sırası

1. `systeminfo`, `Get-ComputerInfo` ve `Get-WindowsOptionalFeature -Online` çıktılarıyla OS/build ve sanallaştırma özelliklerini kaydet.
2. `wsl --status` ve `wsl --version` ile WSL2 durumunu kaydet; kurulum/değişiklik için ayrıca işletim onayı al.
3. `docker version`, `docker info` ve `docker context show` ile client/server, Linux OS type, architecture ve storage driver'ı kanıtla.
4. Şartnamedeki Compose v2 kararı ve exact `2.40.2` binary'sini checksum ile kontrol et. Docker Desktop'ın bundled Compose sürümü farklıysa onu kabul kanıtı sayma; doğrulanmış binary yolunu açıkça kaydet. Başka major veya patch görülürse sessiz upgrade/downgrade yapma.
5. Resmî küçük, digest-pinned Linux image ile geçici smoke container çalıştır; platform/architecture ve ağ erişimini kaydet. Digest seçimi hedef mimari doğrulandıktan sonra sürüm kaydına eklenir.
6. İsimli test volume'una sentetik işaret yaz; container'ı kaldırıp yeniden oluşturarak işaretin kaldığını doğrula. Volume'u silme.
7. Docker/host kontrollü restart sonrasında container restart policy ve volume kalıcılığını doğrula.
8. PostgreSQL 18+ veri volume'unu `/var/lib/postgresql` köküne bağla. Sentetik PostgreSQL veri setini yedekle, checksum al, temiz ve izole hedefe restore et; satır/constraint ve private-file checksum tutarlılığını doğrula. Eski `/var/lib/postgresql/data` mount'u kullanılmaz.
9. Off-host aktarımın şifreli olduğunu, hedefin ayrı failure domain'de bulunduğunu ve erişim/rotasyon politikasını kanıtla.
10. Normal ve pik x5 senaryoda CPU/RAM/disk/queue sürelerini ölç; sonuçları recovery profiline bağla.

## Kanıt paketi

Tarih/saat ve uygulayan kişi, redacted komut çıktıları, exact runtime/Compose/image digest'leri, restart sonucu, volume işareti checksum'u, backup/restore checksum'u, ölçülmüş RPO/RTO ve hata halinde rollback kaydı. Secret veya PII kanıta girmez.

## Mevcut sonuç

Yerel runbook `PASSED_LOCAL_PRECHECK` durumundadır: WSL2, Linux/amd64 engine, exact Compose v2.40.2, digest-pinned Caddy smoke, Docker/WSL restart, named-volume checksum kalıcılığı ve PostgreSQL 18.4 dump/restore geçmiştir. Hedef VPS erişimi ve özellikleri sağlanmadığından hedef runbook `NOT_RUN`, `F0-EXIT-003` ise `BLOCKED_EXTERNAL`dır. Bu hedef blocker yerel geliştirmeyi durdurmaz; production kabulünü durdurur.
