# ADR-001: Modüler Monolit ve Süreç Sınırları

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Çözüm, tek işletmenin entegrasyon işlevlerini veri bütünlüğü ve operasyon sadeliğiyle yürütmelidir.

## Karar

Clean Architecture ilkeleriyle modüler monolit uygulanacaktır. API ve Worker ayrı process/container olarak çalışacak, aynı PostgreSQL veri tabanını ve aynı modül sözleşmelerini kullanacaktır. Modül sınırları şartnamedeki solution/proje yapısına uygun kalacaktır.

Mikroservis, Kubernetes, RabbitMQ, Kafka, Redis, ikinci broker, event bus veya service mesh eklenmez.

## Sonuçlar

Tek deployment ailesi ve tek transaction/veri otoritesi korunur; uzun işler API request lifecycle'ından Worker'a ayrılır. Modüller arası bağımlılık ve süreç sahipliği testlerle izlenecektir.

## Değişiklik kapısı

Yalnız kullanıcıca onaylanan yeni yetkili şartname ve etkileri kapsayan ADR ile değişebilir; ölçek varsayımı tek başına yeterli değildir.
