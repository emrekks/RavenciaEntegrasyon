# Güncel İzlenebilirlik Matrisi

| Gereksinim | Uygulama alanı | Test/kanıt | Durum |
| --- | --- | --- | --- |
| Yalnız Trendyol ve E-Faturam bağlantısı | `ActiveIntegrationScope`, connection service, UI, Worker, DI | Scope unit/UI/repository guard | IMPLEMENTED_LOCAL |
| Güvenli kimlik ve tenant sınırı | API security middleware, Identity, tenant accessor | Domain/application/API tests | IMPLEMENTED_LOCAL |
| Secret değerini geri göstermeme | Protected credential, masked hint, file-backed secret | Security tests | IMPLEMENTED_LOCAL |
| Dış yazmayı varsayılan kapatma | Global feature flag + connection setting | Contract/service guards | IMPLEMENTED_LOCAL |
| Kategori/özellik/değer eşitleme | Trendyol reference port + scoped snapshots | Contract + persistence tests | IMPLEMENTED_LOCAL / STAGE_RETEST_REQUIRED |
| Leaf kategori zorunluluğu | Reference snapshot + `IsLeaf` scope validation | F3 job/persistence tests | IMPLEMENTED_LOCAL |
| Ürün okuma | Trendyol approved-products mapper | Contract tests | IMPLEMENTED_LOCAL / E2E_REQUIRED |
| Ürün yayınlama | Product port adapter | Application orchestration yok | PARTIAL |
| Stok/fiyat yazma | Inventory-price port | Birleşik uzak komut yok | NOT_COMPLETE |
| Sipariş/paket içeri alma | Order polling, overlap cursor, idempotent upsert | Contract/persistence tests | IMPLEMENTED_LOCAL / E2E_REQUIRED |
| İade okuma | Claims polling/mapper | Contract tests | PARTIAL / FIXTURE_REQUIRED |
| Fatura oluşturma | E-Faturam canonical payload + provider port | Contract/payload tests | IMPLEMENTED_LOCAL / FINANCIAL_E2E_REQUIRED |
| Fatura durum/iptal | Provider port | Unsupported guard | NOT_COMPLETE |
| PDF private storage | Permanent URL + private file abstraction | F4 tests | IMPLEMENTED_LOCAL / E2E_REQUIRED |
| Trendyol invoice link | Marketplace delivery port | Contract tests | IMPLEMENTED_LOCAL / RECONCILIATION_REQUIRED |
| Job lease/retry/idempotency | PostgreSQL job/inbox/idempotency | Persistence tests | IMPLEMENTED_LOCAL |
| Gerçek readiness | `/health/ready`, Postgres health, container probe | Repository guard + deploy smoke | IMPLEMENTED_IN_CLEAN_COPY |
| Temiz kaynak paketi | ignore + verifier + CI precheck | `python3 scripts/verify-repository-cleanliness.py` | IMPLEMENTED_IN_CLEAN_COPY |
| Backup/restore | backup scripts/runbook | Hedef off-host test bekliyor | PARTIAL |
