# Güncel İzlenebilirlik Matrisi

| Gereksinim | Uygulama alanı | Test/kanıt | Durum |
| --- | --- | --- | --- |
| Yalnız Trendyol ve E-Faturam bağlantısı | `ActiveIntegrationScope`, connection service, UI, Worker, DI | Scope unit/UI/repository guard | IMPLEMENTED_LOCAL |
| Güvenli kimlik ve tenant sınırı | API security middleware, Identity, tenant accessor | Domain/application/API tests | IMPLEMENTED_LOCAL |
| Secret değerini geri göstermeme | Protected credential, masked hint, file-backed secret | Security tests | IMPLEMENTED_LOCAL |
| Dış yazmayı varsayılan kapatma | Global feature flag + connection setting | Contract/service guards | IMPLEMENTED_LOCAL |
| Kategori/özellik/değer eşitleme | Trendyol reference port + scoped snapshots | Contract + persistence tests | IMPLEMENTED_LOCAL / STAGE_RETEST_REQUIRED |
| Birleşik kategori-kapsamlı eşleme çalışma alanı | `MappingPage kind="attributes"`, `AttributeValueMappingEditor` | Vitest request/payload regression + Playwright route akışı | CODED / DYNAMIC_REVALIDATION_REQUIRED |
| Leaf kategori zorunluluğu | Reference snapshot + `IsLeaf` scope validation | F3 job/persistence tests | IMPLEMENTED_LOCAL |
| Ürün okuma | Trendyol approved/unapproved barkod filtreleri ve mapper | Contract fixture testleri | IMPLEMENTED_LOCAL / STAGE_E2E_REQUIRED |
| Ürün yayınlama | Product create port, payload composer, durable job, batch polling, approved/unapproved onay uzlaştırması, kimlik linkleri ve publication status API | Kodlanmış PostgreSQL create/replay/partial + approval/live/reject/pending/identity-conflict/superseded-payload testleri; exact runtime ve Stage bekliyor | IMPLEMENTED_LOCAL / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED |
| Stok/fiyat yazma | Birleşik price-inventory composer + durable job + version read-back guard | Contract/source + PostgreSQL senaryoları kodlandı | IMPLEMENTED_LOCAL / DYNAMIC_STAGE_REVALIDATION_REQUIRED |
| Sipariş/paket içeri alma | Order stream cursor + `/v2/orders` exact read + idempotent upsert | 2026 field contract tests + persistence tests kodlandı | IMPLEMENTED_LOCAL / STAGE_E2E_REQUIRED |
| Shipment ve etiket | Capability-controlled package actions, exact order read-back, common-label create/poll/private storage | Worker/UI/contract scenarios coded | IMPLEMENTED_LOCAL / DYNAMIC_STAGE_REVALIDATION_REQUIRED |
| İade okuma ve aksiyon | Claims poll/exact read, `claimId`, approve/reject/evidence/read-back | Contract + worker scenarios coded | IMPLEMENTED_LOCAL / DYNAMIC_STAGE_REVALIDATION_REQUIRED |
| Fatura oluşturma | E-Faturam canonical payload + provider port | Contract/payload tests | IMPLEMENTED_LOCAL / FINANCIAL_E2E_REQUIRED |
| Fatura durum/iptal | Provider port | Unsupported guard | NOT_COMPLETE |
| PDF private storage | Permanent URL + private file abstraction | F4 tests | IMPLEMENTED_LOCAL / E2E_REQUIRED |
| Trendyol invoice link | Marketplace delivery port | Contract tests | IMPLEMENTED_LOCAL / RECONCILIATION_REQUIRED |
| Job lease/retry/idempotency | PostgreSQL job/inbox/idempotency | Persistence tests | IMPLEMENTED_LOCAL |
| Gerçek readiness | `/health/ready`, Postgres health, container probe | Repository guard + deploy smoke | IMPLEMENTED_IN_CLEAN_COPY |
| Temiz kaynak paketi | ignore + verifier + CI precheck | `python3 scripts/verify-repository-cleanliness.py` | IMPLEMENTED_IN_CLEAN_COPY |
| Backup/restore | backup scripts/runbook | Hedef off-host test bekliyor | PARTIAL |
