# 2026-08-04 Temizlik ve Değişiklik Manifestosu

Bu kayıt, kaynak-only çıkarılan özgün proje ile temizlenmiş teslim kopyası arasındaki farkı gösterir. Arşiv içindeki `.git`, `bin/obj`, PostgreSQL `pgdata/WAL`, geçici çıktı ve cache dosyaları kaynak karşılaştırmasına alınmadan teslim paketinden tamamen dışlandı.

- Kaynak karşılaştırmasında kaldırılan dosya: **25**
- Yeni yönetim/dokümantasyon dosyası: **5** (bu manifesto dahil)
- Güncellenen mevcut dosya: **38**

## Kaldırılan kaynak/doküman dosyaları

- `docs/adr/ADR-014-platform-delivery-priority.md`
- `docs/adr/ADR-015-three-platform-completion-gate.md`
- `docs/implementation/F5-evidence-log.md`
- `docs/implementation/F5-plan.md`
- `docs/implementation/F6A-evidence-log.md`
- `docs/implementation/F6A-plan.md`
- `docs/runbooks/hepsiburada-reconciliation-and-rollback.md`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/Fixtures/order-read-success.json`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaAdapter.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaConnectionProbe.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaContract.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaOrderJsonMapper.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/HepsiburadaOrderReader.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/README.md`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/README.md`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyAuthenticationHandler.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyContracts.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyGraphQlClient.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyWebhookVerifier.cs`
- `src/MarketplaceHub.Web/debug.log`
- `src/MarketplaceHub.Web/dist/assets/index-DO3jWawA.css`
- `src/MarketplaceHub.Web/dist/assets/index-DY1nqI-3.js`
- `src/MarketplaceHub.Web/dist/index.html`
- `tests/MarketplaceHub.Adapters.ContractTests/F5ShopifyContractTests.cs`
- `tests/MarketplaceHub.Adapters.ContractTests/F6AHepsiburadaContractTests.cs`

## Eklenen dosyalar

- `AGENTS.md`
- `docs/implementation/CURRENT-PHASE.md`
- `docs/adr/ADR-016-trendyol-efaturam-only-until-complete.md`
- `docs/reviews/2026-08-04-project-review.md`
- `docs/specification/current-scope.md`
- `scripts/verify-repository-cleanliness.py`
- `docs/reviews/2026-08-04-cleanup-manifest.md`

## Güncellenen dosyalar

- `.dockerignore`
- `.env.example`
- `.github/workflows/publish-release-images.yml`
- `.gitignore`
- `README.md`
- `deploy/compose/compose.production.yaml`
- `deploy/compose/compose.yaml`
- `deploy/scripts/initialize-deployment.sh`
- `docs/implementation/F0-environment-secret-catalog.md`
- `docs/implementation/F0-external-dependencies.md`
- `docs/implementation/F0-plan.md`
- `docs/implementation/F0-risk-register.md`
- `docs/implementation/F2-plan.md`
- `docs/implementation/F3-evidence-log.md`
- `docs/implementation/F3-plan.md`
- `docs/implementation/F4-evidence-log.md`
- `docs/implementation/F4-plan.md`
- `docs/implementation/traceability-matrix.md`
- `docs/platform-rules/capability-matrix.md`
- `docs/runbooks/local-development.md`
- `src/MarketplaceHub.Api/Program.cs`
- `src/MarketplaceHub.Application/F3Contracts.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/README.md`
- `src/MarketplaceHub.Infrastructure/DependencyInjection.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F3ConnectionService.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F3JobProcessor.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F3SalesService.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F3WebhookService.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/LocalReconciliationPolicy.cs`
- `src/MarketplaceHub.Web/src/App.tsx`
- `src/MarketplaceHub.Web/src/F3Pages.test.tsx`
- `src/MarketplaceHub.Web/src/F3Pages.tsx`
- `src/MarketplaceHub.Web/src/styles.css`
- `src/MarketplaceHub.Worker/Worker.cs`
- `tests/MarketplaceHub.Adapters.ContractTests/AdapterBoundaryTests.cs`
- `tests/MarketplaceHub.Application.Tests/F3ConnectionScopeTests.cs`
- `tests/MarketplaceHub.EndToEnd.Tests/RepositoryGuardTests.cs`
- `tests/MarketplaceHub.Persistence.IntegrationTests/PostgresSchemaTests.cs`

## Paket dışı bırakılan artifact sınıfları

- `.git` geçmişi ve repository metadata
- bütün `bin`, `obj`, `node_modules`, `dist` ve test-report klasörleri
- debug/log dosyaları ve geçici PDF/PNG çıktıları
- yerel PostgreSQL `pgdata`, WAL ve runtime volume içeriği
- secret klasörleri, `.env`, sertifika/anahtar ve arşiv dosyaları
