# ADR-010: Backup, RPO, RTO ve Restore

- Durum: Accepted; yerel DB restore kanıtı tamamlandı, hedef kanıtı bekliyor
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Backup sıklığı tek başına veri kaybı ve hizmete dönüş hedefini kanıtlamaz; veri tabanı ve private dosyaların birlikte restore edilmesi gerekir.

## Karar

`BACKUP_PROFILE` başlangıçta `PILOT_LOCAL`dır. Pilot varsayımı PostgreSQL dump'ı 6 saatte bir/7 gün, haftalık 4, aylık 3 ve private app files günlük yedektir. Aynı fiziksel diskteki kopya `RISK-DR-001` taşır. Production dayanıklılığı için `PRODUCTION_RESILIENT`, şifreli off-host ayrı failure domain ve ölçülmüş restore zorunludur.

RPO için pilot hedefi en fazla 6 saattir; kesin iş RPO/RTO'su kullanıcı girdisi ve restore testi olmadan ilan edilmez.

## Sonuçlar

Backup varlığı restore başarısı sayılmaz. PostgreSQL ve private dosyalar uyumlu recovery point'e alınır; checksum ve iş/veri bütünlüğü test edilir.

Yerel ön doğrulamada digest-pinned PostgreSQL 18.4 ile sentetik dump ayrı temiz volume'a restore edilmiş; dump SHA-256 `51a6a9df0065b7e346e137cac77aa6208e7989b380fe885d7282a7f5c165fd3f`, source/restore mantıksal kanıtı `2|fb4200bade7730f8239ef795f97ee6fc` ve ölçülen restore süresi `0,147 sn` olmuştur. Bu sentetik yerel süre hedef VPS veya iş RTO'su değildir.

## Açık kanıt ve değişiklik kapısı

`BLOCK-DR-001` yerel DB restore için kapanmış, hedef volume, hedef restore, private app files ve hedef RTO için açık kalmıştır. `PILOT_LOCAL` ve en fazla 6 saat pilot RPO tanımlıdır; off-host bu profilde zorunlu değildir. Production resilient profile iş sahibi onayı ve hedef restore kanıtı olmadan geçilmez.
