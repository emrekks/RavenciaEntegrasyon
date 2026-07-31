# ADR-003: PostgreSQL Veri ve Migration Zinciri

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Veri modeli ve migration sahipliğinde birden fazla otorite drift ve deployment sırası riski yaratır.

## Karar

PostgreSQL 18 veri otoritesidir. Tek `AppDbContext` ve tek migration zinciri kullanılacaktır. EF Core 10 ile Npgsql 10 hattı korunur. Şema, constraint ve index'ler bağlayıcı veri modeline göre ilgili fazlarda oluşturulur.

## Sonuçlar

Migration sırası tek ve denetlenebilirdir; API ile Worker aynı veri sözleşmesini kullanır. F0'da DbContext, migration veya tablo oluşturulmaz.

## Değişiklik kapısı

İkinci context/migration zinciri ya da farklı veri otoritesi yalnız yetkili şartname değişikliğiyle mümkündür.
