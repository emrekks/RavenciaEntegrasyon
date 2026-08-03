# Hepsiburada adapter işletim notu

## Mevcut güvenli durum

- Sipariş SIT ürünü için Basic Auth, Merchant ID kapsamı, zorunlu User-Agent ve resmî salt-okunur endpoint 2026-08-02 tarihinde AWS Ubuntu Server üzerinden HTTP 200 ile doğrulandı.
- Uygulama credential değerlerini Data Protection ile şifreler, yalnız maskeli ipucu gösterir ve Basic Authorization değerini istek anında üretir. Secret log, kanıt veya repository içine yazılmaz.
- `v1.0` yalnız doğrulanan Sipariş SIT ürün ailesinin guide sürüm kaydıdır; diğer ürün ailelerinin veya production ortamının aynı auth/sözleşmeyi kullandığı varsayılmaz.
- Bağlantı testi `GET orders/merchantid/{merchantId}?offset=0&limit=1` çağrısıyla sınırlıdır. Dolu SIT yanıtı sipariş kimliği, kalem, fiyat, KDV, müşteri ve teslimat alanlarının tamamını doğrularsa yalnız `ORDER_READ` kanıtlanır.
- Reference, product, inventory-price ve return okumaları; ayrıca bütün dış yazmalar fail-closed kalır.
- Bütün write yöntemleri `EXTERNAL_WRITE_DISABLED` döndürür. Katalog create, listing, stok, fiyat, package veya return action isteği üretilmez.
- F6B N11 ve F6C Pazarama bu adapterın parçası değildir.

## Dış kabul girdileri

Dolu ve anonim sipariş/package fixture’ı, diğer ürün aileleri için ayrı auth/scope/host/version kanıtı, safe-write iş otoriteleri ve varsa public webhook callback bilgisi gereklidir. Secret veya gerçek müşteri verisi repository’ye yazılmaz.
