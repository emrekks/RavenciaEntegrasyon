using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// MoveMz021yVariantsToColorCards on PostgreSQL, with the live ids: a mini MZ021Y-CD family whose Mavi card carries rows of
// other colours. The rows that are still as found move to their colour's card with their attribute, change and option
// rows; a row linked to a marketplace or whose colour card is missing stays; the true Mavi rows, stock, offers and the old
// card's option values stay; a second run changes nothing.
[Collection("postgres-integration")]
public sealed class MoveMz021yVariantsToColorCardsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Guid Mavi = Guid.Parse("01a178e0-9ecb-790a-bbf7-30da96781869");
    private static readonly Guid Bordo = Guid.Parse("01a178e0-9186-7325-964c-02c23b7bfc32");
    private static readonly Guid Murdum = Guid.Parse("01a198bc-37ca-7c58-903f-7ca1f48682fd");
    private static readonly Guid Siyah = Guid.Parse("01a178e0-95ef-7dec-a4e3-5e7fca8d8f38");
    private static readonly Guid BordoL = Guid.Parse("01a19900-aa47-7fbc-9b55-5f3fb7fe83ff");
    private static readonly Guid BordoXl = Guid.Parse("01a19900-aa48-7c16-8e6c-5066a4408d27");
    private static readonly Guid Murdum3Xl = Guid.Parse("01a19900-aa48-7eab-807c-b9f1b3cba9bc");
    private static readonly Guid SiyahLLinked = Guid.Parse("01a19900-aa48-713c-baa9-2c432948b8cf");
    private static readonly Guid KahverengiLNoCard = Guid.Parse("01a19900-aa48-7140-9ccd-facf91f09f23");
    private static readonly Guid MaviL = Guid.Parse("01a19900-aa48-7f1f-8ed8-7df8c31b258c");
    private static readonly Guid Haki = Guid.Parse("01a178e0-a37a-7b28-b55e-7e2fb6ab847d");
    private static readonly Guid Bordo2XlAlreadyOnCard = Guid.Parse("01a19900-aa48-744b-a6a1-e454e2e14bae");
    private static readonly Guid HakiLArchivedCard = Guid.Parse("01a19900-aa48-77b3-bf2b-5f1ac4ff5941");
    private static readonly Guid SiyahXlListed = Guid.Parse("01a19900-aa48-77f0-b282-fabd9cce73ac");

    [PostgreSqlFact]
    public async Task Moves_the_unlinked_rows_to_their_colour_card_once_and_leaves_the_rest()
    {
        await DeleteLeftoversAsync();
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"mz021y-move-{Guid.NewGuid():N}", DisplayName = "mz021y-move", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "MZ021Y test", ExternalStoreId = "seller-mz021y", Status = "ACTIVE", ApiVersion = "V1" };
        var maviTrueRow = Guid.CreateVersion7();
        var maviRenk = Guid.CreateVersion7();
        var maviBeden = Guid.CreateVersion7();
        var maviRenkBordo = Guid.CreateVersion7();
        var maviBedenL = Guid.CreateVersion7();
        var bordoBeden = Guid.CreateVersion7();
        var bordoRenk = Guid.CreateVersion7();
        var bedenAttribute = Guid.CreateVersion7();
        var bedenLValue = Guid.CreateVersion7();
        var inventoryId = Guid.CreateVersion7();
        var bordoLinkId = Guid.CreateVersion7();
        var maviLinkId = Guid.CreateVersion7();
        var murdumLinkId = Guid.CreateVersion7();

        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.Add(connection);
            foreach (var (id, title) in new[] { (Mavi, "Mavi"), (Bordo, "Bordo"), (Murdum, "Mürdüm"), (Siyah, "Siyah"), (Haki, "Haki") })
                db.Products.Add(new Product { Id = id, TenantId = tenant.Id, Title = $"Kadın {title} Bluz", Description = "Bluz", Status = id == Haki ? ProductStatus.Archived : ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 10 });
            ProductVariant Row(Guid id, Guid productId, string sku, string signature) => new() { Id = id, TenantId = tenant.Id, ProductId = productId, Sku = sku, SkuNormalized = sku, Barcode = sku, BarcodeNormalized = sku, ModelCode = "MZ021Y-CD", OptionSignature = signature, Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 3, DefaultListPrice = 900m, DefaultSalePrice = 750m };
            db.ProductVariants.AddRange(
                Row(BordoL, Mavi, "MZ021Y-CD-01", "Renk: Bordo | Beden: L"),
                Row(BordoXl, Mavi, "MZ021Y-CD-02", "Renk: Bordo | Beden: XL"),
                Row(Murdum3Xl, Mavi, "MZ021Y-CD-34", "Renk: Mürdüm | Beden: 3XL"),
                Row(SiyahLLinked, Mavi, "MZ021Y-CD-37", "Renk: Siyah | Beden: L"),
                Row(KahverengiLNoCard, Mavi, "MZ021Y-CD-13", "Renk: Kahverengi | Beden: L"),
                Row(MaviL, Mavi, "MZ021Y-CD-25", "Renk: Mavi | Beden: L"),
                Row(maviTrueRow, Mavi, "MZ021Y-CD13", "Renk: Mavi | Beden: 3XL"),
                Row(Guid.CreateVersion7(), Bordo, "MZ021Y-CD7", "Renk: Bordo | Beden: 3XL"),
                Row(Guid.CreateVersion7(), Murdum, "MZ021Y-CD19", "Renk: Mürdüm | Beden: L"),
                Row(Guid.CreateVersion7(), Siyah, "MZ021Y-CD10", "Renk: Siyah | Beden: 3XL"),
                // Bordo 2XL: the Bordo card has that size already. Haki L: the Haki card is archived. Siyah XL: listed.
                Row(Bordo2XlAlreadyOnCard, Mavi, "MZ021Y-CD-03", "Renk: Bordo | Beden: 2XL"),
                Row(Guid.CreateVersion7(), Bordo, "MZ021Y-CD8", "Renk: Bordo | Beden: 2XL"),
                Row(HakiLArchivedCard, Mavi, "MZ021Y-CD-07", "Renk: Haki | Beden: L"),
                Row(Guid.CreateVersion7(), Haki, "MZ021Y-CD4", "Renk: Haki | Beden: 3XL"),
                Row(SiyahXlListed, Mavi, "MZ021Y-CD-38", "Renk: Siyah | Beden: XL"));
            db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = SiyahXlListed, DesiredStatus = "LIVE", ActualStatus = "APPROVAL_PENDING" });
            // Mavi carries the wrong colour's values from the bad save; Bordo has Renk and Beden; Mürdüm has Renk only.
            db.ProductOptions.AddRange(
                new ProductOption { Id = maviBeden, TenantId = tenant.Id, ProductId = Mavi, Label = "Beden", NormalizedKey = "BEDEN", SortOrder = 0 },
                new ProductOption { Id = maviRenk, TenantId = tenant.Id, ProductId = Mavi, Label = "Renk", NormalizedKey = "RENK", SortOrder = 1 },
                new ProductOption { Id = bordoBeden, TenantId = tenant.Id, ProductId = Bordo, Label = "Beden", NormalizedKey = "BEDEN", SortOrder = 0 },
                new ProductOption { Id = bordoRenk, TenantId = tenant.Id, ProductId = Bordo, Label = "Renk", NormalizedKey = "RENK", SortOrder = 1 },
                new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = Murdum, Label = "Renk", NormalizedKey = "RENK", SortOrder = 1 });
            db.ProductOptionValues.AddRange(
                new ProductOptionValue { Id = maviBedenL, TenantId = tenant.Id, OptionId = maviBeden, Label = "L", NormalizedKey = "L" },
                new ProductOptionValue { Id = maviRenkBordo, TenantId = tenant.Id, OptionId = maviRenk, Label = "Bordo", NormalizedKey = "BORDO" },
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = maviRenk, Label = "Mavi", NormalizedKey = "MAVI", SortOrder = 1 },
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = bordoBeden, Label = "3XL", NormalizedKey = "3XL" },
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = bordoRenk, Label = "Bordo", NormalizedKey = "BORDO", SortOrder = 1 });
            // CD-01 has its option rows under the Mavi card's options; CD-02 has none (as live).
            db.VariantOptionValues.AddRange(
                new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = BordoL, OptionId = maviRenk, OptionValueId = maviRenkBordo },
                new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = BordoL, OptionId = maviBeden, OptionValueId = maviBedenL });
            db.AttributeDefinitions.Add(new AttributeDefinition { Id = bedenAttribute, TenantId = tenant.Id, Code = $"beden-{tenant.Id:N}", Name = "Beden", DataType = AttributeDataType.SingleSelect, CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
            db.AttributeValues.Add(new AttributeValue { Id = bedenLValue, TenantId = tenant.Id, AttributeId = bedenAttribute, Value = "L", NormalizedValue = "L" });
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = Mavi, VariantId = BordoL, AttributeId = bedenAttribute, ValueId = bedenLValue, Version = 1 });
            db.ProductChangeEntries.Add(new ProductChangeEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = Mavi, VariantId = BordoL, Kind = "DEFAULT_PRICE", NewSalePrice = 750m, NewListPrice = 900m, Source = "EDITOR", CorrelationId = "seed", OccurredAt = fixture.Now });
            db.InventoryItems.Add(new InventoryItem { Id = inventoryId, TenantId = tenant.Id, VariantId = BordoL, LocationCode = "MAIN", OnHand = 1m, Available = 1m });
            db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = inventoryId, MovementType = "MANUAL_ADJUSTMENT", QuantityDelta = 1m, SourceType = "MANUAL", SourceId = "seed", SourceEventId = $"seed-{tenant.Id:N}", IdempotencyKey = $"seed-{tenant.Id:N}", OccurredAt = fixture.Now, RecordedAt = fixture.Now, CorrelationId = "seed" });
            db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = BordoL, ListPrice = 900m, SalePrice = 750m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" });
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = SiyahLLinked, ExternalId = "ty-siyah-l", Version = 1 });
            db.MarketplaceProductLinks.AddRange(
                new MarketplaceProductLink { Id = bordoLinkId, TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = Bordo, ExternalId = "ty-bordo", SyncStatus = "SYNCED", Version = 1 },
                new MarketplaceProductLink { Id = maviLinkId, TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = Mavi, ExternalId = "ty-mavi", SyncStatus = "LOCAL_CHANGES_PENDING", DirtyFieldsJson = """["product", "media"]""", Version = 1 },
                new MarketplaceProductLink { Id = murdumLinkId, TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = Murdum, ExternalId = "ty-murdum", SyncStatus = "LOCAL_CHANGES_PENDING", DirtyFieldsJson = null, Version = 1 });
            await db.SaveChangesAsync();
        }

        try
        {
            // Twice in one transaction, as in EF's migration transaction (the ON COMMIT DROP tables are still there for the
            // second run), then once more on its own.
            var migration = new MoveMz021yVariantsToColorCards();
            await using (var db = fixture.CreateContext())
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                for (var run = 0; run < 2; run++)
                    foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                        await db.Database.ExecuteSqlRawAsync(operation.Sql);
                await transaction.CommitAsync();
            }
            await using (var db = fixture.CreateContext())
                foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                    await db.Database.ExecuteSqlRawAsync(operation.Sql);

            await using var check = fixture.CreateContext();
            var variants = await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
            Assert.Equal((Bordo, 4L), (variants[BordoL].ProductId, variants[BordoL].Version));
            Assert.Equal((Bordo, 4L), (variants[BordoXl].ProductId, variants[BordoXl].Version));
            Assert.Equal((Murdum, 4L), (variants[Murdum3Xl].ProductId, variants[Murdum3Xl].Version));
            // Linked or listed on a marketplace, colour card missing, archived or already carrying the size, true Mavi
            // rows: stay where they are.
            foreach (var stays in new[] { SiyahLLinked, SiyahXlListed, KahverengiLNoCard, HakiLArchivedCard, Bordo2XlAlreadyOnCard, MaviL, maviTrueRow })
                Assert.Equal((Mavi, 3L), (variants[stays].ProductId, variants[stays].Version));
            // Codes, prices and stock are untouched.
            Assert.Equal(("MZ021Y-CD-01", "MZ021Y-CD-01", 900m, 750m), (variants[BordoL].Sku, variants[BordoL].Barcode, variants[BordoL].DefaultListPrice, variants[BordoL].DefaultSalePrice));
            Assert.Equal(1m, (await check.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == inventoryId)).OnHand);
            Assert.Equal(1, await check.StockLedgerEntries.CountAsync(x => x.TenantId == tenant.Id));
            Assert.Equal(750m, (await check.ChannelOffers.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).SalePrice);

            // The variant's dependent rows follow it.
            Assert.Equal(Bordo, (await check.ProductAttributeAssignments.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).ProductId);
            Assert.Equal(Bordo, (await check.ProductChangeEntries.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).ProductId);

            // Option rows: each moved row has Renk and Beden under its new card's options, no duplicates after two runs.
            var optionRows = await (from vov in check.VariantOptionValues.AsNoTracking()
                                    join option in check.ProductOptions.AsNoTracking() on vov.OptionId equals option.Id
                                    join value in check.ProductOptionValues.AsNoTracking() on vov.OptionValueId equals value.Id
                                    where vov.TenantId == tenant.Id
                                    select new { vov.VariantId, option.ProductId, Option = option.NormalizedKey, Value = value.Label, ValueOption = value.OptionId, OptionId = option.Id }).ToListAsync();
            Assert.All(optionRows, row => Assert.Equal(row.OptionId, row.ValueOption));
            string Options(Guid variantId) => string.Join(",", optionRows.Where(x => x.VariantId == variantId).OrderBy(x => x.Option).Select(x => $"{x.Option}={x.Value}@{(x.ProductId == Bordo ? "Bordo" : x.ProductId == Murdum ? "Murdum" : x.ProductId.ToString())}"));
            Assert.Equal("BEDEN=L@Bordo,RENK=Bordo@Bordo", Options(BordoL));
            Assert.Equal("BEDEN=XL@Bordo,RENK=Bordo@Bordo", Options(BordoXl));
            Assert.Equal("BEDEN=3XL@Murdum,RENK=Mürdüm@Murdum", Options(Murdum3Xl));
            Assert.Equal(6, optionRows.Count);
            var options = await check.ProductOptions.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
            var values = await check.ProductOptionValues.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
            Assert.Equal(["3XL", "L", "XL"], values.Where(x => x.OptionId == bordoBeden).Select(x => x.Label).Order(StringComparer.Ordinal));
            Assert.Equal(["Bordo"], values.Where(x => x.OptionId == bordoRenk).Select(x => x.Label));
            var murdumBeden = Assert.Single(options, x => x.ProductId == Murdum && x.NormalizedKey == "BEDEN");
            Assert.Equal(["3XL"], values.Where(x => x.OptionId == murdumBeden.Id).Select(x => x.Label));
            Assert.Single(values, x => options.Single(o => o.Id == x.OptionId).ProductId == Murdum && x.NormalizedKey == "MURDUM");
            // Nothing on the old card is deleted.
            Assert.Equal(3, values.Count(x => x.OptionId == maviRenk || x.OptionId == maviBeden));

            // Both cards of a move take one new version; a card no row moved to keeps its own.
            var products = await check.Products.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Version);
            Assert.Equal((11L, 11L, 11L, 10L, 10L), (products[Mavi], products[Bordo], products[Murdum], products[Siyah], products[Haki]));
            var links = await check.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
            Assert.Equal(("LOCAL_CHANGES_PENDING", 2L), (links[bordoLinkId].SyncStatus, links[bordoLinkId].Version));
            Assert.Contains("\"product\"", links[bordoLinkId].DirtyFieldsJson);
            // Already marked as a local product change: left as it is. Pending with no field list: takes "product".
            Assert.Equal(1L, links[maviLinkId].Version);
            Assert.Equal(("LOCAL_CHANGES_PENDING", 2L), (links[murdumLinkId].SyncStatus, links[murdumLinkId].Version));
            Assert.Contains("\"product\"", links[murdumLinkId].DirtyFieldsJson);

            var audits = await check.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.Action == "VARIANT_MOVED_TO_COLOR_CARD").Select(x => x.TargetId).ToListAsync();
            Assert.Equal(new[] { BordoL, BordoXl, Murdum3Xl }.Select(x => x.ToString()).Order(), audits.Order());
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // A run that died before its cleanup leaves rows with the fixed live ids behind; they go before the seed.
    private async Task DeleteLeftoversAsync()
    {
        await using var db = fixture.CreateContext();
        var tenantIds = await db.Products.AsNoTracking().Where(x => x.Id == Mavi || x.Id == Bordo || x.Id == Murdum || x.Id == Siyah || x.Id == Haki).Select(x => x.TenantId).Distinct().ToListAsync();
        foreach (var tenantId in tenantIds) await DeleteTenantAsync(tenantId);
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.AuditLogs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductChangeEntries.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.StockLedgerEntries.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InventoryItems.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ChannelOffers.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceProductLinks.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.VariantOptionValues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductOptionValues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductOptions.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductAttributeAssignments.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AttributeValues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AttributeDefinitions.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }
}
