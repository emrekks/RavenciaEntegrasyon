# F0 Risk Kaydı

| Kimlik | Risk | Etki | Güvenli azaltım | Kapanış kanıtı | Durum |
| --- | --- | --- | --- | --- | --- |
| `RISK-SPEC-001` | Canonical adlı PDF repository kökünde değildi. | Kaynak taşınabilirliği/yanlış kopya | Sağlanan dosya canonical ada byte-for-byte kopyalandı. | Kök PDF: 73 sayfa; SHA-256 `E98365DC34804A478D5DBB41E1997FB6742FD0723A76C08CEE138321F0E2ECA3` | CLOSED |
| `RISK-DR-001` | `PILOT_LOCAL` yedek aynı fiziksel diskte olabilir. | Tek arızada veri+yedek kaybı | Production için şifreli off-host ve restore testi | Off-host checksum ve başarılı restore | OPEN |
| `RISK-CAP-001` | Platform test hesapları/fixture'ları yok. | Yanlış mapping/yazma riski | Tümü `UNKNOWN`; write off; fake adapter | Resmî kaynak + anonim test kanıtı | OPEN |
| `RISK-HOST-001` | Hedef VPS henüz kiralanmadı; runtime/volume özellikleri bilinmiyor. | Deployment/kalıcılık başarısızlığı | Kiralama sonrası runbook'u hedefte uygulamadan production kabul etme | Tarihli runbook çıktısı | OPEN_DEFERRED |
| `RISK-VOLUME-001` | Ürün/sipariş baz hacmi bilinse de varyant, sipariş satırı ve dönemsel pikler henüz ölçülmedi. | Büyümenin baz profili aşması | `1.000` ürün ve `15.000` sipariş/yıl bazına x5 uygula; ikincil metrikleri F1+ gözlemle | x5 yük sonucu ve üretim gözlem kaydı | MITIGATED_MONITOR |
| `RISK-SUPPLY-001` | F1 production manifestleri henüz yok. | Production aktarımında resolved tree/image drift | F0 verification lock ve index digest'leri oluşturuldu; F1 aktarımı fail-closed karşılaştırılacak | F0 lock hash ve registry digest kanıtı | MITIGATED_F0 |
| `RISK-COMPOSE-001` | Compose v2 ile güncel major hattı arasında destek gerilimi olabileceği düşünüldü. | Yanlış major seçimi | Resmî Docker kaynağı v2 ve v5'in birlikte desteklendiğini doğruladı; şartname gereği v2.40.2 exact aday seçildi. | <https://docs.docker.com/compose/intro/history/>; <https://github.com/docker/compose/releases/tag/v2.40.2> | CLOSED |
| `RISK-STITCH-001` | Stitch arayüz dosyası henüz sağlanmadı. | İleri UI görünüm uyumsuzluğu | Markalı fideliği ertele; F1 yerel iskeletini bloklama | Dosya checksum'u ve ilgili faz kabulü | OPEN_NON_BLOCKING |

## Blocker özeti

- `BLOCK-HOST-001`: VPS daha sonra kiralanacak; hedef erişim ve özellik kanıtı yok.
- `BLOCK-DR-001`: `PILOT_LOCAL` ve en fazla 6 saat pilot RPO tanımlı; hedef volume, gerçek restore ve ölçülmüş RTO kanıtı yok.

Bu blockerlar kapanmadan F0 çıkışı `PASSED` yapılamaz.
