# Shopify adapter işletim notu

- Admin GraphQL API sürümü `2026-07` olarak sabittir. Request URL ve `X-Shopify-API-Version` response header eşleşmezse bağlantı testi kapanır.
- Mağaza kapsamı yalnız canonical `*.myshopify.com` alanıdır; scheme, path ve özel domain kabul edilmez.
- Access token ve app client secret yalnız şifreli `PlatformCredential` içinde tutulur. Log, API response veya UI geri gösterimi yoktur.
- GraphQL top-level `errors` ve mutation `userErrors` ayrı başarısızlık kanallarıdır.
- Webhook HMAC, JSON parse işleminden önce değişmemiş raw body üzerinde doğrulanır. `X-Shopify-Webhook-Id` Inbox dedupe anahtarıdır.
- Bulk JSONL satır bazında akar ve tamamlanan satır numarası checkpoint olarak kullanılabilir; geçici result URL kalıcı cursor değildir.
- Global/connection write anahtarları, capability, granted scope, development-store fixture ve iş otoritesi birlikte kanıtlanmadan product, inventory, price veya fulfillment yazması yapılmaz.
- Location mapping bulunmadan inventory veya fulfillment çağrısı yapılmaz. Mevcut uygulamada bütün Shopify yazmaları bu dış kanıtlar gelene kadar daha erken aşamada fail-closed’dur.

## Dış kabul girdileri

Development store canonical alanı, app/auth modeli, access token edinim kanıtı, app client secret, granted scopes, yerel MAIN→Shopify Location GID eşlemesi, anonim fixture checksum’ları, public HTTPS webhook adresi ve ürün/fulfillment iş otoriteleri gereklidir. Bunlar repository’ye secret olarak yazılmaz.
