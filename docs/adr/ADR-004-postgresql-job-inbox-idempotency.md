# ADR-004: PostgreSQL Job, Inbox ve Idempotency

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Dış platform çağrıları gecikebilir, tekrarlanabilir veya belirsiz sonuç verebilir; dış etki tam olarak bir kez teslim garantisine dayanamaz.

## Karar

Durable job queue, Inbox, idempotency ve reconciliation PostgreSQL üzerinde uygulanacaktır. Benzersiz idempotency anahtarları, transaction sınırları, lease/retry/backoff ve operasyonel hata durumu ilgili fazlarda açıkça modellenir.

## Sonuçlar

API uzun işi Worker'a devreder; duplicate/replay ikinci dış etki üretmez; belirsiz sonuç uzlaştırılır. Redis, RabbitMQ, Kafka veya başka broker eklenmez.

## Değişiklik kapısı

Job altyapısı yalnız yetkili şartname ve veri bütünlüğü kanıtıyla değişebilir.
