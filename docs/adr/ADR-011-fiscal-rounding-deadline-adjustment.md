# ADR-011: Mali Yuvarlama, Vade ve Düzeltme Kapıları

- Durum: Proposed — dış mali karar bekleniyor
- Tarih: 2026-07-31
- Faz: F4

## Bağlam

Fatura trigger'ı, order/package kapsamı, fiyatın KDV dahil-hariç otoritesi, satır/belge yuvarlaması, düzenleme vadesi ve kısmi iptal/iade sonucu işletmeye ve mali mevzuat yorumuna bağlıdır. Yetkili şartname bu değerlerin uydurulmasına izin vermez.

## Güvenli geçici karar

- `AUTO_INVOICE_ENABLED` kapalı kalır.
- Policy alanları onaylanana kadar `UNAPPROVED` taşır.
- VKN/TCKN biçim kontrolü mükellefiyet kanıtı sayılmaz.
- Unit/KDV hesap otoritesi kesin değilse invoice doğrulaması `FISCAL_CALCULATION_AUTHORITY_REQUIRED` ile kapanır.
- Timeout veya belirsiz provider sonucu başarı sayılmaz; `UNKNOWN_RESULT` reconciliation bekler.
- Eski invoice, party snapshot ve document değiştirilmez; düzeltme kararı yoksa `MANUAL_REVIEW`/OperationalIssue kullanılır.

## Açık karar kapıları

Mali müşavir ve iş sahibi; trigger state, package scope, KDV dahil-hariç otoritesi, rounding, due başlangıcı/süresi, cancellation/adjustment yöntemi ve retention değerlerini yazılı onaylamalıdır. Karar provider test firması ve anonim fixture ile doğrulanmadan dış submit açılmaz.

## Sonuçlar

Yerel aggregate, migration, API ve UI geliştirilebilir; gerçek mali belge kesimi ve Trendyol delivery `BLOCKED_EXTERNAL` kalır. Bu ADR alternatif mimari, yeni servis veya yeni veri tabanı önermez.
