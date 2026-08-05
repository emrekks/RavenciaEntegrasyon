# Güncel Risk Kaydı

| Kimlik | Öncelik | Risk | Etki | Çözüm / kapı | Durum |
| --- | --- | --- | --- | --- | --- |
| `RISK-PKG-001` | P0 | Teslim arşivinde `.git`, build çıktıları, PostgreSQL data/WAL ve geçici dosyalar bulunuyordu | Secret/veri sızıntısı, 250 MB gereksiz paket, bozuk restore | Kaynak-only paket, ignore kuralları ve `verify-repository-cleanliness.py` | MITIGATED_IN_CLEAN_COPY |
| `RISK-SCOPE-001` | P0 | Kod/UI/Worker/doküman birden fazla eski platformu aktif sayıyordu | Yanlış iş oluşturma ve dağınık geliştirme | ADR-016; yalnız iki platform; eski adapter ve aktif faz dosyaları kaldırıldı | CLOSED_IN_CLEAN_COPY |
| `RISK-WRITE-001` | P0 | Dış yazmalar tam E2E kanıtı olmadan veri bozabilir | Yanlış ürün, stok, fiyat veya fatura | Global + connection write anahtarları varsayılan kapalı; capability gate | OPEN_UNTIL_STAGE_E2E |
| `RISK-INVENTORY-001` | P1 | Yerel stok ve fiyat portları ayrı, Trendyol uzak sözleşmesi birleşik | Kısmi güncelleme ve tutarsız fiyat/stok | Tek `PriceInventoryBatch` komutu, tek dedup key, satır bazlı batch sonucu | OPEN_IMPLEMENTATION |
| `RISK-PUBLISH-001` | P1 | Ürün create akışı dinamik/Stage kanıtı ve onay reconciliation olmadan tamamlanmış sayılabilir | Yanlış canlılık yorumu veya görünmeyen satır hatası | Durable create job + batch poll + satır durumu kodlandı; approved-products reconciliation, operatör UI ve Stage kanıtı zorunlu | PARTIALLY_CLOSED_DYNAMIC_STAGE_UI_PENDING |
| `RISK-UPsert-001` | P1 | Create ve update aynı sözleşme gibi ele alınırsa güncelleme duplicate create üretebilir | Güncelleme isteği duplicate/create olabilir | Create komutu `CreateAsync` olarak ayrıldı; ProductUpdate ve uzak archive ayrı capability/iş olarak uygulanmalı | PARTIALLY_CLOSED_CREATE_ONLY |
| `RISK-EFAT-STATUS-001` | P1 | E-Faturam status/cancel/taxpayer çağrıları kapalı | Nihai belge durumu bilinmez; operasyon yarım kalır | Endpoint contract + test firma + polling/reconciliation | OPEN_EXTERNAL_AND_IMPLEMENTATION |
| `RISK-DELIVERY-001` | P1 | Trendyol invoice-link POST başarısı nihai `DELIVERED` olarak yorumlanıyor | Uzak kabul tamamlanmadan yerel başarı | `SUBMITTED` ara durumu; query/reconciliation veya doğrulanmış idempotent tekrar | OPEN_IMPLEMENTATION |
| `RISK-HEALTH-001` | P1 | API container healthcheck yalnız proses varlığını kontrol ediyordu | DB hazır değilken Caddy trafiği açabilir | Healthcheck `/health/ready` HTTP probuna çevrildi | CLOSED_IN_CLEAN_COPY |
| `RISK-HOST-001` | P1 | Production AllowedHosts yalnız localhost olabilirdi | Gerçek domain istekleri 400 dönebilir | Production `MARKETPLACEHUB_ALLOWED_HOSTS` zorunlu | CLOSED_CONFIGURATION |
| `RISK-DEPLOY-CONFIG-001` | P1 | Initializer zorunlu AllowedHosts değerini üretmiyor; `.env.example` Compose değişkenleriyle eşleşmiyordu | Deployment ön-kontrolü fail veya local ayarların etkisiz kalması | AllowedHosts origin hostundan türetildi; Compose env sözleşmesi ve örnek dosya düzeltildi | CLOSED_IN_CLEAN_COPY |
| `RISK-EFAT-DOCUMENT-001` | P1 | Provider permanent URL doğrudan indiriliyor; host/PDF imza doğrulaması yok | SSRF benzeri erişim veya hatalı içeriğin belge diye saklanması | Exact host allow-list, HTTPS/redirect/IP guard, PDF magic-byte kontrolü; capability kapalı | OPEN_IMPLEMENTATION_AND_STAGE_EVIDENCE |
| `RISK-INVOICE-LINK-RETENTION-001` | P1 | Trendyol’a verilen fatura URL’sinin 8 yıl erişilebilirliği kanıtlı değil | Sonradan erişilemeyen yasal belge bağlantısı | Provider retention sözleşmesi, periyodik probe, checksum/private kopya ve alarm | OPEN_EXTERNAL_AND_OPERATIONS |
| `RISK-DR-001` | P1 | Off-host backup/restore kanıtı yok | Tek host kaybında veri kaybı | Şifreli uzak kopya ve temiz volume restore | OPEN_EXTERNAL |
| `RISK-OBS-001` | P2 | Capability ve dış işlem metriği sınırlı | Arıza ve rate-limit geç fark edilir | Structured metrics, job lag, retry/dead-letter, remote latency dashboard | OPEN |
| `RISK-TEST-001` | P2 | İnceleme ortamında .NET/Docker yok; Node sürümü düşük ve npm mirror bir lock paketini döndürmedi | Backend/web/container değişikliklerinin tam dinamik doğrulaması burada yapılamadı | Pinli CI tam restore/build/test/format/image/smoke çalıştırmalı | OPEN_VALIDATION |

Production kabulünde açık P0/P1 risk kalmamalıdır.
