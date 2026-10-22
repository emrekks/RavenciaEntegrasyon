using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Stock, price and publishing behaviour that needs the real schema (audit 2026-10-19, section 4.3).
[Collection("postgres-integration")]
public sealed class StockPricePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    // Jobs these tests queue (dispatches, price/stock pushes) stay PENDING; the lease tests in the same
    // database take any pending job, so every test removes the jobs of the tenants it created.
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    // K1: a manual adjustment reaches every connection with an ACTIVE offer for the variant.
    [PostgreSqlFact]
    public async Task ManualStockAdjustment_QueuesADispatchForEveryConnectionSellingTheVariant()
    {
        var tenant = NewTenant("stock-fan-out");
        var trendyol = NewConnection(tenant, "TRENDYOL", writesEnabled: true);
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var pazarama = NewConnection(tenant, "PAZARAMA", writesEnabled: true);
        var passive = NewConnection(tenant, "HEPSIBURADA", writesEnabled: true);
        passive.Status = "PASSIVE";
        // Writes off: the dispatch would send nothing, so none is queued (audit 2026-10-20 Q7).
        var writesOff = NewConnection(tenant, "HEPSIBURADA");
        var (product, variant) = NewVariant(tenant, "FAN-1", "8690000000011");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, n11, pazarama, passive, writesOff);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 5m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, trendyol, variant), NewOffer(tenant, n11, variant), NewOffer(tenant, pazarama, variant, status: "INACTIVE"), NewOffer(tenant, passive, variant), NewOffer(tenant, writesOff, variant));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());
        var result = await service.AdjustAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockAdjustmentCommand(3m, "Sayım farkı", "count-1"), $"adjust-{Guid.NewGuid():N}", "stock-fan-out", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var dispatchConnections = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.StockProjectionDispatch)
            .Select(x => x.ConnectionId)
            .ToListAsync();
        Assert.Equal(new Guid?[] { trendyol.Id, n11.Id }.OrderBy(x => x), dispatchConnections.OrderBy(x => x));
    }

    // K2: a candidate whose offer cannot be sent no longer stops the reconcile scan.
    [PostgreSqlFact]
    public async Task StockReconcile_ContinuesPastACandidateWithAnInvalidOffer()
    {
        var tenant = NewTenant("stock-reconcile-continue");
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var (product, invalid) = NewVariant(tenant, "REC-1", "8690000000028");
        var valid = NewVariant(tenant, "REC-2", "8690000000035", product.Id).Variant;
        var now = fixture.Now;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(invalid, valid);
            var invalidInventory = NewInventory(tenant, invalid, 4m);
            invalidInventory.ReconciledAt = now.AddHours(-10);
            var validInventory = NewInventory(tenant, valid, 6m);
            validInventory.ReconciledAt = now.AddHours(-5);
            seed.InventoryItems.AddRange(invalidInventory, validInventory);
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, invalid, currency: "USD"), NewOffer(tenant, n11, valid));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = invalid.Id, ExternalId = "REC-1" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = valid.Id, ExternalId = "REC-2" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOnConfiguration(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{\"lookbackHours\":1}", "stock-reconcile-continue", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorSummary);
        var pushes = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync).Select(x => x.PayloadJson).ToListAsync();
        Assert.Single(pushes);
        Assert.Contains(valid.Id.ToString(), pushes[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.Code == "CHANNEL_OFFER_INVALID"));
    }

    // A PTT AVM model variant read without a barcode is linked by its id ("FK4530/7101"); its price and stock go by
    // the panel barcode, while a variant linked by its barcode keeps that barcode.
    [PostgreSqlFact]
    public async Task PttAvmPriceStock_SendsThePanelBarcodeForAVariantLinkedByItsModelId()
    {
        var tenant = NewTenant("pttavm-model-variant");
        var pttAvm = NewConnection(tenant, "PTTAVM", writesEnabled: true);
        var (product, modelVariant) = NewVariant(tenant, "FK4530-S", "8690000000097");
        var plainVariant = NewVariant(tenant, "FK4530-M", "8690000000103", product.Id).Variant;
        // A panel variant without a barcode is known to PTT AVM by its stock code.
        var noBarcodeVariant = NewVariant(tenant, "FK4530-L", "unused", product.Id).Variant;
        noBarcodeVariant.Barcode = null;
        noBarcodeVariant.BarcodeNormalized = null;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(modelVariant, plainVariant, noBarcodeVariant);
            seed.InventoryItems.AddRange(NewInventory(tenant, modelVariant, 4m), NewInventory(tenant, plainVariant, 2m), NewInventory(tenant, noBarcodeVariant, 1m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, pttAvm, modelVariant), NewOffer(tenant, pttAvm, plainVariant), NewOffer(tenant, pttAvm, noBarcodeVariant));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = noBarcodeVariant.Id, ExternalId = "FK4530/7102" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = modelVariant.Id, ExternalId = "FK4530/7101" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = plainVariant.Id, ExternalId = "PTT-FK4530-M" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var barcodes = draft.Value!.Lines.ToDictionary(x => x.VariantId, x => x.Barcode);
        Assert.Equal("8690000000097", barcodes[modelVariant.Id]);
        Assert.Equal("PTT-FK4530-M", barcodes[plainVariant.Id]);
        Assert.Equal("FK4530-L", barcodes[noBarcodeVariant.Id]);
        Assert.DoesNotContain("FK4530/", draft.Value.PayloadJson, StringComparison.Ordinal);
    }

    // K6: an order line that cannot take its units from physical stock keeps them reserved, also under a
    // DISABLED or missing connection inventory policy (the policy fields are not applied).
    [PostgreSqlFact]
    public async Task OrderShortfall_IsReservedUnderADisabledOrMissingInventoryPolicy()
    {
        var tenant = NewTenant("reservation-unconditional");
        var withDisabledPolicy = NewConnection(tenant, "TRENDYOL");
        var withoutPolicy = NewConnection(tenant, "N11");
        var (product, disabledVariant) = NewVariant(tenant, "RES-1", "8690000000073");
        var missingVariant = NewVariant(tenant, "RES-2", "8690000000080", product.Id).Variant;
        var disabledInventory = NewInventory(tenant, disabledVariant, 0m);
        var missingInventory = NewInventory(tenant, missingVariant, 0m);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(withDisabledPolicy, withoutPolicy);
            seed.ConnectionInventoryPolicies.Add(new ConnectionInventoryPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = withDisabledPolicy.Id, AuthorityMode = InventoryAuthorityPolicy.Central, ReservationMode = "DISABLED", ReserveOnStatuses = "", ReleaseOnStatuses = "" });
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(disabledVariant, missingVariant);
            seed.InventoryItems.AddRange(disabledInventory, missingInventory);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var project = typeof(MarketplaceJobProcessor).GetMethod("ProjectOrderReservations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var (connection, variant) in new[] { (withDisabledPolicy, disabledVariant), (withoutPolicy, missingVariant) })
        {
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = Guid.CreateVersion7(), VariantId = variant.Id, ExternalLineId = $"line-{variant.Sku}", Sku = variant.Sku, TitleSnapshot = variant.Sku, OrderedQuantity = 2m, RawStatus = "Created" };
            await (Task)project.Invoke(processor, [tenant.Id, connection.Id, new List<(OrderLine, DateTimeOffset)> { (line, fixture.Now) }, CancellationToken.None])!;
        }
        await db.SaveChangesAsync();

        foreach (var inventory in new[] { disabledInventory, missingInventory })
        {
            var reservation = await db.StockReservations.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.InventoryItemId == inventory.Id);
            Assert.Equal(ReservationStatus.Active, reservation.Status);
            Assert.Equal(2m, reservation.Quantity);
            Assert.NotNull(reservation.ExpiresAt);
            Assert.Equal(2m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == inventory.Id)).Reserved);
        }
    }

    // K10: a barcode another product already lists on the same connection is refused before create.
    [PostgreSqlFact]
    public async Task Publication_RefusesABarcodeAnotherProductUsesOnTheSameConnection()
    {
        var tenant = NewTenant("barcode-duplicate");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var otherStore = NewConnection(tenant, "TRENDYOL");
        var (listedProduct, listedVariant) = NewVariant(tenant, "DUP-OLD", "8690000000042");
        var (newProduct, newVariant) = NewVariant(tenant, "DUP-NEW", "8690000000042-B");
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, ProductId = listedProduct.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, otherStore);
            seed.Products.AddRange(listedProduct, newProduct);
            seed.ProductVariants.AddRange(listedVariant, newVariant);
            seed.ChannelListingProfiles.Add(profile);
            seed.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = listedVariant.Id, ExternalBarcode = "8690000000042", DesiredStatus = "LIVE", ActualStatus = "LIVE" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        Assert.Equal("8690000000042", await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenant.Id, trendyol.Id, [newVariant.Id], ["8690000000042"], [], CancellationToken.None));
        // The product's own listing, and another store of the same seller, are not duplicates.
        Assert.Null(await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenant.Id, trendyol.Id, [listedVariant.Id], ["8690000000042"], [], CancellationToken.None));
        Assert.Null(await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenant.Id, otherStore.Id, [newVariant.Id], ["8690000000042"], [], CancellationToken.None));
    }

    // K11: an active offer without a MAIN stock row is reported instead of silently skipped.
    [PostgreSqlFact]
    public async Task PriceInventoryBuild_ReportsAnOfferWithoutInventory()
    {
        var tenant = NewTenant("inventory-required");
        var n11 = NewConnection(tenant, "N11");
        var (product, withoutInventory) = NewVariant(tenant, "INV-0", "8690000000059");
        var withInventory = NewVariant(tenant, "INV-1", "8690000000066", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(withoutInventory, withInventory);
            seed.InventoryItems.Add(NewInventory(tenant, withInventory, 2m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, withoutInventory), NewOffer(tenant, n11, withInventory));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = withoutInventory.Id, ExternalId = "INV-0" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = withInventory.Id, ExternalId = "INV-1" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var composer = new PriceInventoryComposer(db);
        var single = await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, withoutInventory.Id);
        Assert.False(single.Succeeded);
        Assert.Equal("INVENTORY_REQUIRED", single.Error!.Code);

        var all = await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None);
        Assert.True(all.Succeeded, all.Error?.Message);
        Assert.Equal([withInventory.Id], all.Value!.Lines.Select(x => x.VariantId));
        Assert.Equal(["INV-0"], all.Value.MissingInventorySkus);
    }

    private static IConfiguration WritesOnConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();

    private static Tenant NewTenant(string code)
    {
        var tenant = NewTenantRow(code);
        CreatedTenants.Add(tenant.Id);
        return tenant;
    }

    private static Tenant NewTenantRow(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}",
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode, bool writesEnabled = false) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} stok testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = writesEnabled ? "{\"ExternalWritesEnabled\":true}" : "{}"
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, string barcode, Guid? productId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = productId ?? Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Stok testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = product.Id,
            Sku = sku,
            SkuNormalized = sku.ToUpperInvariant(),
            Barcode = barcode,
            BarcodeNormalized = barcode.ToUpperInvariant(),
            ModelCode = "MODEL-1",
            OptionSignature = sku,
            CreatedAt = now,
            UpdatedAt = now
        };
        return (product, variant);
    }

    private static InventoryItem NewInventory(Tenant tenant, ProductVariant variant, decimal onHand) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        VariantId = variant.Id,
        LocationCode = "MAIN",
        OnHand = onHand,
        Available = onHand
    };

    private static ChannelOffer NewOffer(Tenant tenant, PlatformConnection connection, ProductVariant variant, string status = "ACTIVE", string currency = "TRY") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        VariantId = variant.Id,
        ListPrice = 150m,
        SalePrice = 120m,
        Currency = currency,
        VatRate = 20m,
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        Status = status
    };
}
