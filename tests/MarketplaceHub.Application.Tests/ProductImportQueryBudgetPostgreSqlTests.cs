using System.Data.Common;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Speed (owner, 12.11.2026: "Neden yavaş çalışıyor?"): a Trendyol product pull rebuilds its category context for every
// model, and that context maps every value the category's attributes list. Live, a 1,379-product pull spent ~5 s per
// model on ~4,000 queries, three per attribute value. The values are now read once per attribute: the reads of the
// attribute's values, value mappings and value snapshot do not grow with the number of values, and the mapping result
// is the one the per-value queries gave (a custom mapping of another panel value is reused, not duplicated).
[Collection("postgres-integration")]
public sealed class ProductImportQueryBudgetPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string CategoryExternalId = "123";
    private const string MaterialExternalId = "10";
    private const int MaterialValues = 120;
    private static readonly string[] Models = ["QB001", "QB002", "QB003"];

    [PostgreSqlFact]
    public async Task CategoryContext_ReadsAttributeValuesOncePerAttribute_AndMapsEveryValueAsBefore()
    {
        var seed = await SeedAsync();
        var port = new CatalogPort(TrendyolJsonMapper.CatalogProducts(Page()).Items);

        var first = await RunAsync(seed, port);
        var second = await RunAsync(seed, port);

        // Three models, each rebuilding the category context: a few dozen reads per model at most, not three per value (360+).
        Assert.True(first <= Models.Length * 40, $"first run read attribute values/mappings/value snapshots {first} times");
        Assert.True(second <= Models.Length * 40, $"second run read attribute values/mappings/value snapshots {second} times");

        await using var db = fixture.CreateContext();
        var scope = $"{CategoryExternalId}/{MaterialExternalId}";
        var mappings = await db.AttributeValueMappings.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.ConnectionId == seed.ConnectionId && x.ScopeExternalId == scope).ToListAsync();
        var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToDictionaryAsync(x => x.Id);
        // One mapping per remote value, none twice.
        Assert.Equal(MaterialValues, mappings.Count);
        Assert.Equal(MaterialValues, mappings.Select(x => x.ExternalId).Distinct().Count());
        Assert.All(mappings, mapping => Assert.Equal("VERIFIED", mapping.Status));
        // The owner's custom mapping of the first value to another panel value stays that one row.
        var custom = Assert.Single(mappings, x => x.ExternalId == ValueId(1));
        Assert.Equal(seed.CustomValueId, custom.LocalId);
        // Every other remote value maps to the panel value of its own name.
        foreach (var mapping in mappings.Where(x => x.ExternalId != ValueId(1)))
        {
            var index = int.Parse(mapping.ExternalId.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(ValueName(index).ToUpperInvariant(), values[mapping.LocalId].NormalizedValue);
        }
        // The values that already existed were reused, the rest made once.
        Assert.Equal(MaterialValues + 1, values.Values.Count(x => x.AttributeId == values[seed.CustomValueId].AttributeId));
    }

    private static string ValueId(int index) => $"{MaterialExternalId}-{index}";
    private static string ValueName(int index) => $"Malzeme{index:000}";

    // Speed (12 Nov 2026, live: catalog.product_variants read ~660 times a minute during a pull): a plain pull of a kept
    // card checked every listed size the panel lacks with a query of its own. The sizes are now checked in one query per
    // card, however many there are, and the notice counts the same new sizes as before.
    [PostgreSqlFact]
    public async Task PlainPull_ChecksTheNewSizesOfAKeptCardInOneQuery_AndCountsThemAsBefore()
    {
        var seed = await SeedAsync();
        string[] newSizes = ["XS", "L", "XL", "XXL", "3XL", "4XL", "5XL", "6XL", "34", "36", "38", "40"];
        await RunAsync(seed, new CatalogPort(TrendyolJsonMapper.CatalogProducts(Page()).Items), "{\"full\":true,\"updateExistingProducts\":true}", new VariantKeyLookupCounter());

        var counter = new VariantKeyLookupCounter();
        await RunAsync(seed, new CatalogPort(TrendyolJsonMapper.CatalogProducts(Page(["S", "M", .. newSizes])).Items), "{\"full\":true,\"updateExistingProducts\":false}", counter);

        // One lookup of the panel's stock codes and barcodes per kept card, not one per new size (36 before).
        Assert.True(counter.Reads <= Models.Length, $"the new-size check read product variants by key {counter.Reads} times");
        await using var db = fixture.CreateContext();
        var issues = await db.OperationalIssues.AsNoTracking().Where(row => row.TenantId == seed.TenantId && row.Code == "PRODUCT_NEW_SIZES_NOT_IMPORTED").ToListAsync();
        Assert.Equal(Models.Length, issues.Count);
        Assert.All(issues, issue => Assert.Contains($"{newSizes.Length} yeni beden var;", issue.Summary, StringComparison.Ordinal));
        // The plain pull kept the cards whole.
        Assert.Equal(Models.Length * 2, await db.ProductVariants.CountAsync(row => row.TenantId == seed.TenantId));
    }

    private static string Page() => Page(["S", "M"]);

    private static string Page(IReadOnlyList<string> sizes) => JsonSerializer.Serialize(new
    {
        content = Models.Select((model, modelIndex) => new
        {
            contentId = 7_100_000 + modelIndex,
            productMainId = model,
            title = $"Bluz {model}",
            description = "Bluz.",
            brand = new { id = 456L, name = "Ravencia" },
            category = new { id = long.Parse(CategoryExternalId, System.Globalization.CultureInfo.InvariantCulture), name = "Bluz" },
            images = Array.Empty<object>(),
            attributes = new[] { new { attributeName = "Materyal", attributeValue = ValueName(modelIndex + 2) } },
            variants = sizes.Select((size, sizeIndex) => new
            {
                variantId = 7_200_000L + modelIndex * 100 + sizeIndex,
                barcode = $"869710{modelIndex:00}{sizeIndex:00}",
                stockCode = $"{model}-{size}",
                attributes = new[] { new { attributeName = "Beden", attributeValue = size } },
                salePrice = 100m,
                listPrice = 120m,
                quantity = 3,
                vatRate = 10,
                dimensionalWeight = 1m,
                onSale = true,
                archived = false
            }).ToList()
        }).ToList(),
        page = 0,
        size = 100,
        totalPages = 1,
        totalElements = Models.Length
    });

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid CustomValueId);

    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"querybudget-{Guid.NewGuid():N}", DisplayName = "querybudget", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        // Fresh reference snapshots: the import reads no reference from Trendyol.
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}-{scope}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        ReferenceItem Item(Guid snapshotId, string type, string externalId, string name, bool? required = null, int? sortOrder = null) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, IsRequired = required, SortOrder = sortOrder, PayloadHash = $"payload-{type}-{externalId}" };
        var categories = Snapshot("CATEGORIES", "");
        var attributes = Snapshot("CATEGORY_ATTRIBUTES", CategoryExternalId);
        var materialValues = Snapshot("ATTRIBUTE_VALUES", $"{CategoryExternalId}/{MaterialExternalId}");
        db.ReferenceSnapshots.AddRange(categories, attributes, materialValues);
        db.ReferenceItems.Add(Item(categories.Id, "CATEGORIES", CategoryExternalId, "Bluz"));
        db.ReferenceItems.Add(Item(attributes.Id, "CATEGORY_ATTRIBUTES", MaterialExternalId, "Materyal", required: true));
        for (var index = 1; index <= MaterialValues; index++)
            db.ReferenceItems.Add(Item(materialValues.Id, "ATTRIBUTE_VALUES", ValueId(index), ValueName(index), sortOrder: index));

        // The panel already holds the attribute with a third of the values, and the owner mapped the first remote value
        // to a value of another name (Koton).
        var attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Code = $"TRD_{CategoryExternalId}_{MaterialExternalId}", Name = "Materyal", DataType = AttributeDataType.SingleSelect, SelectionMode = "SINGLE", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.AttributeDefinitions.Add(attribute);
        var custom = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = attribute.Id, Value = "Koton", NormalizedValue = "KOTON", SortOrder = 0, IsActive = true, Version = 1 };
        db.AttributeValues.Add(custom);
        for (var index = 1; index <= MaterialValues / 3; index++)
            db.AttributeValues.Add(new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = attribute.Id, Value = ValueName(index), NormalizedValue = ValueName(index).ToUpperInvariant(), SortOrder = index, IsActive = true, Version = 1 });
        db.AttributeValueMappings.Add(new AttributeValueMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = materialValues.Id, LocalId = custom.Id, ScopeExternalId = $"{CategoryExternalId}/{MaterialExternalId}", ExternalId = ValueId(1), Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, custom.Id);
    }

    // The reads of attribute values, value mappings and value snapshots one full import made.
    private static async Task<int> RunAsync(Seed seed, CatalogPort port)
    {
        var counter = new ValueReadCounter();
        await RunAsync(seed, port, "{\"full\":true,\"updateExistingProducts\":true}", counter);
        return counter.Reads;
    }

    private static async Task RunAsync(Seed seed, CatalogPort port, string payloadJson, ReadCounter counter)
    {
        var connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(counter).Options, TimeProvider.System);
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), TimeProvider.System);
        var result = await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ProductSync, payloadJson, $"query-budget-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorSummary);
    }

    private sealed class ValueReadCounter : ReadCounter
    {
        protected override bool Counts(string text) =>
            text.Contains("catalog.attribute_values ", StringComparison.Ordinal)
            || text.Contains("catalog.attribute_value_mappings ", StringComparison.Ordinal)
            || text.Contains("\"ResourceType\" = 'ATTRIBUTE_VALUES'", StringComparison.Ordinal);
    }

    // The new-size check's read: panel variants by stock-code keys or barcode keys, nothing else.
    private sealed class VariantKeyLookupCounter : ReadCounter
    {
        protected override bool Counts(string text) =>
            text.Contains("FROM catalog.product_variants", StringComparison.Ordinal)
            && text.Contains("\"SkuNormalized\" = ANY", StringComparison.Ordinal)
            && text.Contains("\"BarcodeNormalized\" = ANY", StringComparison.Ordinal)
            && !text.Contains("\"ModelCode\"", StringComparison.Ordinal)
            && !text.Contains("JOIN", StringComparison.Ordinal);
    }

    private abstract class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        protected abstract bool Counts(string text);

        private void Count(DbCommand command)
        {
            var text = command.CommandText;
            if (!text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;
            if (Counts(text))
                Reads++;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CatalogPort(IReadOnlyList<RemoteCatalogProduct> snapshots) : IProductPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(snapshots, null, false, snapshots.Count)));

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
