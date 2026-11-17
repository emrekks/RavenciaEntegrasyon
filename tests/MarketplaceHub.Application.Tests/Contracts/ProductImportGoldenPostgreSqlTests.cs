using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 3d (16 Nov 2026): the safety net for moving the catalog import out of the job processor. Each
// marketplace's product contract fixture is mapped as its contract test maps it and read by a full product import job into
// an empty store; the products, models, variants, offers, inventory rows, attribute assignments, model, product and variant
// links and issues it leaves are written into a golden (no ids or times). The golden must not change while the code moves.
// The reference read answers with a refusal: the import reads only the products and maps no category or brand on its own.
[Collection("postgres-integration")]
public sealed class ProductImportGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<string> Pages => new() { "Trendyol", "N11", "Pazarama" };

    [PostgreSqlTheory]
    [MemberData(nameof(Pages))]
    public async Task AProductPageImportsTheSameCatalog(string marketplace)
    {
        var (platform, products) = MappedPage(marketplace);
        Assert.NotEmpty(products);
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pgold-{marketplace.ToLowerInvariant()}-{Guid.NewGuid():N}"[..30], DisplayName = "golden", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = marketplace, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(products), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, """{"full":true,"includeArchived":true,"includeDrafts":true}""", $"product-golden-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }

        await using var check = fixture.CreateContext();
        var t = tenant.Id;
        var summary = new
        {
            Products = (await check.Products.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.Title, Status = x.Status.ToString() }).ToListAsync()).OrderBy(x => x.Title, StringComparer.Ordinal).ThenBy(x => x.Status, StringComparer.Ordinal).ToList(),
            Models = await check.ProductModels.AsNoTracking().CountAsync(x => x.TenantId == t),
            Variants = (await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.Sku, x.Barcode, x.ModelCode, x.OptionSignature, Status = x.Status.ToString() }).ToListAsync())
                .OrderBy(x => x.Sku, StringComparer.Ordinal).ThenBy(x => x.Barcode, StringComparer.Ordinal).ThenBy(x => x.ModelCode, StringComparer.Ordinal).ThenBy(x => x.OptionSignature, StringComparer.Ordinal).ToList(),
            Offers = (await check.ChannelOffers.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.SalePrice, x.ListPrice, x.Currency, x.VatRate, x.Status }).ToListAsync())
                .OrderBy(x => x.SalePrice).ThenBy(x => x.ListPrice).ThenBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.VatRate).ThenBy(x => x.Status.ToString(), StringComparer.Ordinal).ToList(),
            Inventory = (await check.InventoryItems.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.LocationCode, x.OnHand, x.Available }).ToListAsync())
                .OrderBy(x => x.OnHand).ThenBy(x => x.Available).ThenBy(x => x.LocationCode, StringComparer.Ordinal).ToList(),
            AttributeAssignments = await check.ProductAttributeAssignments.AsNoTracking().CountAsync(x => x.TenantId == t),
            ModelLinks = (await check.MarketplaceModelLinks.AsNoTracking().Where(x => x.TenantId == t).Select(x => x.ExternalId).ToListAsync()).Order(StringComparer.Ordinal).ToList(),
            ProductLinks = (await check.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.ExternalId, x.ExternalCode }).ToListAsync()).OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToList(),
            VariantLinks = (await check.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.ExternalId, x.ExternalBarcode, x.ExternalSku }).ToListAsync())
                .OrderBy(x => x.ExternalId, StringComparer.Ordinal).ThenBy(x => x.ExternalBarcode, StringComparer.Ordinal).ThenBy(x => x.ExternalSku, StringComparer.Ordinal).ToList(),
            Issues = (await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == t).Select(x => x.Code).ToListAsync()).Order(StringComparer.Ordinal).ToList()
        };
        ContractFixture.MatchesGolden($"{marketplace}/products-import.golden.json", summary);
    }

    private static (string Platform, IReadOnlyList<RemoteCatalogProduct> Products) MappedPage(string marketplace)
    {
        switch (marketplace)
        {
            case "Trendyol":
                return ("TRENDYOL", TrendyolJsonMapper.CatalogProducts(ContractFixture.Read("Trendyol/products-approved.json")).Items);
            case "N11":
                {
                    using var document = ContractFixture.Json("N11/product-query-docs-sample.json");
                    return ("N11", N11ProductMapper.Page(document.RootElement).Items);
                }
            default:
                {
                    using var document = ContractFixture.Json("Pazarama/products-approved-docs-sample.json");
                    return ("PAZARAMA", PazaramaProductMapper.Page(document.RootElement, pending: false).Items);
                }
        }
    }

    // One page holding every product of the fixture; nothing is written.
    private sealed class CatalogPage(IReadOnlyList<RemoteCatalogProduct> products) : IProductPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(products, null, false, products.Count)));

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

    private sealed class RefusingReferences : IReferenceDataPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(new AdapterError(AdapterErrorClass.NotSupported, "TEST_REFERENCES_REFUSED", "Referans okunmadı.", 400, null, null)));
    }
}
