using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
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

    // PTT AVM keeps a product with variants by its model code and names each variant by its colour and size: the send
    // is one product with the changed variants, priced from the lowest variant with each variant's difference. A
    // product without variants goes by its own code with its stock, and an old option-id link is not sent.
    [PostgreSqlFact]
    public async Task PttAvmPriceStock_SendsAProductWithItsVariantsByColourAndSize()
    {
        var tenant = NewTenant("pttavm-model-variant");
        var pttAvm = NewConnection(tenant, "PTTAVM", writesEnabled: true);
        var (product, small) = NewVariant(tenant, "FK4530-S", "8690000000097");
        var medium = NewVariant(tenant, "FK4530-M", "8690000000103", product.Id).Variant;
        small.OptionSignature = "Renk: Lacivert | Beden: S";
        medium.OptionSignature = "Renk: Lacivert | Beden: M";
        var (single, singleVariant) = NewVariant(tenant, "QU2410121", "QU2410121");
        var (stale, staleVariant) = NewVariant(tenant, "OLD-1", "8690000000110");
        var mediumOffer = NewOffer(tenant, pttAvm, medium);
        mediumOffer.SalePrice = 130m;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.AddRange(product, single, stale);
            seed.ProductVariants.AddRange(small, medium, singleVariant, staleVariant);
            seed.InventoryItems.AddRange(NewInventory(tenant, small, 4m), NewInventory(tenant, medium, 2m), NewInventory(tenant, singleVariant, 3m), NewInventory(tenant, staleVariant, 1m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, pttAvm, small), mediumOffer, NewOffer(tenant, pttAvm, singleVariant), NewOffer(tenant, pttAvm, staleVariant));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = small.Id, ExternalId = "FK4530-S" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = medium.Id, ExternalId = "FK4530-M" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = singleVariant.Id, ExternalId = "QU2410121" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = staleVariant.Id, ExternalId = "OLD/7101" });
            seed.MarketplaceProductLinks.AddRange(
                new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = product.Id, ExternalId = "1523598789", ExternalCode = "FK4530" },
                new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = single.Id, ExternalId = "1523598790", ExternalCode = "QU2410121" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var items = PttAvmPriceInventoryRules.Items(draft.Value!.PayloadJson)!;
        Assert.Equal(2, items.Count);
        var model = items.Single(x => x.Barcode == "FK4530");
        Assert.Equal(120.00m, model.PriceWithVat);
        Assert.Null(model.Quantity);
        Assert.Equal(2, model.Variants!.Count);
        Assert.Contains(new PttAvmPriceInventoryRules.VariantItem([new("Renk", "Lacivert"), new("Beden", "S")], 4, 0m), model.Variants);
        Assert.Contains(new PttAvmPriceInventoryRules.VariantItem([new("Renk", "Lacivert"), new("Beden", "M")], 2, 10m), model.Variants);
        Assert.Equal(new PttAvmPriceInventoryRules.Item("QU2410121", 120.00m, 20m, 3), items.Single(x => x.Barcode == "QU2410121"));
        // Each line is one variant (its own code) and answers under the product's PTT AVM barcode.
        var lines = draft.Value.Lines.ToDictionary(x => x.VariantId);
        Assert.Equal(("FK4530-S", "FK4530"), (lines[small.Id].Barcode, lines[small.Id].ExternalVariantId));
        Assert.Equal(("QU2410121", "QU2410121"), (lines[singleVariant.Id].Barcode, lines[singleVariant.Id].ExternalVariantId));
        Assert.False(lines.ContainsKey(staleVariant.Id));
        Assert.DoesNotContain("/7101", draft.Value.PayloadJson, StringComparison.Ordinal);
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

    // Codes typed in the product editor: a changed code starts without the marketplace's barcode and stock code (the
    // next import reads them), two variants may hand a code over in one save, a code another product's variant holds is
    // refused with that variant's stock code, and an empty code removes the link.
    // An uploaded tariff is stored once per file and period, its rows match panel variants by barcode, and the
    // lookup answers the band rate only inside the period.
    [PostgreSqlFact]
    public async Task CommissionTariff_UploadListRowsLookupAndDelete()
    {
        var tenant = NewTenant("commission-tariff");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var (product, variant) = NewVariant(tenant, "RYP00101", "RYP00101");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, shopify);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            await seed.SaveChangesAsync();
        }
        string[] headers = ["ÜRÜN İSMİ", "BARKOD", "SATICI STOK KODU", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit", "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti", "Tarih aralığı (7 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "KOMİSYONA ESAS FİYAT", "GÜNCEL KOMİSYON", "GÜNCEL TSF"];
        var file = CommissionTariffTests.Xlsx(headers,
        [
            ["Bluz", "RYP00101", "RYP00101", "470.64", "470.63", "435.98", "435.97", "388.64", "388.63", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "20.6", "19.4", "17.5", "519.75", "21.5", "525.0"],
            ["Etek", "NOTINPANEL", "NOTINPANEL", "300", "299.99", "250", "249.99", "200", "199.99", "22 Ekim 08.00-29 Ekim 07.59", "20", "19", "18", "17", "260", "19", "270"]
        ]);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

        Guid tariffId;
        await using (var db = fixture.CreateContext())
        {
            var service = new CommissionTariffService(db, clock);
            var uploaded = await service.UploadAsync(tenant.Id, null, trendyol.Id, "tarife.xlsx", new MemoryStream(file), "test", CancellationToken.None);
            Assert.True(uploaded.Succeeded, uploaded.Error?.Message);
            var tariff = Assert.Single(uploaded.Value!.Tariffs);
            Assert.Equal(2, uploaded.Value.ImportedRows);
            Assert.False(uploaded.Value.AlreadyUploaded);
            Assert.Equal(1, tariff.MatchedRowCount);
            Assert.True(tariff.IsCurrent);
            tariffId = tariff.Id;

            var refused = await service.UploadAsync(tenant.Id, null, shopify.Id, "tarife.xlsx", new MemoryStream(file), "test", CancellationToken.None);
            Assert.Equal("COMMISSION_TARIFF_PLATFORM_UNSUPPORTED", refused.Error?.Code);
        }
        await using (var db = fixture.CreateContext())
        {
            var service = new CommissionTariffService(db, clock);
            var again = await service.UploadAsync(tenant.Id, null, trendyol.Id, "kopya.xlsx", new MemoryStream(file), "test", CancellationToken.None);
            Assert.True(again.Value!.AlreadyUploaded);
            Assert.Equal(tariffId, Assert.Single(again.Value.Tariffs).Id);

            var rows = await service.RowsAsync(tenant.Id, trendyol.Id, tariffId, "ryp", 0, 50, new CommissionTariffRowFilter(null, null), CancellationToken.None);
            var row = Assert.Single(rows.Value!.Items);
            Assert.Equal(1, row.CurrentTier);
            Assert.Equal(21.5m, row.CurrentBandRate);
            Assert.Equal(470.63m, row.NextLowerBand?.Max);
            Assert.True(row.MatchesPanel);
            Assert.Equal(4, row.Bands.Count);
        }
        await using (var db = fixture.CreateContext())
        {
            var lookup = new CommissionTariffLookup(db);
            var inPeriod = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(3));
            Assert.Equal(20.6m, (await lookup.RateAsync(tenant.Id, trendyol.Id, "RYP00101", null, inPeriod, 450m, CancellationToken.None))?.Rate);
            Assert.Equal(17.5m, (await lookup.RateAsync(tenant.Id, trendyol.Id, "UNKNOWN", "RYP00101", inPeriod, 300m, CancellationToken.None))?.Rate);
            Assert.Null(await lookup.RateAsync(tenant.Id, trendyol.Id, "RYP00101", null, new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.FromHours(3)), 450m, CancellationToken.None));
        }
        await using (var db = fixture.CreateContext())
        {
            var deleted = await new CommissionTariffService(db, clock).DeleteAsync(tenant.Id, null, trendyol.Id, tariffId, "test", CancellationToken.None);
            Assert.True(deleted.Succeeded);
            Assert.False(await db.CommissionTariffRows.AnyAsync(x => x.TariffId == tariffId));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [PostgreSqlFact]
    public async Task ProductMarketplaceCodes_SaveTypedCodes()
    {
        var tenant = NewTenant("product-marketplace-codes-save");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var (product, small) = NewVariant(tenant, "SAVE-S", "8690000000302");
        var medium = NewVariant(tenant, "SAVE-M", "8690000000319", product.Id).Variant;
        var (otherProduct, other) = NewVariant(tenant, "OTHER-S", "8690000000326");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(hepsiburada, shopify);
            seed.Products.AddRange(product, otherProduct);
            seed.ProductVariants.AddRange(small, medium, other);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = small.Id, ExternalId = "HBCV00001ABCD", ExternalSku = "SAVE-S", ExternalBarcode = "8690000000302" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = other.Id, ExternalId = "HBCV0000OTHER", ExternalSku = "OTHER-S" });
            await seed.SaveChangesAsync();
        }
        CatalogService Service(AppDbContext context) => new(context, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        await using (var db = fixture.CreateContext())
        {
            var saved = await Service(db).UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([
                new(small.Id, hepsiburada.Id, " HBCV00001NEW1 "),
                new(medium.Id, hepsiburada.Id, "HBCV00001ABCD"),
                new(small.Id, shopify.Id, "gid://shopify/ProductVariant/44791234567899"),
                new(medium.Id, shopify.Id, "")]), CancellationToken.None);

            Assert.True(saved.Succeeded, saved.Error?.Message);
            var codes = saved.Value!.Variants.ToDictionary(x => x.VariantId, x => x.Codes.ToDictionary(code => code.ConnectionId));
            Assert.Equal("HBCV00001NEW1", codes[small.Id][hepsiburada.Id].ExternalId);
            Assert.Null(codes[small.Id][hepsiburada.Id].ExternalSku);
            Assert.Equal("HBCV00001ABCD", codes[medium.Id][hepsiburada.Id].ExternalId);
            Assert.Equal("44791234567899", codes[small.Id][shopify.Id].ExternalId);
            Assert.False(codes[medium.Id].ContainsKey(shopify.Id));
        }

        await using (var db = fixture.CreateContext())
        {
            var refused = await Service(db).UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([new(medium.Id, hepsiburada.Id, "HBCV0000OTHER")]), CancellationToken.None);

            Assert.Equal("MARKETPLACE_CODE_IN_USE", refused.Error?.Code);
            Assert.Contains("OTHER-S", refused.Error!.Message, StringComparison.Ordinal);
            Assert.True(refused.Error.FieldErrors!.ContainsKey(CatalogService.MarketplaceCodeField(medium.Id, hepsiburada.Id)));
        }

        await using (var db = fixture.CreateContext())
        {
            var cleared = await Service(db).UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([new(small.Id, hepsiburada.Id, "  ")]), CancellationToken.None);

            Assert.True(cleared.Succeeded, cleared.Error?.Message);
            Assert.DoesNotContain(cleared.Value!.Variants.Single(x => x.VariantId == small.Id).Codes, code => code.ConnectionId == hepsiburada.Id);
            Assert.Equal("HBCV00001ABCD", cleared.Value.Variants.Single(x => x.VariantId == medium.Id).Codes.Single(code => code.ConnectionId == hepsiburada.Id).ExternalId);
        }
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

    // The panel keeps one model as a product per colour (as Trendyol lists it) while PTT AVM keeps it as one product:
    // every variant is linked, and price and stock go as one product with all of the model's variants.
    [PostgreSqlFact]
    public async Task PttAvmModelSplitByColour_IsMappedAndSentAsOneProduct()
    {
        var tenant = NewTenant("pttavm-colour-products");
        var pttAvm = NewConnection(tenant, "PTTAVM", writesEnabled: true);
        var (navy, navyXl) = NewVariant(tenant, "MZDT002ECD05", "MZDT002ECD05");
        var (black, blackXl) = NewVariant(tenant, "MZDT002ECD09", "MZDT002ECD09");
        navyXl.ModelCode = blackXl.ModelCode = "MZDT002ECD";
        navyXl.OptionSignature = "Renk: Lacivert | Beden: XL";
        blackXl.OptionSignature = "Renk: Siyah | Beden: XL";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.AddRange(navy, black);
            seed.ProductVariants.AddRange(navyXl, blackXl);
            seed.InventoryItems.AddRange(NewInventory(tenant, navyXl, 2m), NewInventory(tenant, blackXl, 3m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, pttAvm, navyXl), NewOffer(tenant, pttAvm, blackXl));
            await seed.SaveChangesAsync();
        }
        RemoteCatalogVariant Option(string id, string value) => new($"MZDT002ECD/{id}", $"MZDT002ECD-{value}", null, "MZDT002ECD", new Dictionary<string, string> { ["Varyant"] = value }, false, 120m, null, 20m, 0m, "TRY", "{}");
        var snapshot = new RemoteCatalogProduct("1533250134", "MZDT002ECD", "Tunik", "", null, null, null, null, [], [Option("1", "Lacivert"), Option("2", "Siyah"), Option("3", "XL")], "{}");

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var map = typeof(MarketplaceJobProcessor).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True(await (Task<bool>)map.Invoke(processor, [tenant.Id, pttAvm.Id, snapshot, false, CancellationToken.None, true, true, false, null, "PTTAVM", true])!);
            await db.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        var links = await check.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id).ToDictionaryAsync(x => x.VariantId, x => x.ExternalId);
        Assert.Equal("MZDT002ECD05", links[navyXl.Id]);
        Assert.Equal("MZDT002ECD09", links[blackXl.Id]);
        var draft = await new PriceInventoryComposer(check).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None);
        Assert.True(draft.Succeeded, draft.Error?.Message);
        var item = Assert.Single(PttAvmPriceInventoryRules.Items(draft.Value!.PayloadJson)!);
        Assert.Equal("MZDT002ECD", item.Barcode);
        Assert.Equal(2, item.Variants!.Count);
        // A send started from one colour's product covers the whole model.
        var single = await new PriceInventoryComposer(check).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None, blackXl.Id);
        Assert.Equal(2, Assert.Single(PttAvmPriceInventoryRules.Items(single.Value!.PayloadJson)!).Variants!.Count);
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

    // A link the mapping re-keyed in memory (old id → variant code) is still found by its stored id; recording the
    // read codes passes over it instead of failing the whole product.
    [PostgreSqlFact]
    public async Task ProductImport_LinkCodesSkipALinkReKeyedInTheSameBatch()
    {
        var tenant = NewTenant("import-link-rekeyed");
        var pttAvm = NewConnection(tenant, "PTTAVM");
        var (product, variant) = NewVariant(tenant, "M1-S", "8690000000203");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = variant.Id, ExternalId = "MODEL-1/7101" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var link = await db.MarketplaceVariantLinks.SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id);
        link.ExternalId = "M1-S";
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var record = typeof(MarketplaceJobProcessor).GetMethod("RecordVariantLinkCodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var remote = new RemoteCatalogVariant("MODEL-1/7101", "MODEL-1-S", null, "MODEL-1", new Dictionary<string, string> { ["Varyant"] = "S" }, false, 100m, null, 20m, 1m, "TRY", "{}");
        var snapshots = new List<RemoteCatalogProduct> { new("900001", "MODEL-1", "Etek", "", null, null, null, null, [], [remote], "{}") };

        await (Task)record.Invoke(processor, [tenant.Id, pttAvm.Id, snapshots, CancellationToken.None])!;

        Assert.Equal("M1-S", link.ExternalId);
        Assert.Null(link.ExternalSku);
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
