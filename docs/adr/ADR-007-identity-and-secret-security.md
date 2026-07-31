# ADR-007: Kimlik ve Secret Güvenliği

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Tek Owner hesabı, platform credential'ları ve imza/key-ring malzemesi repository ile runtime arasında açık güven sınırları gerektirir.

## Karar

Kimlik ve platform credential'ları least privilege, açık scope, güvenli runtime injection, redaction ve rotasyonla yönetilir. Secret değerleri repository, image, log ve fixture'a girmez. İmza/Data Protection anahtarları kalıcı private volume veya onaylı secret kaynağında tutulur. Güvenlik kontrolleri tenant/connection/environment scope'unu zorunlu kılar.

## Sonuçlar

F1 yerel geliştirmesi gerçek secret olmadan fake adapter ile yürür. Gerçek credential ancak capability kanıtı ve kill-switch onayıyla kullanılır.

## Değişiklik kapısı

Auth/secret yöntemi, resmî sağlayıcı sözleşmesi ve threat-model incelemesi olmadan değişmez.
