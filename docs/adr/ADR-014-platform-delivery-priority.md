# ADR-014 - Platform teslim önceliği

Durum: Superseded by ADR-015
Tarih: 2026-08-03

## Karar

İşletme sahibi aktif geliştirme ve doğrulama önceliğini `Trendyol → Trendyol E-Faturam → Hepsiburada` olarak belirledi. Bu tarihsel sıra, 2026-08-03 tarihli yeni işletme sahibi kararıyla ADR-015 tarafından `Trendyol → Hepsiburada → Trendyol E-Faturam` olarak değiştirilmiştir.

Bu karar mevcut modüler monolit sınırlarını, generic portları veya fail-closed capability modelini değiştirmez. Ertelenen platformların mevcut yerel kodu silinmez; yeni production capability, route, menü veya dış yazma açılmaz. Erteleme, eksik Shopify production reconciliation kanıtını tamamlanmış saymaz; yalnız iş sırasını değiştirir.

## Sonuçlar

- Trendyol, E-Faturam ve Hepsiburada için salt-okunur Stage/SIT kanıtları ve güvenli yerel akışlar önceliklidir.
- Bu üç platformdaki her read/write capability ayrı kanıtlanır.
- Bütün dış yazmalar ayrıca açık iş otoritesi ve safe-write kanıtı gelene kadar kapalıdır.
- Shopify, N11 ve Pazarama ancak işletme sahibi yeniden öncelik verdiğinde kendi açık blocker ve faz kapılarıyla ele alınır.
