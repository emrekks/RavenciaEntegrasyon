# Hepsiburada adapter işletim notu

## Mevcut güvenli durum

- Halka açık portal katalog/sipariş/talep guide’larında Basic Auth anlatırken yeni portal başlangıç yüzeyi client-credentials örneği göstermektedir.
- Partner hesabındaki servis anahtarı ekranı, ürün ailesi, environment, merchant scope ve gerçek SIT çağrısı birlikte doğrulanana kadar auth türü veya token endpoint’i seçilmez.
- `v1.0` yalnız doğrulanan marketplace guide sürüm kaydıdır; bütün ürün ailelerinin tek ve değişmez production sürümü olduğu varsayılmaz.
- Adapter generic connection/reference/product/inventory-price/order/return portlarını uygular fakat dış HTTP çağrılarını capability kanıtına kadar fail-closed tutar.
- Bütün write yöntemleri `EXTERNAL_WRITE_DISABLED` döndürür. Katalog create, listing, stok, fiyat, package veya return action isteği üretilmez.
- F6B N11 ve F6C Pazarama bu adapterın parçası değildir.

## Dış kabul girdileri

Hepsiburada partner/SIT hesabı, merchant ID, ürün ailesi yetkileri, güncel auth sözleşmesi, SIT/production host ve version kaydı, anonim reference/product/listing/order/package/return fixture’ları, safe-write iş otoriteleri ve varsa public webhook callback bilgisi gereklidir. Secret veya gerçek müşteri verisi repository’ye yazılmaz.
