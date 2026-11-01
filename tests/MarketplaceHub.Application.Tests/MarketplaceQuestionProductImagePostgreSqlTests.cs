using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// n11 sends a question with no image, stock code or barcode (live: "Kadın Bordo Düğmeli Kalın Esnek Anne Triko Yelek
// Standart"). The panel finds its product by the n11 productId link or by the store's own order line with the same
// listing title, and shows that product's photo; a title two panel products answer to keeps the placeholder.
[Collection("postgres-integration")]
public sealed class MarketplaceQuestionProductImagePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AnN11QuestionWithoutCodesShowsThePhotoOfTheOneProductItNames()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"question-image-{Guid.NewGuid():N}", DisplayName = "question-image", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var n11 = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "N11", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(n11);
        ProductVariant Product(string sku, string image)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, ModelCode = sku, OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = image, MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
            db.AddRange(product, variant, asset, new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" });
            return variant;
        }
        void Ordered(string title, string sku)
        {
            var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ExternalOrderId = $"o-{Guid.NewGuid():N}", OrderNumber = $"o-{Guid.NewGuid():N}"[..20], Currency = "TRY", GrossAmount = 10m, NetAmount = 10m, OrderedAt = now, LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.AddRange(order, new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = "1", Sku = sku, TitleSnapshot = title, OrderedQuantity = 1, UnitPrice = 10m, VatRate = 10m, RawStatus = "Delivered" });
        }
        MarketplaceQuestion Question(string productName, string? productId = null)
        {
            var question = new MarketplaceQuestion { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ExternalQuestionId = $"q-{Guid.NewGuid():N}", Kind = "PRODUCT", Status = "WAITING_FOR_ANSWER", QuestionText = "Beden?", ProductName = productName, ProductExternalId = productId, CreatedAt = now, LastRemoteModifiedAt = now, LastSyncedAt = now, Version = 1 };
            db.MarketplaceQuestions.Add(question);
            return question;
        }
        Product("MZ0024YLK01", "https://cdn.example.com/yelek.jpg");
        Ordered("Kadın Bordo Düğmeli Kalın Esnek Anne Triko Yelek Standart", "MZ0024YLK01");
        var linked = Product("MZ025DY30", "https://cdn.example.com/bluz.jpg");
        db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ProductId = linked.ProductId, ExternalId = "n11:752773818", SyncStatus = "SYNCED" });
        Product("AMB-1", "https://cdn.example.com/a.jpg");
        Product("AMB-2", "https://cdn.example.com/b.jpg");
        Ordered("İki Ürünlü Başlık", "AMB-1");
        Ordered("İki Ürünlü Başlık", "AMB-2");
        var byTitle = Question("Kadın Bordo Düğmeli Kalın Esnek Anne Triko Yelek Standart");
        var byProductId = Question("Başka Bir Başlık", "752773818");
        var ambiguous = Question("İki Ürünlü Başlık");
        var unknown = Question("Hiç Satılmamış Ürün");
        await db.SaveChangesAsync();
        var service = new MarketplaceQuestionService(db, null!, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);

        var titled = (await service.GetAsync(tenant.Id, byTitle.Id, CancellationToken.None))!;
        Assert.Equal("https://cdn.example.com/yelek.jpg", titled.ProductImageUrl);
        // The ordered variant names the codes: model code and stock code, as an order line shows them.
        Assert.Equal("MZ0024YLK01", titled.ProductModelCode);
        Assert.Equal("MZ0024YLK01", titled.ProductSku);
        var linkedView = (await service.GetAsync(tenant.Id, byProductId.Id, CancellationToken.None))!;
        Assert.Equal("https://cdn.example.com/bluz.jpg", linkedView.ProductImageUrl);
        // A product link names no variant: the model code only.
        Assert.Equal("MZ025DY30", linkedView.ProductModelCode);
        Assert.Null(linkedView.ProductSku);
        Assert.Null((await service.GetAsync(tenant.Id, ambiguous.Id, CancellationToken.None))!.ProductImageUrl);
        Assert.Null((await service.GetAsync(tenant.Id, unknown.Id, CancellationToken.None))!.ProductImageUrl);
    }
}
