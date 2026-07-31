# F0 Risk Kaydı

| Kimlik | Risk | Etki | Güvenli azaltım | Kapanış kanıtı | Durum |
| --- | --- | --- | --- | --- | --- |
| `RISK-SPEC-001` | Canonical adlı PDF repository kökünde değildi. | Kaynak taşınabilirliği/yanlış kopya | Sağlanan dosya canonical ada byte-for-byte kopyalandı. | Kök PDF: 73 sayfa; SHA-256 `E98365DC34804A478D5DBB41E1997FB6742FD0723A76C08CEE138321F0E2ECA3` | CLOSED |
| `RISK-DR-001` | `PILOT_LOCAL` yedek aynı fiziksel diskte olabilir. | Tek arızada veri+yedek kaybı | Yerel dump/restore kanıtı tamamlandı; production için hedef-host restore ve seçilirse şifreli off-host gerekir | Yerel restore geçti; hedef/off-host kanıtı bekliyor | OPEN_TARGET_ONLY |
| `RISK-CAP-001` | Platform test hesapları/fixture'ları yok. | Yanlış mapping/yazma riski | Tümü `UNKNOWN`; write off; fake adapter | Resmî kaynak + anonim test kanıtı | OPEN |
| `RISK-HOST-001` | Hedef VPS henüz kiralanmadı; runtime/volume özellikleri bilinmiyor. | Deployment/kalıcılık başarısızlığı | Kullanıcı onaylı yerel-makine-önce doğrulamasını uygula; kiralama sonrası runbook'u hedefte tekrarlamadan production kabul etme | Yerel ön kanıt + tarihli hedef runbook çıktısı | OPEN_DEFERRED_NONBLOCKING_LOCAL |
| `RISK-LOCAL-RUNTIME-001` | Yerel WSL2 Windows özellikleri ilk restart sonrasında uygulama paketi eksikliği nedeniyle çalışmadı. | Yerel Linux-container doğrulaması başlayamadı | Microsoft WSL `2.7.11` paketi doğrulandı; engine, Linux image, restart ve volume testleri çalıştırıldı | Tarihli yerel runtime, restart ve volume checksum kanıtı | CLOSED |
| `RISK-PG18-MOUNT-001` | PostgreSQL 18 resmî imajı eski `/var/lib/postgresql/data` volume mount'unu fail-closed reddetti. | Veritabanı container'ı başlatılamaz | 18+ için volume `/var/lib/postgresql` köküne bağlandı; yeni izole volume'da dump/restore geçti | PostgreSQL 18.4 source/restore container'ları ve eşit mantıksal checksum | CLOSED |
| `RISK-VOLUME-001` | Ürün/sipariş baz hacmi bilinse de varyant, sipariş satırı ve dönemsel pikler henüz ölçülmedi. | Büyümenin baz profili aşması | `1.000` ürün ve `15.000` sipariş/yıl bazına x5 uygula; ikincil metrikleri F1+ gözlemle | x5 yük sonucu ve üretim gözlem kaydı | MITIGATED_MONITOR |
| `RISK-SUPPLY-001` | F1 production manifestleri henüz yok. | Production aktarımında resolved tree/image drift | F0 verification lock ve index digest'leri oluşturuldu; F1 aktarımı fail-closed karşılaştırılacak | F0 lock hash ve registry digest kanıtı | MITIGATED_F0 |
| `RISK-COMPOSE-001` | Docker Desktop `4.84.0` bundled Compose `v5.3.1` getiriyor ve başlangıçta kullanıcı plugin konumunu yeniden yazıyor; şartname v2 hattını sabitliyor. | Yanlış major seçimi ve yerel/hedef drift | Bundled v5 kabul kanıtı sayılmadı; resmî Windows x86_64 `v2.40.2` binary'si kayıtlı SHA-256 ile doğrulanıp Ravencia'ya ayrılmış sabit araç konumuna kuruldu. Hedef VPS'te yeniden doğrulanır. | `C:\Users\emrek\AppData\Local\Ravencia\tools\docker-compose-v2.40.2.exe version`; kayıtlı SHA-256 eşit | MITIGATED_LOCAL |
| `RISK-STITCH-001` | Stitch arayüz dosyası başlangıçta sağlanmamıştı. | İleri UI görünüm uyumsuzluğu | Dosya faz filtresiyle incelendi; yalnız F3 Teal Precision token/layout girdisi kullanıldı. | ZIP SHA-256 `3B51EBF78D7653933451E2B41D627A5281E14298844F7B7AFFAFC0B8198CE0A9`; F3 route guard | CLOSED_F3 |
| `RISK-F4-FISCAL-001` | Rounding, due, trigger, package scope ve adjustment mali otoriteleri onaylanmadı. | Hatalı veya mükerrer mali belge | Policy yalnız `UNAPPROVED`; invoice type `UNDETERMINED`; auto-submit ve dış write kapalı | ADR-011 Accepted + mali onay + test firma E2E | OPEN_BLOCKING_EXTERNAL |
| `RISK-F4-PROVIDER-001` | E-Faturam hesap modeli/test firma ve Trendyol Stage invoice delivery kanıtı yok. | Yanlış firma scope'u, contract veya delivery | Sign-in dışındaki provider işlemleri ve public-link delivery fail-closed; capability `UNKNOWN` | Anonim fixture + tarihli Stage/SIT kanıtı | OPEN_BLOCKING_EXTERNAL |
| `RISK-F4-PRIVACY-001` | Mali/KVKK retention ve hedef backup erişimi kararlaştırılmadı. | Belge/PII sızıntısı veya mevzuata aykırı saklama | Protected snapshot, private immutable file, no-store; hard delete yok | Onaylı retention + hedef restore/access testi | OPEN_BLOCKING_EXTERNAL |

## Blocker özeti

- `BLOCK-HOST-001`: VPS daha sonra kiralanacak; hedef erişim ve özellik kanıtı yok. Yerel geliştirmeyi durdurmaz, production kabulünü durdurur.
- `BLOCK-DR-001`: Yerel sentetik restore ve süre ölçümü tamamlandı; hedef VPS volume/restore ve hedef RTO kanıtı henüz yok.

Bu blockerlar kapanmadan F0 çıkışı `PASSED` yapılamaz.
