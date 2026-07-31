# Fx Faz Planı Şablonu

> Bu şablon yeni bir fazı kendiliğinden etkinleştirmez. Yalnız kullanıcı onayıyla etkin faz için kopyalanır. Yetkili şartnamenin mimari, solution, modül, veri modeli ve faz sırası korunur.

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `Fx` |
| Durum | `DRAFT / READY / IN_PROGRESS / BLOCKED / PASSED` |
| Yetkili şartname ve SHA-256 | |
| Onay kaydı | |

## Hedefler

Ölçülebilir faz sonuçları.

## Kapsam dışı

Sonraki fazlar, alternatif mimari ve doğrulanmamış platform davranışları.

## Gereksinim matrisi

| Kimlik | Kaynak bölümü | Kabul ölçütü | Planlanan kanıt | Dosya/modül | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |

## Dosya etkisi

Oluşturulacak, değiştirilecek ve dokunulmayacak yollar.

## Teknoloji/capability kapıları

Exact sürüm, resmî kaynak, lock/digest, capability supportLevel ve test kanıtı.

## Test ve kanıt planı

| Kanıt kimliği | Komut/senaryo | Beklenen sonuç | Artefakt | Durum |
| --- | --- | --- | --- | --- |

## Dış bağımlılıklar, riskler ve blockerlar

Her kayıt için güvenli fallback, sahibi ve kapanış kanıtı.

## ADR etkisi

Yeni karar değil; mevcut ADR'lerle uyum ve gerekiyorsa açık değişiklik kapısı.

## Çıkış kriterleri

| Kimlik | Ölçülebilir koşul | Kanıt | Durum |
| --- | --- | --- | --- |

## Sonuç

`PASSED` yalnız tüm zorunlu çıkış kriterleri kanıtlıysa; aksi halde `BLOCKED`.
