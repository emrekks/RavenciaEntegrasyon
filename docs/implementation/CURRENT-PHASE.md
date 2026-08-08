# Güncel Faz ve Devralma Durumu

**Son güncelleme:** 2026-08-06

**Ana plan sürümü:** 7.3

**Aktif ürün kapsamı:** `TRENDYOL Türkiye CORE` + `TRENDYOL_EFATURAM`

**Genel durum:** `F3_CORE_CODE_COMPLETE_VALIDATION_PENDING / F4_CODE_COMPLETE_VALIDATION_PENDING / PRODUCTION_BLOCKED`

**2026-08-08 doğrulama notu:** Backend build ve frontend typecheck `PASS`; 137 Docker gerektirmeyen backend testi, 13 Vitest davranış testi ve frontend production build `PASS`. Docker/PostgreSQL dinamik testleri `NOT_RUN / BLOCKED_ENVIRONMENT`; gerçek Stage kabulü bulunmadığından production blokajı değişmemiştir.

## Faz özeti

| Faz | Durum | Açıklama |
| --- | --- | --- |
| F0 | `BASELINE_COMPLETE` | Mimari, bağımlılık, risk ve doğrulama temeli hazır. |
| F1 | `HARDENING_CODED_DYNAMIC_REVALIDATION_REQUIRED` | Güvenlik/job/deployment sertleştirmesi kodlandı; exact runtime doğrulaması bekler. |
| F2 | `V9_CATALOG_WORKSPACE_CODED_STATIC_VERIFIED` | Yerel katalog çekirdeğine birleşik kategori/özellik/değer eşleme, kategori özellikleri, varyant matrisi ve güvenli yayın hazırlığı eklendi; exact runtime doğrulaması bekler. |
| F3 | `CORE_CODE_COMPLETE_STATIC_VERIFIED` | Trendyol Türkiye CORE bağlantı, referans, mapping, Product V2 create/update/archive/approval, birleşik fiyat-stok, Order V2/stream, paket aksiyonu, takip numarası, ortak etiket, iade aksiyonu/evidence/read-back, webhook ve invoice-link sınırı kodlandı. Dynamic, Docker ve Stage kabulü bekler. |
| F4 | `CODE_COMPLETE_STATIC_VERIFIED_DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED` | Doğrudan API_USER auth, token kaynaklı mali kapsam, otomatik E-Fatura/E-Arşiv seçimi, provider-managed hesap, numeric status, PDF, E-Arşiv cancel, Trendyol link teslimi ve sade operatör UI kodlandı; exact runtime ve Stage kabulü bekler. |
| F5 | `PLANNED_BLOCKED_BY_F3_F4_AND_REVALIDATION` | Production pilot, F3/F4 dış kabul kapıları geçmeden başlamaz. |
| F6+ | `PLANNED` | Stabilizasyon, adapter registry ve sonraki platformlar. |

## Bu teslimde kapanan v9 katalog işleri

- Panel yaprak kategorisi ile Trendyol yaprak kategorisi eşleme ekranı referans görünüme göre yenilendi.
- Kategoriye panel özellik başlığı bağlama, yeni özellik/seçenek oluşturma ve zorunlu/özel değer kuralları eklendi.
- Trendyol kategori özellikleri ve değerleri, kategori kapsamında panel özellik/değerleriyle eşlenir; zorunlu eşleme ilerlemesi gösterilir.
- Toplu mapping okuma endpoint'iyle kart başına N+1 API çağrıları kaldırıldı.
- Ürün oluşturma ekranında kategori özellikleri doğrudan yüklenir; seçilen varyant özelliklerinin Kartezyen kombinasyonları oluşturulur.
- Varyant satırlarında SKU, barkod, stok, satış/liste fiyatı düzenlenir; toplu değer uygulama ve tekrar kontrolü vardır.
- Ürün ve varyant özellikleri doğru kapsamda kalıcılaştırılır; ACTIVE Trendyol kanalı seçildiğinde listing profile, teklifler ve güvenli yayın job'u hazırlanır.
- Kaynak kabul kontrolleri statik olarak geçti; exact Node/.NET, PostgreSQL ve Stage kabulü bekler.

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

- E-Faturam bağlantısı yalnız doğrudan API_USER `signIn` modeline sadeleştirildi; panel yalnız e-posta/parolayı şifreli saklar.
- `companyId` ve `userId` sign-in tokenından otomatik okunur; mali hesap, kullanıcı kimliği ve seri/prefix ayarları panel/API/persistence yüzeyinden çıkarıldı. Eski bağlantı `SettingsJson` kayıtları veri migrasyonuyla yalnız `ExternalWritesEnabled` kalacak biçimde temizlenir.
- Belge türü `commercial + eInvoiceAvailable` snapshotına göre otomatik `TEMELFATURA` veya `EARSIVFATURA` seçilir; ayrı mükellef sorgusu ve kullanıcı senaryo seçimi kaldırıldı.
- Ödeme ve taşıyıcı kullanıcı ayarları kaldırıldı. E-Arşiv internet satışı için gereken teknik alanlar Trendyol siparişi ve resmî kargo sağlayıcı kataloğundan otomatik üretilir; bilinmeyen taşıyıcı fail-closed bloklanır.
- Canonical payload, ASCII VKN/TCKN, iptal edilmiş satır filtresi ve kuruş denklemleri korunur.
- Durable submit, numeric status reconciliation, private PDF, E-Arşiv cancellation ve Trendyol package invoice-link teslimi korunur.
- Resmî E-Faturam evidence hostu ve mali write capability'lerde Stage fixture SHA-256 zorunluluğu korunur.
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
