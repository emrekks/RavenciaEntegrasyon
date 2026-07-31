# ADR-010: Backup, RPO, RTO ve Restore

- Durum: Accepted; iş hedefi ve restore kanıtı bekliyor
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Backup sıklığı tek başına veri kaybı ve hizmete dönüş hedefini kanıtlamaz; veri tabanı ve private dosyaların birlikte restore edilmesi gerekir.

## Karar

`BACKUP_PROFILE` başlangıçta `PILOT_LOCAL`dır. Pilot varsayımı PostgreSQL dump'ı 6 saatte bir/7 gün, haftalık 4, aylık 3 ve private app files günlük yedektir. Aynı fiziksel diskteki kopya `RISK-DR-001` taşır. Production dayanıklılığı için `PRODUCTION_RESILIENT`, şifreli off-host ayrı failure domain ve ölçülmüş restore zorunludur.

RPO için pilot hedefi en fazla 6 saattir; kesin iş RPO/RTO'su kullanıcı girdisi ve restore testi olmadan ilan edilmez.

## Sonuçlar

Backup varlığı restore başarısı sayılmaz. PostgreSQL ve private dosyalar uyumlu recovery point'e alınır; checksum ve iş/veri bütünlüğü test edilir.

## Açık kanıt ve değişiklik kapısı

`BLOCK-DR-001` hedef volume, gerçek restore ve ölçülmüş RTO için açıktır. `PILOT_LOCAL` ve en fazla 6 saat pilot RPO tanımlıdır; off-host bu profilde zorunlu değildir. Production resilient profile iş sahibi onayı ve restore kanıtı olmadan geçilmez.
