# Ravencia v9 Katalog ve Eşleme Çalışma Alanı

## Amaç

v9 geliştirmesi, panel kategorisi → pazaryeri kategorisi → kategori özelliği → özellik değeri zincirini ürün oluşturma ekranına bağlar. Kullanıcı kategoriye tanımlanmış özellikleri ürün formunda seçer, varyant olarak işaretlenen özelliklerin Kartezyen kombinasyonlarını oluşturur ve her varyant için SKU, barkod, stok ve fiyat bilgilerini yönetir.

## Yeni API yüzeyi

- `GET /api/v1/catalog/categories/{categoryId}/attribute-requirements`
- `PUT /api/v1/catalog/categories/{categoryId}/attribute-requirements`
- `POST /api/v1/catalog/attributes/{attributeId}/values`
- `GET /api/v1/mappings/{mappingType}?connectionId=...&scopeExternalId=...`
- `DELETE /api/v1/mappings/{mappingType}/{localId}?connectionId=...&scopeExternalId=...`

Toplu mapping endpoint'i kategori kapsamındaki tüm eşlemeleri tek istekte döndürür. Böylece her pazaryeri özelliği için ayrı mapping sorgusu yapılmaz.

## Kategori ve özellik eşleme akışı

1. ACTIVE Trendyol bağlantısı seçilir.
2. Paneldeki etkin yaprak kategori seçilir.
3. Trendyol'un güncel kategori snapshot'ındaki etkin yaprak kategori seçilir.
4. Panel kategorisine ürün özellik başlıkları bağlanır.
5. Özellik için zorunlu ve özel değer kabul etme kuralları belirlenir.
6. Trendyol kategori özellikleri panel özelliklerine eşlenir.
7. Seçimli özelliklerin bütün panel değerleri aynı kartta Trendyol değerleriyle eşlenir; yanlış eşlemeler ETag korumalı biçimde kaldırılabilir.
8. Zorunlu özellik eşleme ilerlemesi `eşlenen/toplam` olarak gösterilir.

## Ürün oluşturma akışı

1. Temel ürün, kategori, marka, model kodu ve temel SKU girilir.
2. Fiyat, KDV, stok ve güvenlik stoğu belirlenir.
3. Kargo ölçülerinden desi otomatik hesaplanır.
4. En fazla sekiz HTTPS ürün görseli girilir.
5. Kategoriye bağlı özellikler yüklenir.
6. Varyant özelliği olarak işaretlenen başlıkların seçilmiş değerleriyle en fazla 100 kombinasyon oluşturulur.
7. Varyant tablosunda barkod, SKU, stok, satış fiyatı ve liste fiyatı düzenlenir.
8. Toplu stok ve fiyat uygulaması kullanılabilir.
9. Ürün oluşturulurken normal özellikler ürün seviyesine, varyant özellikleri ilgili varyant seviyesine kaydedilir.
10. ACTIVE Trendyol kanalı seçilmişse kanal teklifleri, listing profile ve güvenli yayın işi oluşturulur.

## Güvenlik ve doğrulama

- Yalnız etkin yaprak kategori ürün kaydında kullanılabilir.
- SKU ve barkodlar ürün içinde ve tenant genelinde benzersiz olmalıdır.
- Tekli seçim özellikleri birden fazla ürün seviyesi değeri kabul etmez.
- Çoklu seçim özellikleri birden fazla değer ataması taşıyabilir.
- Her varyant kategori zorunlu özelliklerini ürün veya varyant seviyesinde karşılamalıdır.
- Yayında model kodu, barkod, görsel, fiyat, kategori/marka eşlemesi ve capability kapıları korunur.
- Ürün detayında yalnız başlık/açıklama güncellemek mevcut özellik atamalarını silmez.
- Aynı Trendyol değerinin birden fazla panel değerine seçilmesi arayüzde engellenir.

## Testler

- `scripts/validate-operational-workspaces.py`
- `scripts/validate-v9-catalog-workspace.py`
- `CatalogWorkspacePages.test.tsx`
- Güncellenmiş `F3Pages.test.tsx`
- `V9CatalogWorkspaceSourceTests.cs`

Bu ortamda npm registry paket eksikleri ve .NET SDK bulunmaması nedeniyle tam Vite/Vitest ve `dotnet test` çalıştırılamamıştır. Kaynak kabul testleri, TSX sözdizimi dönüşümü, hafif semantik TypeScript kontrolü, C# delimiter kontrolü ve `git diff --check` çalıştırılır.
