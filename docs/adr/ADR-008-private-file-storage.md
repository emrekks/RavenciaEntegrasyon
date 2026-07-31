# ADR-008: Private Dosya Depolama

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Fatura, etiket, iade kanıtı ve benzeri dosyalar hassas veri içerebilir.

## Karar

Dosyalar `IFileStorage` soyutlaması arkasında, public web root dışında private local volume'da tutulur. Erişim yetkilendirilmiş uygulama akışıyla, opaque tanımlayıcı ve allowlist kontrolleriyle sağlanır. Kalıcı public URL kullanılmaz.

## Sonuçlar

Path traversal ve doğrudan yayın riski azaltılır; app files yedek/restore noktasının parçasıdır. F0'da interface veya storage kodu oluşturulmaz.

## Değişiklik kapısı

Depolama backend'i yalnız `IFileStorage` sözleşmesi, private erişim, encryption ve backup/restore kanıtı korunarak değişebilir.
