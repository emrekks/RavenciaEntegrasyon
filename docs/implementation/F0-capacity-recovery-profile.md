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
| Hedef host | Mevcut AWS Ubuntu Server 26.04 LTS x86_64; ölçülen 2 vCPU, 8.153.141.248 byte RAM ve 80.530.636.800 byte NVMe aygıt. | HOST_PROFILE_VERIFIED_RUNTIME_PENDING |
| PostgreSQL dump | Pilot varsayımı: 6 saatte bir, 7 günlük saklama. Yerel sentetik PostgreSQL 18.4 dump checksum'u doğrulandı. | LOCAL_TESTED_TARGET_PENDING |
| Haftalık / aylık | Pilot varsayımı: 4 haftalık ve 3 aylık kopya. | TARGET_NOT_TESTED |
| Private app files | Günlük yedek. | TARGET_NOT_TESTED |
| RPO | 6 saatlik dump programına göre pilot hedefi en fazla 6 saat; hedef operasyon programı bekliyor. | DEFINED_TARGET_NOT_TESTED |
| RTO | Yerel sentetik DB restore ölçümü `0,147 sn`; bu değer hedef Ubuntu Server veya iş RTO'su değildir. | LOCAL_MEASURED_TARGET_BLOCKED |
| Off-host | `PILOT_LOCAL` için zorunlu değil ve tam DR sayılmaz; `PRODUCTION_RESILIENT` seçilirse şifreli, ayrı failure domain zorunlu. | NOT_APPLICABLE_PILOT |
| Restore testi | PostgreSQL sentetik veri ayrı temiz volume'a restore edildi ve mantıksal checksum eşleşti; private app files ve hedef host bekliyor. | DB_LOCAL_PASSED_TARGET_PENDING |
| Hedef volume kalıcılığı | Digest-pinned ayrı test container/volume kontrollü AWS reboot sonrası otomatik başladı; marker SHA-256 değişmedi; test kaynakları temizlendi. | TARGET_PASSED |

`PILOT_LOCAL` yedekleri aynı fiziksel diskteyse `RISK-DR-001` açıktır; yerel başarılı restore üretim dayanıklılığı veya hedef RTO kanıtı değildir.

## Çıkış kanıtı

Mevcut AWS sunucusunda OS, mimari, 2 vCPU, 8 GB sınıfı RAM, 80 GB NVMe aygıt ve root filesystem kapasitesi ölçülmüştür. Direct Docker Engine/Compose, systemd yeniden başlatma ve kalıcı volume checksum kanıtı tamamlanmıştır. Production PostgreSQL/private-file/Data Protection restore seti `20260802T223309Z` ile `3 sn` içinde doğrulanmıştır. İşletme sahibi 2026-08-03 tarihinde x5 ölçümünü ek yük testi çalıştırılmadan kabul edilmiş saydı; durum `ACCEPTED_OWNER_WAIVER`, ölçüm sonucu ise `NOT_MEASURED` olarak korunur. Production staging tek host/disk üzerinde kaldığı için onaylı şifreli off-host kopya hâlâ zorunludur.
