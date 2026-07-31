# Windows VPS Üzerinde Linux Container Doğrulama Runbook'u

## Amaç ve sınır

Bu runbook hedef Windows VPS üzerinde uygulanacaktır. Yerel geliştirme makinesindeki sonuç hedef kanıtı sayılmaz. Komutlar salt-okunur doğrulamayla başlar; volume silen `down -v`, prune veya benzeri işlemler yasaktır.

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
4. `docker compose version` ile şartnamedeki Compose v2 kararını ve exact aday `2.40.2` sürümünü kontrol et. Başka major veya patch görülürse sessiz upgrade/downgrade yapma; hedef provenance/checksum kaydını incele.
5. Resmî küçük, digest-pinned Linux image ile geçici smoke container çalıştır; platform/architecture ve ağ erişimini kaydet. Digest seçimi hedef mimari doğrulandıktan sonra sürüm kaydına eklenir.
6. İsimli test volume'una sentetik işaret yaz; container'ı kaldırıp yeniden oluşturarak işaretin kaldığını doğrula. Volume'u silme.
7. Docker/host kontrollü restart sonrasında container restart policy ve volume kalıcılığını doğrula.
8. Sentetik PostgreSQL veri setini yedekle, checksum al, temiz ve izole hedefe restore et; satır/constraint ve private-file checksum tutarlılığını doğrula.
9. Off-host aktarımın şifreli olduğunu, hedefin ayrı failure domain'de bulunduğunu ve erişim/rotasyon politikasını kanıtla.
10. Normal ve pik x5 senaryoda CPU/RAM/disk/queue sürelerini ölç; sonuçları recovery profiline bağla.

## Kanıt paketi

Tarih/saat ve uygulayan kişi, redacted komut çıktıları, exact runtime/Compose/image digest'leri, restart sonucu, volume işareti checksum'u, backup/restore checksum'u, ölçülmüş RPO/RTO ve hata halinde rollback kaydı. Secret veya PII kanıta girmez.

## Mevcut sonuç

Hedef VPS erişimi ve özellikleri sağlanmadığından runbook `NOT_RUN`, `F0-EXIT-003` ise `BLOCKED_EXTERNAL`dır.
