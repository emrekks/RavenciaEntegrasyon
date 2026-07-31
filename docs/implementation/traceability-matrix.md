# F0 İzlenebilirlik Matrisi

## Kural

Bu matris yalnız F0 dokümantasyon teslimatlarını izler. Gelecek kod ve test yolları bilerek `F1+ / henüz yok` olarak gösterilir; F0 sırasında production artefaktı oluşturulmaz. Durumlar: `DONE`, `BLOCKED_EXTERNAL`, `PENDING_F1`.

| Kimlik | Faz | Kabul ölçütü | F0 kanıtı | Gelecek kod/test | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| `F0-REQ-001` | F0 | Gereksinim-faz-kabul-kaynak-durum bağı kuruludur. | Bu matris; `Fx-plan-template.md` | F1+ / henüz yok | Yok | DONE |
| `F0-REQ-002` | F0 | ADR-001–010 karar, sonuç ve değişiklik kapısı içerir. | `docs/adr/ADR-001`–`ADR-010` | F1+ / henüz yok | Yok | DONE |
| `F0-REQ-003` | F0 | Platform sırası değişmeden kaydedilmiştir. | `F0-external-dependencies.md`; capability matrisi | Adapterlar F4+ | Test hesapları | DONE |
| `F0-REQ-004` | F0 | Her platform/capability kanıt alanlarıyla kayıtlıdır. | `docs/platform-rules/capability-matrix.md` | Adapter testleri F4+ | Resmî kaynak ve test hesabı | DONE |
| `F0-REQ-005` | F0 | Güvenli iş otoriteleri açık ve çelişkisizdir. | `F0-business-authorities.md`; ADR-006 | Domain uygulaması F2+ | Yok | DONE |
| `F0-REQ-006` | F0 | Hacim, pik x5, RPO/RTO ve backup profili kayıtlıdır. | `F0-capacity-recovery-profile.md`; ADR-010 | Load/restore testleri F1+ | Hacim baz/x5 tamamlandı; hedef restore ve RTO bekliyor | BLOCKED_EXTERNAL |
| `F0-REQ-007` | F0 | Environment/secret, threat, risk, kill switch ve rollback kayıtlıdır. | İlgili beş F0 belgesi; ADR-007 | Uygulama kontrolleri F1+ | Secret store ve hedef ortam | DONE |
| `F0-REQ-008` | F0 | Fake adapter/anonim fixture standardı tanımlıdır. | `fake-adapter-fixture-standard.md` | Fixture/test uygulaması F1+ | Test hesabı fixture'ları | DONE |
| `F0-REQ-009` | F0 | Hedef Windows VPS Linux container kanıtı vardır. | `windows-vps-runtime-validation.md` | Dağıtım F1+ | VPS erişimi/özellikleri | BLOCKED_EXTERNAL |
| `F0-REQ-010` | F0 | Stitch ileri tarihli, engelleyici olmayan bağımlılıktır. | `F0-external-dependencies.md` | UI uygulaması ilgili faz | Stitch dosyası | DONE |
| `F0-REQ-011` | F0 | Exact sürüm, resmî kaynak, tarih, lock ve digest kayıtlıdır. | `verified-versions.md`; F0 verification lock/digest kanıtları | Production lock/image F1 | Hedef child digest host runbook'una bağlı | DONE_F0 |
| `F0-VAL-001` | F0 | Her gereksinim tek faz ve ölçülebilir kabule bağlıdır. | Bu matris | Yok | Yok | DONE |
| `F0-VAL-002` | F0 | Kanıtsız capability `UNKNOWN`; uydurma sözleşme yoktur. | Capability matrisi incelemesi | Adapter sözleşmeleri F4+ | Test hesapları | DONE |
| `F0-VAL-003` | F0 | Fixture standardı secret/PII yasaklar. | Fixture standardı | Tarama testi F1+ | Fixture erişimi | DONE |
| `F0-VAL-004` | F0 | ADR'ler şartname ve birbirleriyle çelişmez. | ADR karar özeti ve çapraz bağlantılar | Yok | Yok | DONE |
| `F0-VAL-005` | F0 | F1 gerçek platform secret'ı olmadan başlayabilir. | Fake adapter standardı; tüm write anahtarları kapalı | Fake adapter F1 | Yok | DONE |
| `F0-VAL-006` | F0 | Exact sürüm + kaynak + lock/digest eksiksizdir. | F0 locked restore/dry-run, index digest ve Compose checksum kanıtı | Production aktarımı F1 | Hedef child digest host runbook'una bağlı | DONE_F0 |
| `F0-EXIT-001` | F0 | F1'i durduran mimari belirsizlik yoktur. | ADR-001–010 | Yok | Kullanıcı kabulü | DONE |
| `F0-EXIT-002` | F0 | Dış bağımlılık, blocker ve güvenli fallback kayıtlıdır. | Dependency/risk kayıtları | Yok | Dış sağlayıcılar | DONE |
| `F0-EXIT-003` | F0 | Runtime, volume ve backup uygulanabilirliği hedefte kanıtlıdır. | Runbook ve recovery profilinde kanıt yuvaları | Runtime testleri F1 öncesi | Hedef VPS | BLOCKED_EXTERNAL |
| `F0-EXIT-004` | F0 | Sürüm belgesi commitli; lock/digest tutarlıdır. | F0 verification lock/digest seti ve kullanıcı onaylı faz sınırı kararı | Production aktarımı F1 | Git commit | READY_TO_COMMIT |

## F0 sonucu

Dokümantasyon ve dependency kanıt kapsamı tamamlanmıştır. `F0-REQ-006`, `F0-REQ-009` ve `F0-EXIT-003` hedef VPS/restore kanıtı nedeniyle açıktır; F0 çıkışı `BLOCKED`dır.
