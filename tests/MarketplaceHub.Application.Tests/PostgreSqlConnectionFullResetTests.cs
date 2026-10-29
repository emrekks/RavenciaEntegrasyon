using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// P-6 full reset (30 Oct 2026 owner decision): every operational record of the connection goes, issued invoices
// included; its settings, another connection's records and the products the panel created or another connection uses
// stay (only their link goes); the units its order lines took stay taken under their carry key.
[Collection("postgres-integration")]
public sealed class PostgreSqlConnectionFullResetTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task FullReset_EmptiesEveryOperationalTableOfTheConnection_AndLeavesSettingsAndTheOtherConnection()
    {
        var seed = NewSeed("fr-all");
        try
        {
            await SeedAsync(seed);
            var removedBefore = await CountsAsync(ConnectionDataInventory.Removed, seed.Reset.Id);
            var otherBefore = await CountsAsync(ConnectionDataInventory.Removed, seed.Other.Id);
            var keptBefore = await CountsAsync(ConnectionDataInventory.Kept, seed.Reset.Id);
            // Every operational table the scenario reaches has a row to remove.
            foreach (var table in SeededTables) Assert.True(removedBefore[table] > 0, table);
            Assert.All(new[] { "integration.platform_credentials", "integration.connection_sync_policies", "integration.webhook_subscriptions", "sales.cargo_provider_mappings", "catalog.attribute_mapping_templates" }, table => Assert.True(keptBefore[table] > 0, table));

            await using (var db = fixture.CreateContext())
            {
                var view = await NewService(db).FullResetAsync(seed.Tenant.Id, seed.Reset.Id, CancellationToken.None);
                Assert.True(view.Succeeded, view.Error?.Message);
                var counts = view.Value!.Items.ToDictionary(x => x.Scope, x => x.Count);
                Assert.Equal((1, 1, 1, 1), (counts["ORDERS"], counts["INVOICES"], counts["PRODUCTS"], counts["QUESTIONS"]));
                Assert.Equal(3, counts["PRODUCT_LINKS"]);
                Assert.Equal(2, view.Value.KeptProducts);
                Assert.Equal("LEASED", view.Value.Job?.Status);
            }

            await using (var db = fixture.CreateContext())
            {
                var outcome = await NewService(db).RunFullResetAsync(seed.Tenant.Id, seed.Reset.Id, seed.ResetJob.Id, CancellationToken.None);
                Assert.True(outcome.Result.Succeeded, outcome.Result.ErrorSummary);
            }

            var removedAfter = await CountsAsync(ConnectionDataInventory.Removed, seed.Reset.Id);
            foreach (var table in ConnectionDataInventory.Removed)
                Assert.True(removedAfter[table] == (table == "integration.jobs" ? 1 : 0), $"{table}: {removedAfter[table]}");
            Assert.Equal(otherBefore, await CountsAsync(ConnectionDataInventory.Removed, seed.Other.Id));
            Assert.Equal(keptBefore, await CountsAsync(ConnectionDataInventory.Kept, seed.Reset.Id));

            await using var verification = fixture.CreateContext();
            // The issued invoice of the reset store's order went; the other store's invoice stayed.
            Assert.Equal([seed.OtherInvoice.Id], await verification.Invoices.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync());
            // The imported product went; the panel product and the shared product stayed without their link to the store.
            var products = await verification.Products.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync();
            Assert.DoesNotContain(seed.ImportedProduct.Id, products);
            Assert.Contains(seed.PanelProduct.Id, products);
            Assert.Contains(seed.SharedProduct.Id, products);
            Assert.Contains(seed.OtherProduct.Id, products);
            Assert.Equal([seed.Other.Id, seed.Other.Id], await verification.MarketplaceProductLinks.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.ConnectionId).ToListAsync());
            // The panel product's stock stays taken under the removed line's carry key.
            var carryKey = OrderInventoryStockPolicy.ResetCarryKey(seed.Reset.Id, seed.ResetOrder.ExternalOrderId, "fr-line");
            Assert.Equal(carryKey, (await verification.StockReservations.SingleAsync(x => x.TenantId == seed.Tenant.Id)).SourceId);
            Assert.Equal(carryKey, (await verification.StockLedgerEntries.SingleAsync(x => x.TenantId == seed.Tenant.Id)).SourceId);
            Assert.Equal(7m, (await verification.InventoryItems.SingleAsync(x => x.Id == seed.PanelStock.Id)).OnHand);
            // Problems about the store are closed; the other store's stay open.
            Assert.Equal(IssueStatus.Resolved, (await verification.OperationalIssues.SingleAsync(x => x.Id == seed.ResetIssue.Id)).Status);
            Assert.Equal(IssueStatus.Open, (await verification.OperationalIssues.SingleAsync(x => x.Id == seed.OtherIssue.Id)).Status);
            // The other store's waiting job was not cancelled.
            Assert.Equal(JobStatus.Pending, (await verification.IntegrationJobs.SingleAsync(x => x.Id == seed.OtherJob.Id)).Status);
            Assert.True(await verification.AuditLogs.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Action == "CONNECTION_FULL_RESET" && x.TargetId == seed.Reset.Id.ToString("D")));
            Assert.True(await verification.IntegrationOutboxEvents.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.OperationType == "reset" && x.AggregateId == seed.Reset.Id));
            Assert.Equal(OperationalDataMaintenanceService.PurgePendingStatus, (await verification.FileAssets.SingleAsync(x => x.Id == seed.ManualPdf.Id)).Status);
            Assert.Equal(100, (await verification.IntegrationJobs.SingleAsync(x => x.Id == seed.ResetJob.Id)).ProgressPercent);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task FullResetRequest_ChecksTheConfirmationAndPlatform_QueuesOneJob_AndCancelsTheWaitingOnes()
    {
        var seed = NewSeed("fr-req");
        try
        {
            await using (var seedDb = fixture.CreateContext())
            {
                seedDb.Tenants.Add(seed.Tenant);
                seedDb.PlatformConnections.AddRange(seed.Reset, seed.Other, seed.Provider);
                seedDb.IntegrationJobs.Add(Job(seed, seed.Reset, MarketplaceJobTypes.OrderSync, JobStatus.Pending));
                await seedDb.SaveChangesAsync();
            }

            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var wrong = await service.RequestFullResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Reset.Id, seed.Reset.Version, new FullResetConnectionCommand("yanlış"), "corr", CancellationToken.None);
            Assert.Equal(("VALIDATION_FAILED", 422), (wrong.Error!.Code, wrong.Error.Status));
            var stale = await service.RequestFullResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Reset.Id, seed.Reset.Version + 1, new FullResetConnectionCommand(seed.Reset.DisplayName), "corr", CancellationToken.None);
            Assert.Equal(412, stale.Error!.Status);
            var provider = await service.RequestFullResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Provider.Id, seed.Provider.Version, new FullResetConnectionCommand(seed.Provider.DisplayName), "corr", CancellationToken.None);
            Assert.Equal("FULL_RESET_UNSUPPORTED", provider.Error!.Code);

            var queued = await service.RequestFullResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Reset.Id, seed.Reset.Version, new FullResetConnectionCommand(seed.Reset.DisplayName), "corr", CancellationToken.None);
            Assert.True(queued.Succeeded, queued.Error?.Message);
            Assert.Equal("PENDING", queued.Value!.Job!.Status);
            var again = await service.RequestFullResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Reset.Id, seed.Reset.Version, new FullResetConnectionCommand(seed.Reset.DisplayName), "corr", CancellationToken.None);
            Assert.Equal(queued.Value.Job.JobId, again.Value!.Job!.JobId);

            await using var verification = fixture.CreateContext();
            var jobs = await verification.IntegrationJobs.Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
            var reset = Assert.Single(jobs, x => x.JobType == MarketplaceJobTypes.ConnectionFullReset);
            Assert.Equal((JobStatus.Pending, "user", JobPriorityPolicy.Default), (reset.Status, reset.TriggerType, reset.Priority));
            Assert.Equal(JobStatus.Cancelled, Assert.Single(jobs, x => x.JobType == MarketplaceJobTypes.OrderSync).Status);
            Assert.True(await verification.AuditLogs.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Action == "CONNECTION_FULL_RESET_REQUESTED"));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // The tables the scenario puts a row of the reset store into (the others are checked for 0 rows all the same).
    private static readonly string[] SeededTables =
    [
        "sales.orders", "sales.shipment_packages", "sales.return_claims", "sales.marketplace_invoice_reference_history", "sales.marketplace_questions",
        "sales.marketplace_finance_transactions", "sales.marketplace_cargo_invoice_items",
        "billing.marketplace_delivery_states", "billing.manual_order_invoice_documents", "billing.manual_order_invoice_delivery_states",
        "catalog.marketplace_product_links", "catalog.marketplace_variant_links", "catalog.marketplace_listing_states", "catalog.external_identifier_aliases",
        "catalog.category_mappings", "catalog.brand_mappings", "catalog.commission_tariffs", "catalog.promotion_offer_files",
        "catalog.import_sessions", "catalog.product_import_sessions", "catalog.product_import_staging_records", "catalog.catalog_structure_runs",
        "inventory.channel_offers", "integration.jobs", "integration.external_effect_records", "integration.reconciliation_runs",
        "integration.sync_cursors", "integration.connection_backoffs", "integration.reference_snapshots", "integration.reference_items",
        "integration.marketplace_question_sync_states", "integration.marketplace_finance_sync_states",
    ];

    private async Task<Dictionary<string, long>> CountsAsync(IEnumerable<string> tables, Guid connectionId)
    {
        await using var db = fixture.CreateContext();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var column = table is "billing.invoices" or "billing.invoice_policies" ? "ProviderConnectionId" : "ConnectionId";
            await using var command = new NpgsqlCommand($"""SELECT count(*) FROM {table} WHERE "{column}"=@connection""", connection);
            command.Parameters.AddWithValue("connection", connectionId);
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }
        return counts;
    }

    private OperationalDataMaintenanceService NewService(AppDbContext db) => new(db, fixture.TimeProvider, new NoDashboard(), NullLogger<OperationalDataMaintenanceService>.Instance);

    private sealed record Seed(Tenant Tenant, PlatformConnection Reset, PlatformConnection Other, PlatformConnection Provider, LegalEntityProfile Profile, InvoicePolicy Policy,
        Order ResetOrder, Order OtherOrder, Invoice OtherInvoice, Product ImportedProduct, Product PanelProduct, Product SharedProduct, Product OtherProduct,
        InventoryItem PanelStock, IntegrationJob ResetJob, IntegrationJob OtherJob, OperationalIssue ResetIssue, OperationalIssue OtherIssue, FileAsset ManualPdf);

    private Seed NewSeed(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var reset = Connection(tenant.Id, "TRENDYOL", $"{code}-reset");
        var other = Connection(tenant.Id, "TRENDYOL", $"{code}-other");
        var provider = Connection(tenant.Id, "TRENDYOL_EFATURAM", $"{code}-efaturam");
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "fr-profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        var resetOrder = NewOrder(tenant.Id, reset.Id, $"{code}-reset-order");
        var otherOrder = NewOrder(tenant.Id, other.Id, $"{code}-other-order");
        // A product an import of the store created carries the moment of its link; the panel's is hours older.
        var importedProduct = NewProduct(tenant.Id, Guid.CreateVersion7(fixture.Now), "İçe aktarılan ürün");
        var panelProduct = NewProduct(tenant.Id, Guid.CreateVersion7(fixture.Now.AddHours(-3)), "Panel ürünü");
        var sharedProduct = NewProduct(tenant.Id, Guid.CreateVersion7(fixture.Now), "Ortak ürün");
        var otherProduct = NewProduct(tenant.Id, Guid.CreateVersion7(fixture.Now), "Diğer mağaza ürünü");
        var panelStock = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = Guid.Empty, LocationCode = "MAIN", OnHand = 7m, Reserved = 1m, Available = 6m };
        var resetJob = Job(tenant.Id, reset.Id, MarketplaceJobTypes.ConnectionFullReset, JobStatus.Leased);
        var otherJob = Job(tenant.Id, other.Id, MarketplaceJobTypes.OrderSync, JobStatus.Pending);
        var resetIssue = Issue(tenant.Id, $"order-sync:{reset.Id:D}:page");
        var otherIssue = Issue(tenant.Id, $"order-sync:{other.Id:D}:page");
        var manualPdf = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = $"{tenant.Id:N}/manual.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = fixture.Now };
        var otherInvoice = NewInvoice(tenant.Id, otherOrder.Id, provider.Id, profile.Id, policy.Id);
        return new(tenant, reset, other, provider, profile, policy, resetOrder, otherOrder, otherInvoice, importedProduct, panelProduct, sharedProduct, otherProduct, panelStock, resetJob, otherJob, resetIssue, otherIssue, manualPdf);
    }

    private async Task SeedAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var tenantId = seed.Tenant.Id;
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Reset, seed.Other, seed.Provider);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
        db.Products.AddRange(seed.ImportedProduct, seed.PanelProduct, seed.SharedProduct, seed.OtherProduct);
        var importedVariant = NewVariant(tenantId, seed.ImportedProduct.Id, "FR-IMPORTED");
        var panelVariant = NewVariant(tenantId, seed.PanelProduct.Id, "FR-PANEL");
        var sharedVariant = NewVariant(tenantId, seed.SharedProduct.Id, "FR-SHARED");
        var otherVariant = NewVariant(tenantId, seed.OtherProduct.Id, "FR-OTHER");
        db.ProductVariants.AddRange(importedVariant, panelVariant, sharedVariant, otherVariant);
        seed.PanelStock.VariantId = panelVariant.Id;
        db.InventoryItems.Add(seed.PanelStock);
        db.MarketplaceProductLinks.AddRange(
            ProductLink(tenantId, seed.Reset.Id, seed.ImportedProduct.Id, Guid.CreateVersion7(fixture.Now.AddSeconds(1))),
            ProductLink(tenantId, seed.Reset.Id, seed.PanelProduct.Id, Guid.CreateVersion7(fixture.Now)),
            ProductLink(tenantId, seed.Reset.Id, seed.SharedProduct.Id, Guid.CreateVersion7(fixture.Now)),
            ProductLink(tenantId, seed.Other.Id, seed.SharedProduct.Id, Guid.CreateVersion7(fixture.Now)),
            ProductLink(tenantId, seed.Other.Id, seed.OtherProduct.Id, Guid.CreateVersion7(fixture.Now)));
        db.MarketplaceVariantLinks.AddRange(
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, VariantId = panelVariant.Id, ExternalId = "fr-panel-variant" },
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Other.Id, VariantId = otherVariant.Id, ExternalId = "fr-other-variant" });
        db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, VariantId = panelVariant.Id, DesiredStatus = "ACTIVE", ActualStatus = "ACTIVE" });
        db.ExternalIdentifierAliases.Add(new ExternalIdentifierAlias { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, EntityType = "PRODUCT", LocalId = seed.PanelProduct.Id, ExternalId = "fr-alias", CreatedAt = fixture.Now });
        foreach (var (connection, variant) in new[] { (seed.Reset, panelVariant), (seed.Other, otherVariant) })
        {
            var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 100m, SalePrice = 90m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_AWAY_FROM_ZERO", Status = "ACTIVE" };
            db.ChannelOffers.Add(offer);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = 1, ListPrice = 100m, SalePrice = 90m, Currency = "TRY", Reason = "TEST", ActorSource = "TEST", EffectiveAt = fixture.Now });
        }

        // The reset store's order: a line of the panel product holding stock, a package, its returns, its issued invoice, the
        // uploaded invoice document and the references the marketplace reported.
        db.Orders.AddRange(seed.ResetOrder, seed.OtherOrder);
        var resetPackage = NewPackage(tenantId, seed.Reset.Id, seed.ResetOrder.Id, "fr-reset-package");
        var otherPackage = NewPackage(tenantId, seed.Other.Id, seed.OtherOrder.Id, "fr-other-package");
        db.ShipmentPackages.AddRange(resetPackage, otherPackage);
        var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = seed.ResetOrder.Id, VariantId = panelVariant.Id, ExternalLineId = "fr-line", Sku = "FR-PANEL", TitleSnapshot = "Panel ürünü", OrderedQuantity = 3m, UnitPrice = 40m, VatRate = 20m, RawStatus = "Created" };
        db.OrderLines.Add(line);
        db.StockReservations.Add(new StockReservation { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = seed.PanelStock.Id, SourceType = "ORDER_LINE", SourceId = line.Id.ToString("D"), Quantity = 1m });
        db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = seed.PanelStock.Id, MovementType = "ORDER_STOCK_BASELINE", QuantityDelta = -3m, SourceType = "ORDER_LINE", SourceId = line.Id.ToString("D"), SourceEventId = "fr-line", IdempotencyKey = $"fr-ledger:{Guid.NewGuid():N}", OccurredAt = fixture.Now, RecordedAt = fixture.Now, CorrelationId = "corr" });
        db.ReturnClaims.Add(new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, OrderId = seed.ResetOrder.Id, ExternalClaimId = "fr-claim", RawStatus = "Created", LastRemoteModifiedAt = fixture.Now, CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
        var resetInvoice = NewInvoice(tenantId, seed.ResetOrder.Id, seed.Provider.Id, seed.Profile.Id, seed.Policy.Id);
        db.Invoices.AddRange(resetInvoice, seed.OtherInvoice);
        db.MarketplaceDeliveryStates.Add(new MarketplaceDeliveryState { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = resetInvoice.Id, ConnectionId = seed.Reset.Id, PackageId = resetPackage.Id, AttemptNumber = 1, ExternalIdempotencyKey = $"fr-delivery:{Guid.NewGuid():N}", RequestHash = "hash", DeliveryType = "LINK", Status = "DELIVERED", CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
        db.FileAssets.Add(seed.ManualPdf);
        var manual = new ManualOrderInvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = seed.ResetOrder.Id, PackageId = resetPackage.Id, ConnectionId = seed.Reset.Id, IdempotencyKey = $"fr-manual:{Guid.NewGuid():N}", DocumentType = "PDF", DocumentUrl = "https://example.invalid/fr.pdf", FileAssetId = seed.ManualPdf.Id, Status = "ACTIVE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        db.ManualOrderInvoiceDocuments.Add(manual);
        db.ManualOrderInvoiceDeliveryStates.Add(new ManualOrderInvoiceDeliveryState { Id = Guid.CreateVersion7(), TenantId = tenantId, DocumentId = manual.Id, ConnectionId = seed.Reset.Id, PackageId = resetPackage.Id, IdempotencyKey = $"fr-manual-delivery:{Guid.NewGuid():N}", RequestHash = "hash", Status = "DELIVERED", AttemptCount = 1, CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
        db.MarketplaceInvoiceReferenceHistories.Add(new MarketplaceInvoiceReferenceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = seed.ResetOrder.Id, PackageId = resetPackage.Id, ConnectionId = seed.Reset.Id, InvoiceNumber = "FR2026000000001", ChangeReason = "MARKETPLACE_REPORTED", ChangedAt = fixture.Now });

        foreach (var connection in new[] { seed.Reset, seed.Other })
        {
            var id = connection.Id;
            db.MarketplaceQuestions.Add(new MarketplaceQuestion { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, ExternalQuestionId = $"fr-q-{id:N}", Kind = "PRODUCT", Status = "WAITING_FOR_ANSWER", QuestionText = "Soru", CreatedAt = fixture.Now, LastRemoteModifiedAt = fixture.Now, LastSyncedAt = fixture.Now });
            db.MarketplaceQuestionSyncStates.Add(new MarketplaceQuestionSyncState { TenantId = tenantId, ConnectionId = id });
            db.MarketplaceFinanceTransactions.Add(new MarketplaceFinanceTransaction { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, PlatformCode = "TRENDYOL", Source = "SETTLEMENT", ExternalId = $"fr-tx-{id:N}", RequestedType = "Sale", Category = "SALE", TransactionDate = fixture.Now, RawJson = "{}", FirstSeenAt = fixture.Now, UpdatedAt = fixture.Now });
            db.MarketplaceCargoInvoiceItems.Add(new MarketplaceCargoInvoiceItem { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, InvoiceSerialNumber = "FR-CARGO", LineNo = 1, Amount = 10m, RawJson = "{}", FetchedAt = fixture.Now });
            db.MarketplaceFinanceSyncStates.Add(new MarketplaceFinanceSyncState { TenantId = tenantId, ConnectionId = id });
            var snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, ResourceType = "BRANDS", SourceVersion = "v1", ContentHash = $"fr-{id:N}", FetchedAt = fixture.Now, IsCurrent = true, ItemCount = 3 };
            db.ReferenceSnapshots.Add(snapshot);
            for (var index = 0; index < 3; index++)
                db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, SnapshotId = snapshot.Id, ResourceType = "BRANDS", ExternalId = $"brand-{index}", Name = $"Marka {index}", NormalizedName = $"marka {index}", Path = $"Marka {index}", IsLeaf = true, IsActive = true, PayloadHash = "hash" });
            db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, SnapshotId = snapshot.Id, LocalId = Guid.CreateVersion7(), ExternalId = "fr-category", Status = "MAPPED" });
            db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, SnapshotId = snapshot.Id, LocalId = Guid.CreateVersion7(), ExternalId = "fr-brand", Status = "MAPPED" });
            var tariff = new CommissionTariff { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, PlatformCode = "TRENDYOL", PeriodStart = fixture.Now, PeriodEnd = fixture.Now.AddDays(7), PeriodText = "1-7", FileName = "tarife.xlsx", FileHash = $"fr-{id:N}", UploadedAt = fixture.Now, RowCount = 1 };
            db.CommissionTariffs.Add(tariff);
            db.CommissionTariffRows.Add(new CommissionTariffRow { Id = Guid.CreateVersion7(), TenantId = tenantId, TariffId = tariff.Id, Barcode = "B1" });
            db.PromotionOfferFiles.Add(new PromotionOfferFile { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, PlatformCode = "TRENDYOL", Kind = PromotionOfferKinds.Advantage, FileName = "avantaj.xlsx", FileHash = $"fr-{id:N}", UploadedAt = fixture.Now, RowCount = 0, RowsJson = "[]" });
            var importSession = new ImportSession { Id = Guid.CreateVersion7(), TenantId = tenantId, SourceType = ImportSourceType.Marketplace, ConnectionId = id, CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
            db.ImportSessions.Add(importSession);
            db.ImportStagingRecords.Add(new ImportStagingRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, SessionId = importSession.Id, RowNumber = 1, RawJson = "{}", SafeValuesJson = "{}", ValidationErrorsJson = "[]", RowHash = "hash", ReviewStatus = "NONE" });
            var importJobId = Guid.CreateVersion7();
            db.ProductImportSessions.Add(new ProductImportSession { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, JobId = importJobId, Phase = "DONE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
            db.ProductImportStagingRecords.Add(new ProductImportStagingRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, JobId = importJobId, ModelKey = "model", ExternalProductId = "fr-remote", SnapshotHash = "hash", SnapshotJson = "{}", State = "FINALIZED", CreatedAt = fixture.Now });
            db.CatalogStructureRuns.Add(new CatalogStructureRun { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, Mode = "PREVIEW", Status = "DONE", CreatedAt = fixture.Now });
            db.ReconciliationRuns.Add(new ReconciliationRun { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, Scope = "ORDERS", Status = "DONE", StartedAt = fixture.Now });
            db.ExternalEffectRecords.Add(new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, EffectType = "PRICE", IdempotencyKey = $"fr-effect:{id:N}", CreatedAt = fixture.Now });
            db.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, ResourceType = "ORDERS", OpaqueCursor = "page-3", Version = 1 });
        }

        // The jobs: the running reset stays, the store's waiting job goes, the other store's waits on.
        db.IntegrationJobs.AddRange(seed.ResetJob, seed.OtherJob, Job(tenantId, seed.Reset.Id, MarketplaceJobTypes.OrderSync, JobStatus.Pending), Job(tenantId, seed.Reset.Id, MarketplaceJobTypes.ReturnSync, JobStatus.Dead));
        db.OperationalIssues.AddRange(seed.ResetIssue, seed.OtherIssue);

        // The settings of the reset store, which stay.
        db.PlatformCredentials.Add(new PlatformCredential { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, CredentialType = "BASIC", ProtectedPayload = "protected", MaskedHint = "***1", CreatedAt = fixture.Now, Version = 1 });
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, ResourceType = "ORDERS", IntervalSeconds = 180, Enabled = true });
        db.WebhookSubscriptions.Add(new WebhookSubscription { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, RouteTokenHash = $"fr-route-{Guid.NewGuid():N}", AuthenticationType = "API_KEY", ProtectedVerifierSecret = "secret", Status = "ACTIVE" });
        db.CargoProviderMappings.Add(new CargoProviderMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, ExternalProviderId = "YKMP", ExternalProviderName = "Yurtiçi", LocalProviderCode = "YURTICI", Status = "ACTIVE" });
        db.AttributeMappingTemplates.Add(new AttributeMappingTemplate { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = seed.Reset.Id, ExternalAttributeId = "47", LocalAttributeId = Guid.CreateVersion7(), UpdatedAt = fixture.Now });
        await db.SaveChangesAsync();

        foreach (var id in new[] { seed.Reset.Id, seed.Other.Id })
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO integration.connection_backoffs ("ConnectionId", "LockGroup", "Until", "Reason") VALUES ({id}, 'orders', {fixture.Now.AddMinutes(5)}, 'test')""");
    }

    private PlatformConnection Connection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} {storeId}",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private Order NewOrder(Guid tenantId, Guid connectionId, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = externalId,
        OrderNumber = externalId,
        Currency = "TRY",
        GrossAmount = 120m,
        NetAmount = 120m,
        OrderedAt = fixture.Now.AddDays(-1),
        LastRemoteModifiedAt = fixture.Now.AddDays(-1),
        CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = "CREATED",
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private ShipmentPackage NewPackage(Guid tenantId, Guid connectionId, Guid orderId, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalPackageId = externalId,
        GrossAmount = 120m,
        NetAmount = 120m,
        Status = ShipmentPackageStatus.New,
        RawStatus = "Created",
        StatusOccurredAt = fixture.Now,
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private Invoice NewInvoice(Guid tenantId, Guid orderId, Guid providerId, Guid profileId, Guid policyId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        OrderId = orderId,
        OrderFiscalGuarded = true,
        ProviderConnectionId = providerId,
        LegalEntityProfileId = profileId,
        InvoicePolicyId = policyId,
        InvoiceType = "EARSIVFATURA",
        SequencePurpose = "SALE",
        Status = InvoiceStatus.Completed,
        Currency = "TRY",
        TaxExclusiveTotal = 100m,
        TaxTotal = 20m,
        PayableTotal = 120m,
        Note = "test",
        IdempotencyKey = $"fr-invoice:{Guid.NewGuid():N}",
        InvoiceNumber = $"GIB{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)}",
        IssuedAt = fixture.Now.AddHours(-2),
        CreatedAt = fixture.Now.AddHours(-2),
        UpdatedAt = fixture.Now.AddHours(-2)
    };

    private Product NewProduct(Guid tenantId, Guid id, string title) => new() { Id = id, TenantId = tenantId, Title = title, Description = "Tam sıfırlama testi", Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now };

    private ProductVariant NewVariant(Guid tenantId, Guid productId, string sku) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ProductId = productId,
        Sku = sku,
        SkuNormalized = sku,
        Barcode = $"869{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
        BarcodeNormalized = sku,
        ModelCode = sku,
        OptionSignature = sku,
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now
    };

    private static MarketplaceProductLink ProductLink(Guid tenantId, Guid connectionId, Guid productId, Guid id) => new() { Id = id, TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = $"fr-{productId:N}", Version = 1 };

    private IntegrationJob Job(Seed seed, PlatformConnection connection, string jobType, JobStatus status) => Job(seed.Tenant.Id, connection.Id, jobType, status);

    private IntegrationJob Job(Guid tenantId, Guid connectionId, string jobType, JobStatus status)
    {
        var job = ConnectionReferencePlan.NewJob(tenantId, connectionId, jobType, $"fr-job:{Guid.NewGuid():N}", "{}", "corr", JobPriorityPolicy.Default, "user", fixture.Now);
        job.Status = status;
        return job;
    }

    private OperationalIssue Issue(Guid tenantId, string dedupeKey) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = dedupeKey, Code = "ORDER_SYNC", Summary = "Test", Status = IssueStatus.Open, FirstSeenAt = fixture.Now, LastSeenAt = fixture.Now, OccurrenceCount = 1 };

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM integration.connection_backoffs WHERE "ConnectionId" IN (SELECT "Id" FROM integration.platform_connections WHERE "TenantId"={{tenantId}});
            DELETE FROM integration.job_attempts WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.jobs WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.outbox_events WHERE "TenantId"={{tenantId}};
            DELETE FROM ops.audit_logs WHERE "TenantId"={{tenantId}};
            DELETE FROM ops.operational_issues WHERE "TenantId"={{tenantId}};
            DELETE FROM dashboard.snapshot WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.external_effect_records WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.reconciliation_runs WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.sync_cursors WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.reference_items WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.category_mappings WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.brand_mappings WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.reference_snapshots WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.marketplace_question_sync_states WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.marketplace_finance_sync_states WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.marketplace_questions WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.marketplace_finance_transactions WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.marketplace_cargo_invoice_items WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.commission_tariff_rows WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.commission_tariffs WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.promotion_offer_files WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.import_staging_records WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.import_sessions WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.product_import_staging_records WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.product_import_sessions WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.catalog_structure_runs WHERE "TenantId"={{tenantId}};
            DELETE FROM billing.marketplace_delivery_states WHERE "TenantId"={{tenantId}};
            DELETE FROM billing.manual_order_invoice_delivery_states WHERE "TenantId"={{tenantId}};
            DELETE FROM billing.manual_order_invoice_documents WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.marketplace_invoice_reference_history WHERE "TenantId"={{tenantId}};
            DELETE FROM billing.invoices WHERE "TenantId"={{tenantId}};
            DELETE FROM billing.invoice_policies WHERE "TenantId"={{tenantId}};
            DELETE FROM billing.legal_entity_profiles WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.return_claims WHERE "TenantId"={{tenantId}};
            DELETE FROM inventory.stock_reservations WHERE "TenantId"={{tenantId}};
            DELETE FROM inventory.stock_ledger_entries WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.order_lines WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.shipment_packages WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.orders WHERE "TenantId"={{tenantId}};
            DELETE FROM inventory.channel_price_history WHERE "TenantId"={{tenantId}};
            DELETE FROM inventory.channel_offers WHERE "TenantId"={{tenantId}};
            DELETE FROM inventory.inventory_items WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.marketplace_listing_states WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.external_identifier_aliases WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.marketplace_variant_links WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.marketplace_product_links WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.product_variants WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.products WHERE "TenantId"={{tenantId}};
            DELETE FROM ops.file_assets WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.platform_credentials WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.connection_sync_policies WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.webhook_subscriptions WHERE "TenantId"={{tenantId}};
            DELETE FROM sales.cargo_provider_mappings WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.attribute_mapping_templates WHERE "TenantId"={{tenantId}};
            DELETE FROM integration.platform_connections WHERE "TenantId"={{tenantId}};
            DELETE FROM iam.tenants WHERE "Id"={{tenantId}};
            """);
    }

    private sealed class NoDashboard : IDashboardReadService
    {
        public Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
