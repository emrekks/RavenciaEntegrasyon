# F0 Operasyon Kontrolleri

## Kill switch'ler

| Kontrol | Varsayılan | Etki |
| --- | --- | --- |
| `EXTERNAL_WRITES_ENABLED` | `false` | Global dış yazma kapısıdır; production açılışı açık onay gerektirir. |
| `AUTO_INVOICE_ENABLED` | `false` | Otomatik fatura üretim/gönderim davranışını kapalı tutar. |
| `PLATFORM_{CODE}_WRITES_ENABLED` | `false` | İlgili platformun dış yazmalarını kapalı tutar. |

Global anahtar kapalıysa platform anahtarı açık olsa bile yazma yapılamaz. Capability `SUPPORTED` değilse iki anahtar açık olsa dahi işlem fail-closed reddedilir. Read capability'leri write capability'lerinden ayrıdır; şartnamede doğrulanmamış yeni environment anahtarı üretilmez.

## Rollback sözleşmesi

1. `EXTERNAL_WRITES_ENABLED` ile tüm dış yazmaları kapat ve yeni job kabulünü durdur.
2. API/Worker sürümünü son doğrulanmış immutable image digest'ine döndür; floating tag kullanma.
3. İleri migration'ı otomatik geri alma varsayımı yapma. Veri kaybı yaratacak downgrade yerine forward-fix veya doğrulanmış restore planı uygula.
4. Inbox/idempotency kayıtlarını koru; aynı dış etkiyi yeniden tetikleme.
5. Backup/restore gerekiyorsa veri tabanı ile private dosyaları aynı recovery point'e getir ve uzlaştırma çalıştır.
6. Correlation id, sürüm/digest, olay zamanı, sahibi ve sonuçla operasyon kaydı oluştur.

## Açma kapısı

Dış write yalnız ilgili capability `SUPPORTED`, scope doğrulanmış, test kanıtı mevcut, global ve platform kill-switch değişikliği onaylı ve rollback gözden geçirilmişse açılır. Otomatik fatura için ayrıca `AUTO_INVOICE_ENABLED` açık olmalıdır.
