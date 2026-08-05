# Güncel Risk Kaydı

| Kimlik | Öncelik | Risk | Etki | Çözüm / kapı | Durum |
| --- | --- | --- | --- | --- | --- |
| `RISK-PKG-001` | P0 | Teslim arşivinde `.git`, build çıktıları, PostgreSQL data/WAL ve geçici dosyalar bulunuyordu | Secret/veri sızıntısı, 250 MB gereksiz paket, bozuk restore | Kaynak-only paket, ignore kuralları ve `verify-repository-cleanliness.py` | MITIGATED_IN_CLEAN_COPY |
| `RISK-SCOPE-001` | P0 | Kod/UI/Worker/doküman birden fazla eski platformu aktif sayıyordu | Yanlış iş oluşturma ve dağınık geliştirme | ADR-016; yalnız iki platform; eski adapter ve aktif faz dosyaları kaldırıldı | CLOSED_IN_CLEAN_COPY |
| `RISK-WRITE-001` | P0 | Dış yazmalar tam E2E kanıtı olmadan veri bozabilir | Yanlış ürün, stok, fiyat veya fatura | Global + connection write anahtarları varsayılan kapalı; capability gate | OPEN_UNTIL_STAGE_E2E |
| `RISK-INVENTORY-001` | P1 | Birleşik fiyat-stok sonucu daha yeni yerel sürümü ezebilir | Kısmi veya eski fiyat/stok kanıtı | Tek batch, offer/price/projection version snapshot ve stale-result guard kodlandı | MITIGATED_CODE_STAGE_VALIDATION_PENDING |
| `RISK-PUBLISH-001` | P1 | Ürün create/update/archive dinamik/Stage kanıtı olmadan production tamamlanmış sayılabilir | Duplicate ürün, eski payload veya yanlış canlılık yorumu | Ayrı durable state machine, external-effect fence, approval read-back ve operatör UI kodlandı; exact runtime/Stage kanıtı zorunlu | MITIGATED_CODE_DYNAMIC_STAGE_PENDING |
| `RISK-UPsert-001` | P1 | Create, update ve archive aynı sözleşmeye yönlenebilir | Duplicate create veya yanlış arşiv durumu | Ayrı create/update/archive portları, job türleri ve endpointleri uygulandı | CLOSED_CODE_STAGE_REVALIDATION_REQUIRED |
| `RISK-TY-ACTION-001` | P1 | Kanıtsız shipment/return aksiyonu yanlış paketi veya claimi değiştirebilir | Uzak operasyon bozulması | Capability constraints, ETag, idempotency, external-effect fence ve exact read-back kodlandı | MITIGATED_CODE_STAGE_VALIDATION_PENDING |
| `RISK-EFAT-STATUS-001` | P1 | Giden E-Fatura UUID status yolu public sözleşmede kesin değil | E-Fatura nihai durumu yanlış endpoint ile sorgulanabilir | E-Arşiv status/cancel/taxpayer kodlandı; E-Fatura yolu yalnız exact Stage/SIT evidence ile config edilir | MITIGATED_CODE_EXTERNAL_ENDPOINT_PENDING |
| `RISK-DELIVERY-001` | P1 | Trendyol invoice-link POST başarısı kesin terminal kabul kanıtı sağlamayabilir | Uzak kabul tamamlanmadan yerel başarı | `SUBMITTED` ara durumu, güvenli read-back; query kanıtı yoksa `MANUAL_REVIEW` | MITIGATED_CODE_EXTERNAL_CONFIRMATION_PENDING |
| `RISK-HEALTH-001` | P1 | API container healthcheck yalnız proses varlığını kontrol ediyordu | DB hazır değilken Caddy trafiği açabilir | Healthcheck `/health/ready` HTTP probuna çevrildi | CLOSED_IN_CLEAN_COPY |
| `RISK-HOST-001` | P1 | Production AllowedHosts yalnız localhost olabilirdi | Gerçek domain istekleri 400 dönebilir | Production `MARKETPLACEHUB_ALLOWED_HOSTS` zorunlu | CLOSED_CONFIGURATION |
| `RISK-DEPLOY-CONFIG-001` | P1 | Initializer zorunlu AllowedHosts değerini üretmiyor; `.env.example` Compose değişkenleriyle eşleşmiyordu | Deployment ön-kontrolü fail veya local ayarların etkisiz kalması | AllowedHosts origin hostundan türetildi; Compose env sözleşmesi ve örnek dosya düzeltildi | CLOSED_IN_CLEAN_COPY |
| `RISK-EFAT-DOCUMENT-001` | P1 | Provider permanent URL yanlış host/içerik döndürebilir | SSRF benzeri erişim veya hatalı içeriğin belge diye saklanması | Exact host allow-list, HTTPS/redirect/IP, boyut, MIME ve PDF magic-byte guardları kodlandı; Stage PDF kanıtı bekler | MITIGATED_CODE_STAGE_EVIDENCE_PENDING |
| `RISK-INVOICE-LINK-RETENTION-001` | P1 | Trendyol’a verilen fatura URL’sinin 8 yıl erişilebilirliği kanıtlı değil | Sonradan erişilemeyen yasal belge bağlantısı | Provider retention sözleşmesi, periyodik probe, checksum/private kopya ve alarm | OPEN_EXTERNAL_AND_OPERATIONS |
| `RISK-DR-001` | P1 | Off-host backup/restore kanıtı yok | Tek host kaybında veri kaybı | Şifreli uzak kopya ve temiz volume restore | OPEN_EXTERNAL |
| `RISK-OBS-001` | P2 | Capability ve dış işlem metriği sınırlı | Arıza ve rate-limit geç fark edilir | Structured metrics, job lag, retry/dead-letter, remote latency dashboard | OPEN |
| `RISK-TEST-001` | P2 | İnceleme ortamında .NET/Docker yok; Node sürümü düşük ve npm mirror bir lock paketini döndürmedi | Backend/web/container değişikliklerinin tam dinamik doğrulaması burada yapılamadı | Pinli CI tam restore/build/test/format/image/smoke çalıştırmalı | OPEN_VALIDATION |

Production kabulünde açık P0/P1 risk kalmamalıdır.
