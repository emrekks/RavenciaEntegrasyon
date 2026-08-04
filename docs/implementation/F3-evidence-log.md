# F3 Trendyol Kanıt Günlüğü

Bu dosya yalnız tekrar üretilebilir kanıtları içerir. Önceki platformlara ait tarihsel kayıtlar aktif kanıt sayılmaz.

| Kanıt | Durum | Not |
| --- | --- | --- |
| Adapter klasör sınırı | PASS_LOCAL | Yalnız Trendyol ve TrendyolEFaturam adapter klasörleri beklenir |
| Connection scope | PASS_LOCAL | Service/UI/Worker yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM` kabul eder |
| Reference mapper/leaf guard | PASS_LOCAL | Scoped snapshot ve leaf category doğrulaması vardır |
| Product/order/return contract fixtures | PASS_LOCAL_PARTIAL | Fixture kapsamı gerçek Stage payload’larıyla genişletilmeli |
| Product publication orchestration | MISSING | Adapter application job/endpoint/UI akışına bağlı değildir |
| Combined stock-price write | MISSING | Ayrı portlar uzak birleşik sözleşmeyle uyumlu değildir |
| Stage safe-write | BLOCKED_EXTERNAL | Credential ve açık operasyon onayı gerekir |
| Reconciliation/rollback | PARTIAL | Yerel iskelet var; gerçek uzak fark senaryosu gerekir |
