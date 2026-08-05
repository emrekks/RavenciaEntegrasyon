# Güncel Faz ve Devralma Durumu

**Son güncelleme:** 2026-08-05

**Ana plan sürümü:** 7.2

**Aktif ürün kapsamı:** `TRENDYOL Türkiye CORE` + `TRENDYOL_EFATURAM`

**Genel durum:** `F3_CORE_CODE_COMPLETE_VALIDATION_PENDING / F4_CODE_COMPLETE_VALIDATION_PENDING / PRODUCTION_BLOCKED`

## Faz özeti

| Faz | Durum | Açıklama |
| --- | --- | --- |
| F0 | `BASELINE_COMPLETE` | Mimari, bağımlılık, risk ve doğrulama temeli hazır. |
| F1 | `HARDENING_CODED_DYNAMIC_REVALIDATION_REQUIRED` | Güvenlik/job/deployment sertleştirmesi kodlandı; exact runtime doğrulaması bekler. |
| F2 | `READY_LOCAL` | Yerel katalog, ürün, import, stok ve fiyat çekirdeği hazır. |
| F3 | `CORE_CODE_COMPLETE_STATIC_VERIFIED` | Trendyol Türkiye CORE bağlantı, referans, mapping, Product V2 create/update/archive/approval, birleşik fiyat-stok, Order V2/stream, paket aksiyonu, takip numarası, ortak etiket, iade aksiyonu/evidence/read-back, webhook ve invoice-link sınırı kodlandı. Dynamic, Docker ve Stage kabulü bekler. |
| F4 | `CODE_COMPLETE_STATIC_VERIFIED_DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED` | API_USER/MARKETPLACE auth, güvenli ayar görünümü, çoklu kargo eşlemesi, taxpayer, E-Fatura/E-Arşiv create, numeric status, PDF, E-Arşiv cancel, Trendyol link teslimi ve operatör UI kodlandı; exact runtime ve Stage kabulü bekler. |
| F5 | `PLANNED_BLOCKED_BY_F3_F4_AND_REVALIDATION` | Production pilot, F3/F4 dış kabul kapıları geçmeden başlamaz. |
| F6+ | `PLANNED` | Stabilizasyon, adapter registry ve sonraki platformlar. |

## Bu teslimde kapanan Trendyol işleri

- Product Update: unapproved veya approved content/variant/delivery fazlarına ayrılan durable state machine.
- Archive/unarchive: batch submit, poll ve publication read-back.
- Fiyat-stok: tek batch payload, offer/projection version kanıtı ve stale-result koruması.
- Sipariş: `/v2/orders` tekil read, stream cursor ve 2026 alan adları.
- Shipment: capability listesine bağlı paket aksiyonları, takip numarası, read-back ve ortak etiket.
- Return: `claimId` sözleşmesi, exact claim read-back, approve/reject, private evidence ve karar uzlaştırması.
- Capability: mevcut bağlantılara yeni capability backfill ve Owner/Administrator evidence kaydı.
- UI: ürün yayın yönetimi, fiyat-stok sync, shipment detail/actions/label, return decision/evidence ve capability evidence formu.
- Product read: 100 kayıt üst sınırı, ilk 10.000 kayıtta page, sonrasında `nextPageToken` cursor geçişi.

## Bu teslimde kapanan Trendyol E-Faturam işleri

- API_USER `signIn` ve MARKETPLACE partner `signIn -> customerSignIn` kimlik doğrulaması, company/user scope uyuşmazlığı koruması.
- Şifre içermeyen mali ayar read-back endpoint'i, mevcut değerleri koruyan PATCH ve çoklu kargo VKN/TCKN-yasal unvan paneli.
- MARKETPLACE Partner ID + VKN/TCKN mükellefiyet sorgusu ve başvuru ayrıntıları.
- Temel/Ticari E-Fatura ile internet satışı E-Arşiv canonical payload; `paymentInfo`, `deliveryInfo`, ASCII VKN/TCKN ve kuruş denklemleri.
- Durable submit, numeric status reconciliation, private PDF, E-Arşiv cancellation ve Trendyol package invoice-link teslimi.
- Resmî E-Faturam evidence hostu ve mali write capability'lerde Stage fixture SHA-256 zorunluluğu.
- Giden E-Fatura status yolu tahmin edilmeden deployment ayarında fail-closed bırakıldı.

## Kalan zorunlu doğrulamalar

1. Exact .NET `10.0.302`, Node `24.18.1`, npm `11.12.1` ve PostgreSQL/Docker ortamında locked restore, build, test, format, Vitest, Playwright ve Compose smoke.
2. Trendyol Stage'de tarihli fixture checksum ile capability evidence; açık onaylı create/update/archive/fiyat-stok/paket/etiket/iade yazma senaryoları.
3. Duplicate, timeout, rate-limit, partial batch, visibility delay, stale payload ve read-back uyuşmazlığı testleri.
4. Invoice-link için resmî terminal query kanıtlanırsa reconciliation; kanıtlanmazsa onaylı manuel teyit prosedürü.
5. F4 exact runtime/Stage mali E2E, backup/restore ve production pilotu.

## Production blockerları

- Exact backend ve frontend dinamik suite sonucu yok.
- Docker/Compose ve gerçek PostgreSQL Testcontainers sonucu yok.
- Trendyol Stage credential, kontrollü barkod/SKU/claim/package ve açık safe-write onayı yok.
- Capability satırları gerçek evidence olmadan `SUPPORTED` yapılamaz; global ve connection write switch kapalı kalır.
- LUXE/uluslararası storefront kapsam dışıdır.
- F4 kod kapsamı tamamlandı; exact runtime/Stage mali E2E ve off-host restore kanıtı tamamlanmamıştır.
