# F0 Kapasite ve Kurtarma Profili

## Kapasite girdileri

Baz değerler işletme sahibinin 2026-07-31 tarihli beyanıdır. x5 değerleri şartname gereği matematiksel test hedefidir; platform limiti veya production taahhüdü değildir. Ürün sayısı SKU/varyant sayısı olarak yorumlanmaz.

| Girdi | Normal baz | Pik x5 test hedefi | Durum |
| --- | --- | --- | --- |
| Ürün sayısı | `1.000` | `5.000` | USER_CONFIRMED |
| Yıllık sipariş | `15.000` | `75.000` yıllık eşdeğer | USER_CONFIRMED |
| Aylık ortalama sipariş | `1.250` | `6.250` | DERIVED |
| Günlük ortalama sipariş | `41,1` | `205,5`; test girdisinde yukarı yuvarlanmış `206` | DERIVED |
| SKU ve varyant sayısı | UNKNOWN; ürün sayısına eşit varsayılmaz | Baz ölçüldüğünde x5 | MONITOR_INPUT |
| Sipariş satırı sayısı | UNKNOWN; sipariş sayısına eşit varsayılmaz | Baz ölçüldüğünde x5 | MONITOR_INPUT |
| Saatlik stok/fiyat değişimi | UNKNOWN | Baz ölçüldüğünde x5 | MONITOR_INPUT |
| Günlük iade/fatura/dosya hacmi | UNKNOWN | Baz ölçüldüğünde x5 | MONITOR_INPUT |
| Platform başına webhook/poll trafiği | UNKNOWN | Baz ölçüldüğünde x5 | MONITOR_INPUT |
| Veri tabanı ve private dosya büyümesi | UNKNOWN | Baz ölçüldüğünde x5 | MONITOR_INPUT |

`BLOCK-CAPACITY-001`, F0 çekirdek hacim tabanı ve x5 profilinin sağlanmasıyla kapanmıştır. Bilinmeyen ikincil metrikler F1 gözlem/yük testi girdisidir; bunlara platform limiti veya kapasite taahhüdü atanmaz.

## Kurtarma profili

| Alan | F0 kararı | Durum |
| --- | --- | --- |
| `BACKUP_PROFILE` | F0 başlangıç profili `PILOT_LOCAL`; production resilient geçişi ayrı kapıdır. | SELECTED_FOR_PILOT |
| PostgreSQL dump | Pilot varsayımı: 6 saatte bir, 7 günlük saklama. | TARGET_NOT_TESTED |
| Haftalık / aylık | Pilot varsayımı: 4 haftalık ve 3 aylık kopya. | TARGET_NOT_TESTED |
| Private app files | Günlük yedek. | TARGET_NOT_TESTED |
| RPO | 6 saatlik dump programına göre pilot hedefi en fazla 6 saat; restore/operasyon testi bekliyor. | DEFINED_NOT_TESTED |
| RTO | Restore ölçümü olmadan sayı verilmez. | BLOCKED_EXTERNAL |
| Off-host | `PILOT_LOCAL` için zorunlu değil ve tam DR sayılmaz; `PRODUCTION_RESILIENT` seçilirse şifreli, ayrı failure domain zorunlu. | NOT_APPLICABLE_PILOT |
| Restore testi | Veri tabanı + private dosyalarla temiz ortama geri dönüş ve tutarlılık kontrolü. | NOT_RUN |

`PILOT_LOCAL` yedekleri aynı fiziksel diskteyse `RISK-DR-001` açıktır; bunlar üretim dayanıklılığı kanıtı değildir.

## Çıkış kanıtı

Hedef VPS kiralandıktan sonra CPU/RAM/disk/IOPS, container runtime ve volume yolları kaydedilmelidir. F1+ yük testinde yukarıdaki x5 profilinin sonucu; recovery testinde dump checksum'u ve ölçülmüş restore süresi kanıtlanmalıdır. Off-host kanıtı yalnız `PRODUCTION_RESILIENT` seçilirse zorunludur.
