# Hepsiburada Yerel Mutabakat ve Geri Dönüş Rehberi

Durum: `LOCAL_DRY_ONLY`. Bu rehber dış sisteme bağlanma veya dış yazmayı açma yetkisi vermez.

## Güvenlik sınırı

- Hepsiburada bağlantısı yalnız `DRAFT` ve capability kayıtları `UNKNOWN` iken yerel kuru mutabakat yapılabilir.
- Yerel kuru mutabakat HTTP isteği, credential kullanımı, job kuyruğu veya dış etki üretmez.
- Karşılaştırma yalnız veritabanındaki mevcut yerel listing, sipariş ve paket durumlarıyla yapılır.
- Farklarda ham değer yerine SHA-256 özetleri saklanır; sonuç sessizce veriyi değiştirmez.
- `N11` ve `PAZARAMA` bağlantıları F6A politikasında reddedilir.

## Yerel kuru mutabakat kapsamları

| Kapsam | Kontrol | Sonuç |
| --- | --- | --- |
| `PRODUCT_LISTING` | İstenen ve mevcut yerel listing durumu | Fark kaydı; uzak okuma gereksinimi |
| `ORDER_PACKAGE_RETURN` | Sipariş durumu ile yerel paketlerden türetilen durum | Fark kaydı; yerel yeniden hesaplama gereksinimi |
| `ALL_LOCAL` | Yukarıdaki iki kontrol | Tek koşu altında açıklanabilir fark listesi |

Bu kapsam adları mevcut ortak mutabakat sözleşmesidir; Hepsiburada dış API kapsamı veya enum’u değildir.

## Çalıştırma ve kanıt

1. Bağlantının platform kodunun `HEPSIBURADA`, durumunun `DRAFT` ve bütün dış yazma anahtarlarının kapalı olduğunu kaydet.
2. Ortak `IF3ReconciliationService.RunLocalDryAsync` servisini seçilen yerel kapsamla çalıştır.
3. Koşu kimliği, başlangıç/bitiş zamanı, karşılaştırılan kayıt sayısı ve fark sayısını kanıt kaydına ekle.
4. Her fark için entity türü, anahtar, alan, iki SHA-256 özet ve çözüm sınıfını gözden geçir.
5. Açıklanamayan kritik fark varsa bağlantıyı etkinleştirme ve capability durumunu yükseltme.

Henüz operatöre açık yeni bir API endpoint veya menü oluşturulmamıştır. SIT verisi gelene kadar doğrulama servis ve otomatik test seviyesindedir.

## Geri dönüş

1. Global, bağlantı ve capability dış yazma anahtarlarını kapalı tut; biri açılmışsa önce kapat.
2. Bağlantıyı etkinleştirme; `DRAFT` durumunda bırak.
3. Hepsiburada’ya ait bekleyen dış-yazma işi oluşturma veya yeniden deneme başlatma.
4. Mutabakat, inbox ve audit kayıtlarını silme; olay incelemesi için koru.
5. Credential mevcutsa loglama veya dışa aktarma yapmadan güvenli depoda devre dışı bırak; silme/rotasyon için ayrıca onay al.
6. Yerel kuru mutabakatı yeniden çalıştır ve yeni dış etki olmadığını kanıtla.
7. Yeniden açma için doğrulanmış auth modeli, partner/SIT hesabı, merchant scope, anonim fixture, iş otoritesi ve işlem bazlı kullanıcı onayı bekle.

## Açık kapılar

- Shopify production reconciliation/rollback kanıtı tamamlanmadan F6A production çıkışı yoktur.
- Hepsiburada partner/SIT kanıtı olmadan remote read, safe-write ve production smoke yoktur.
- Açıklanamayan kritik fark varken capability `SUPPORTED` yapılamaz.
- Bu rehber F6B veya F6C başlangıç onayı değildir.
