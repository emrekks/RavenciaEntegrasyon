using System.Net;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// O8/D5: the size chart pictures through the editor's own calls (no Claude call: the generated files are seeded).
[Collection("postgres-integration")]
public sealed class SizeChartEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private static string StoredUrl(Guid assetId) => $"/api/v1/files/product-media/{assetId:D}/content";

    [PostgreSqlFact]
    public async Task An_earlier_try_used_before_a_regenerate_saves_and_marks_and_a_deleted_chart_loses_its_mark()
    {
        var productId = Guid.CreateVersion7();
        var otherProductId = Guid.CreateVersion7();
        var used = Guid.CreateVersion7();
        var newest = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            foreach (var id in new[] { productId, otherProductId })
                db.Products.Add(new Product { Id = id, TenantId = fixture.TenantId, Title = $"Beden {id:N}", Description = "", VatRate = 10m, VatInclusion = "INCLUDED", DefaultListPrice = 0m, DefaultSalePrice = 0m, CreatedAt = now, UpdatedAt = now, Version = 1 });
            FileAsset Chart(Guid id, Guid forProduct) => new() { Id = id, TenantId = fixture.TenantId, Classification = "PRODUCT_MEDIA", RelativePath = $"{id:N}.jpg", OriginalNameSafe = $"beden-tablosu-{forProduct:N}.jpg", MimeType = "image/jpeg", SizeBytes = 10, Sha256 = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray()), Status = "ACTIVE", CreatedAt = now };
            db.FileAssets.AddRange(Chart(used, productId), Chart(newest, productId), Chart(foreign, otherProductId));
            // Kullan on the first try, then Yeniden Oluştur without using it: the chart row remembers only the newest.
            db.ProductSizeCharts.Add(new ProductSizeChart { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ProductId = productId, LastGeneratedAssetId = newest, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (admin)
        using (signIn)
        {
            HttpRequestMessage Reconcile(params Guid[] assets)
            {
                var request = ApiTestHost.Json(HttpMethod.Put, "/api/v1/files/product-media-reconcile", csrf, new { productId, variantId = (Guid?)null, items = assets.Select((asset, index) => new { url = StoredUrl(asset), sortOrder = index }), altText = "Beden" });
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                return request;
            }

            // Another card's generated chart is not this card's.
            using (var refused = await admin.SendAsync(Reconcile(foreign)))
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            using (var saved = await admin.SendAsync(Reconcile(used)))
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var mark = ApiTestHost.Json(HttpMethod.Put, $"/api/v1/products/{productId:D}/size-chart", csrf, new { url = StoredUrl(used) });
            mark.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using (var marked = await admin.SendAsync(mark))
                Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

            long version;
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(used, (await db.ProductSizeCharts.AsNoTracking().SingleAsync(x => x.ProductId == productId)).FileAssetId);
                version = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId)).Version;
            }

            // D5: the chart's picture deleted from the card takes the mark with it.
            var delete = ApiTestHost.Json(HttpMethod.Delete, "/api/v1/files/product-media-item", csrf, new { productId, url = StoredUrl(used) });
            delete.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            delete.Headers.TryAddWithoutValidation("If-Match", $"\"v{version}\"");
            using (var deleted = await admin.SendAsync(delete))
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Null((await db.ProductSizeCharts.AsNoTracking().SingleAsync(x => x.ProductId == productId)).FileAssetId);
            }
        }
    }
}
