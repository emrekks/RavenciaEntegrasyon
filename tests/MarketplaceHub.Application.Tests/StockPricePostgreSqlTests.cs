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

    // An imported Trendyol variant has no listing rows; its price and stock go by the barcode Trendyol holds,
    // which differs from the panel's when the seller keyed it differently there.
    [PostgreSqlFact]
    public async Task ImportedTrendyolVariant_IsSentByTheMarketplaceBarcode()
    {
        var tenant = NewTenant("imported-trendyol-codes");
        var trendyol = NewConnection(tenant, "TRENDYOL", writesEnabled: true);
        var (product, variant) = NewVariant(tenant, "IMP-1", "8690000000110");
        var withoutCodes = NewVariant(tenant, "IMP-2", "8690000000127", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(variant, withoutCodes);
            seed.InventoryItems.AddRange(NewInventory(tenant, variant, 3m), NewInventory(tenant, withoutCodes, 3m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, trendyol, variant), NewOffer(tenant, trendyol, withoutCodes));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = variant.Id, ExternalId = "777001", ExternalBarcode = "TY-BARKOD-1", ExternalSku = "TY-STOK-1" },
                // Linked before the codes were kept: nothing to address it by until the next import.
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = withoutCodes.Id, ExternalId = "777002" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, trendyol.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var line = Assert.Single(draft.Value!.Lines);
        Assert.Equal(variant.Id, line.VariantId);
        Assert.Equal("TY-BARKOD-1", line.Barcode);
    }

    // Shopify addresses a variant by its own id: an imported variant without a barcode is still sent, keyed by it.
    [PostgreSqlFact]
    public async Task ImportedShopifyVariant_IsSentByItsVariantId()
    {
        var tenant = NewTenant("imported-shopify-variant-id");
        var shopify = NewConnection(tenant, "SHOPIFY", writesEnabled: true);
        var (product, withoutBarcode) = NewVariant(tenant, "SHP-1", "8690000000165");
        var withBarcode = NewVariant(tenant, "SHP-2", "8690000000172", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(shopify);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(withoutBarcode, withBarcode);
            seed.InventoryItems.AddRange(NewInventory(tenant, withoutBarcode, 2m), NewInventory(tenant, withBarcode, 5m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, shopify, withoutBarcode), NewOffer(tenant, shopify, withBarcode));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = withoutBarcode.Id, ExternalId = "44791234567890", ExternalSku = "SHP-1" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = withBarcode.Id, ExternalId = "44791234567891", ExternalBarcode = "SH-BARKOD-2", ExternalSku = "SHP-2" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, shopify.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var lines = draft.Value!.Lines.ToDictionary(x => x.VariantId);
        Assert.Equal("44791234567890", lines[withoutBarcode.Id].Barcode);
        Assert.Equal("44791234567890", lines[withoutBarcode.Id].ExternalVariantId);
        Assert.Equal("SH-BARKOD-2", lines[withBarcode.Id].Barcode);
        Assert.Equal("44791234567891", lines[withBarcode.Id].ExternalVariantId);
        Assert.Contains("\"variantId\":\"44791234567890\"", draft.Value.PayloadJson, StringComparison.Ordinal);
    }

    // The product editor lists the Hepsiburada and Shopify codes of each variant.
    [PostgreSqlFact]
    public async Task ProductMarketplaceCodes_ListTheHepsiburadaAndShopifyCodesOfEachVariant()
    {
        var tenant = NewTenant("product-marketplace-codes");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, small) = NewVariant(tenant, "CODE-S", "8690000000189");
        var medium = NewVariant(tenant, "CODE-M", "8690000000196", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(hepsiburada, shopify, trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(small, medium);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = small.Id, ExternalId = "HBCV00001ABCD", ExternalSku = "CODE-S" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = small.Id, ExternalId = "44791234567892" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = medium.Id, ExternalId = "999" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new CatalogService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var result = await service.GetProductMarketplaceCodesAsync(tenant.Id, product.Id, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(new[] { "HEPSIBURADA", "SHOPIFY" }, result.Value!.Connections.Select(x => x.PlatformCode));
        var smallCodes = result.Value.Variants.Single(x => x.VariantId == small.Id).Codes;
        Assert.Equal("HBCV00001ABCD", smallCodes.Single(x => x.ConnectionId == hepsiburada.Id).ExternalId);
        Assert.Equal("44791234567892", smallCodes.Single(x => x.ConnectionId == shopify.Id).ExternalId);
        // A Trendyol link is not a Hepsiburada or Shopify code.
        Assert.Empty(result.Value.Variants.Single(x => x.VariantId == medium.Id).Codes);
    }

    // A PTT AVM product with variants is kept by its model code and lists only its colour and size values: the
    // panel variants of the model are linked by their own stock codes (PTT AVM's variant stock codes), an earlier
    // option-id link is rewritten, and a variant whose size PTT AVM does not list stays unlinked and is reported.
    [PostgreSqlFact]
    public async Task PttAvmModelMapping_LinksTheModelsPanelVariantsByTheirStockCodes()
    {
        var tenant = NewTenant("pttavm-model-codes");
        var pttAvm = NewConnection(tenant, "PTTAVM");
        var (product, small) = NewVariant(tenant, "M1-S", "8690000000203");
        var medium = NewVariant(tenant, "M1-M", "8690000000210", product.Id).Variant;
        var large = NewVariant(tenant, "M1-L", "8690000000227", product.Id).Variant;
        small.OptionSignature = "Renk: Lacivert | Beden: S";
        medium.OptionSignature = "Renk: Lacivert | Beden: M";
        large.OptionSignature = "Renk: Lacivert | Beden: L";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(small, medium, large);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = medium.Id, ExternalId = "MODEL-1/7102" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = large.Id, ExternalId = "MODEL-1/7103" });
            await seed.SaveChangesAsync();
        }
        RemoteCatalogVariant Option(string id, string value) => new($"MODEL-1/{id}", $"MODEL-1-{value}", null, "MODEL-1", new Dictionary<string, string> { ["Varyant"] = value }, false, 100m, null, 20m, 1m, "TRY", "{}");
        var snapshot = new RemoteCatalogProduct("900001", "MODEL-1", "Mini Etek", "", null, null, null, null, [], [Option("7100", "Lacivert"), Option("7101", "S"), Option("7102", "M")], "{}");

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var map = typeof(MarketplaceJobProcessor).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var mapped = await (Task<bool>)map.Invoke(processor, [tenant.Id, pttAvm.Id, snapshot, false, CancellationToken.None, true, true, false, null, "PTTAVM", true])!;
        await db.SaveChangesAsync();

        Assert.True(mapped);
        var links = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id).ToDictionaryAsync(x => x.VariantId);
        Assert.Equal("M1-S", links[small.Id].ExternalId);
        Assert.Equal("8690000000203", links[small.Id].ExternalBarcode);
        Assert.Equal("M1-M", links[medium.Id].ExternalId);
        Assert.Equal("M1-M", links[medium.Id].ExternalSku);
        // PTT AVM lists no size L for the model: the old option-id link is removed and the variant is reported.
        Assert.False(links.ContainsKey(large.Id));
        Assert.True(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.Code == "PRODUCT_MAPPING_UNMATCHED" && x.Summary.Contains("bu renk/beden yok: L")));
        var productLink = await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id);
        Assert.Equal("900001", productLink.ExternalId);
        Assert.Equal("MODEL-1", productLink.ExternalCode);
    }

    // A PTT AVM product without variants is kept by the product's own code, which may differ from its panel model code.
    [PostgreSqlFact]
    public async Task PttAvmMapping_LinksAProductWithoutVariantsByItsOwnCode()
    {
        var tenant = NewTenant("pttavm-single-code");
        var pttAvm = NewConnection(tenant, "PTTAVM");
        var (product, variant) = NewVariant(tenant, "QU2410121", "QU2410121");
        variant.ModelCode = "QUAC241012";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            await seed.SaveChangesAsync();
        }
        var remote = new RemoteCatalogVariant("QU2410121", "QU2410121", "QU2410121", null, new Dictionary<string, string>(), false, 499m, null, 10m, 3m, "TRY", "{}");
        var snapshot = new RemoteCatalogProduct("900002", "QU2410121", "Bluz", "", null, null, null, null, [], [remote], "{}");

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var map = typeof(MarketplaceJobProcessor).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var mapped = await (Task<bool>)map.Invoke(processor, [tenant.Id, pttAvm.Id, snapshot, false, CancellationToken.None, true, true, false, null, "PTTAVM", true])!;
        await db.SaveChangesAsync();

        Assert.True(mapped);
        Assert.Equal("QU2410121", (await db.MarketplaceVariantLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id)).ExternalId);
        Assert.Equal("QU2410121", (await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id)).ExternalCode);
    }

    // An import keeps the marketplace's barcode and stock code on the links it read, whatever code paired them.
    [PostgreSqlFact]
    public async Task ProductImport_RecordsTheMarketplaceCodesOnItsLinks()
    {
        var tenant = NewTenant("import-link-codes");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, variant) = NewVariant(tenant, "PANEL-STOK", "8690000000158");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = variant.Id, ExternalId = "888001" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var record = typeof(MarketplaceJobProcessor).GetMethod("RecordVariantLinkCodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var remote = new RemoteCatalogVariant("888001", " TY-STOK ", "TY-BARKOD", null, new Dictionary<string, string>(), false, 100m, 120m, 20m, 2m, "TRY", "{}");
        var snapshots = new List<RemoteCatalogProduct> { new("p-1", null, "Ürün", "", null, null, null, null, [], [remote], "{}") };
        await (Task)record.Invoke(processor, [tenant.Id, trendyol.Id, snapshots, CancellationToken.None])!;
        await db.SaveChangesAsync();

        var link = await db.MarketplaceVariantLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == trendyol.Id);
        Assert.Equal("TY-BARKOD", link.ExternalBarcode);
        Assert.Equal("TY-STOK", link.ExternalSku);
        Assert.Equal(2, link.Version);
    }

    // An order line names the variant by the marketplace's codes; a link holding them finds the panel variant
    // even when the panel's barcode and stock code differ.
    [PostgreSqlFact]
    public async Task OrderLine_ResolvesThroughTheConnectionsVariantLinkCodes()
    {
        var tenant = NewTenant("order-line-link-codes");
        var n11 = NewConnection(tenant, "N11");
        var other = NewConnection(tenant, "PAZARAMA");
        var (product, linked) = NewVariant(tenant, "PANEL-1", "8690000000134");
        var panelMatch = NewVariant(tenant, "N11-STOK-9", "8690000000141", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(n11, other);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(linked, panelMatch);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = linked.Id, ExternalId = "N11-STOK-9", ExternalBarcode = "N11-BARKOD-9", ExternalSku = "N11-STOK-9" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = other.Id, VariantId = panelMatch.Id, ExternalId = "N11-BARKOD-9" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var resolve = typeof(MarketplaceJobProcessor).GetMethod("ResolveOrderLineVariantIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var lines = new List<RemoteOrderLine> { new("line-1", "N11-STOK-9", "N11-BARKOD-9", "Ürün", 1m, 100m, 20m, "Created"), new("line-2", "PANEL-ONLY", "8690000000141", "Ürün", 1m, 100m, 20m, "Created") };
        var keys = await (Task<Dictionary<string, Guid>>)resolve.Invoke(processor, [tenant.Id, n11.Id, lines, CancellationToken.None])!;

        // The n11 stock code is also a panel SKU of another variant; the n11 link wins on its own connection.
        Assert.Equal(linked.Id, keys["s:N11-STOK-9"]);
        Assert.Equal(linked.Id, keys["b:N11-BARKOD-9"]);
        // A code no link of this connection names still resolves by the panel's codes.
        Assert.Equal(panelMatch.Id, keys["b:8690000000141"]);
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
