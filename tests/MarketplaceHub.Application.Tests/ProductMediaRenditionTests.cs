using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductMediaRenditionTests
{
    // A random colour, so every seeded picture has its own hash (one active upload per hash and tenant).
    internal static byte[] Png(int width, int height)
    {
        using var image = (Image.Black(width, height, bands: 3) + new double[] { Random.Shared.Next(256), Random.Shared.Next(256), Random.Shared.Next(256) }).Cast(Enums.BandFormat.Uchar);
        return image.PngsaveBuffer();
    }

    // The directives, sorted: the client reorders the header when it parses it.
    internal static string[] CacheControl(HttpResponseMessage response) =>
        [.. Header(response, "Cache-Control").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    internal static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : "";

    internal static (int Width, int Height, string Loader) Measure(byte[] bytes)
    {
        using var image = Image.NewFromBuffer(bytes);
        return (image.Width, image.Height, (string)image.Get("vips-loader"));
    }

    internal static (int Width, int Height) Size(byte[] bytes) { var measured = Measure(bytes); return (measured.Width, measured.Height); }

    [Theory]
    [InlineData("168", 168)]
    [InlineData("480", 480)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("200", null)]
    [InlineData("-168", null)]
    [InlineData("168abc", null)]
    public void Only_the_two_thumbnail_widths_are_served_anything_else_is_the_original(string? query, int? expected) =>
        Assert.Equal(expected, ProductMediaRenditions.RequestedWidth(query));

    [Fact]
    public void A_copy_fits_its_2_by_3_box_keeps_the_aspect_and_never_grows()
    {
        Assert.Equal((168, 252, "webpload_buffer"), Measure(ProductMediaRenditions.Render(Png(1024, 1536), 168)));
        Assert.Equal((480, 720, "webpload_buffer"), Measure(ProductMediaRenditions.Render(Png(1024, 1536), 480)));
        // A wide picture is bounded by its width, a small one keeps its size.
        Assert.Equal((168, 84), Size(ProductMediaRenditions.Render(Png(400, 200), 168)));
        Assert.Equal((100, 150), Size(ProductMediaRenditions.Render(Png(100, 150), 480)));
    }

    [Fact]
    public async Task Ensure_writes_both_copies_once_and_a_broken_picture_is_reported_not_thrown()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-renditions", Guid.NewGuid().ToString("N"));
        try
        {
            var renditions = new ProductMediaRenditions(root);
            var tenant = Guid.NewGuid();
            var asset = Guid.NewGuid();
            var reads = 0;
            Task<byte[]> Read(CancellationToken _) { reads++; return Task.FromResult(Png(600, 900)); }
            Assert.True(await renditions.TryEnsureAsync(tenant, asset, Read, CancellationToken.None));
            Assert.True(await renditions.TryEnsureAsync(tenant, asset, Read, CancellationToken.None));
            Assert.Equal(1, reads);
            Assert.True(renditions.HasAll(tenant, asset));

            var broken = Guid.NewGuid();
            Assert.False(await renditions.TryEnsureAsync(tenant, broken, _ => Task.FromResult(new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 }), CancellationToken.None));
            Assert.Null(renditions.TryOpen(tenant, broken, 168));

            renditions.Delete(tenant, asset);
            Assert.False(renditions.Exists(tenant, asset, 168));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

[Collection("postgres-integration")]
public sealed class ProductMediaContentEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private async Task<(Guid AssetId, string Sha256)> SeedAsync(bool live)
    {
        var bytes = ProductMediaRenditionTests.Png(1024, 1536);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IPrivateFileStorage>();
        var assetId = Guid.CreateVersion7();
        using var content = new MemoryStream(bytes);
        var stored = await storage.SaveAsync(fixture.TenantId, $"{assetId:N}.png", "image/png", content, bytes.Length, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, Title = $"Görsel {assetId:N}", Description = "", VatRate = 10m, VatInclusion = "INCLUDED", DefaultListPrice = 0m, DefaultSalePrice = 0m, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Products.Add(product);
        db.FileAssets.Add(new FileAsset { Id = assetId, TenantId = fixture.TenantId, Classification = "PRODUCT_MEDIA", RelativePath = stored, OriginalNameSafe = "kapak.png", MimeType = "image/png", SizeBytes = bytes.Length, Sha256 = sha, Status = "ACTIVE", CreatedAt = now });
        db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ProductId = product.Id, FileAssetId = assetId, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" });
        if (live)
        {
            var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Görsel", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
            db.PlatformConnections.Add(connection);
            db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE" });
        }
        await db.SaveChangesAsync();
        return (assetId, sha);
    }

    private static HttpRequestMessage Get(string path, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifNoneMatch is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(ifNoneMatch));
        return request;
    }

    [PostgreSqlFact]
    public async Task The_panel_gets_an_immutable_tagged_original_a_304_on_revalidation_and_webp_thumbnails_on_request()
    {
        var (assetId, sha) = await SeedAsync(live: false);
        var path = $"/api/v1/files/product-media/{assetId:D}/content";
        var (client, _, signIn) = await fixture.SignInAsync(MembershipRole.ReadOnly);
        using (client)
        using (signIn)
        {
            using (var original = await client.SendAsync(Get(path)))
            {
                Assert.Equal(HttpStatusCode.OK, original.StatusCode);
                Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);
                Assert.Equal($"\"{sha}\"", original.Headers.ETag?.Tag);
                Assert.Equal(["immutable", "max-age=31536000", "private"], ProductMediaRenditionTests.CacheControl(original));
                Assert.Contains("sandbox", string.Join(";", original.Headers.GetValues("Content-Security-Policy")));
                Assert.Equal((1024, 1536), ProductMediaRenditionTests.Size(await original.Content.ReadAsByteArrayAsync()));
            }
            using (var revalidated = await client.SendAsync(Get(path, $"\"{sha}\"")))
            {
                Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
                Assert.Equal($"\"{sha}\"", revalidated.Headers.ETag?.Tag);
                Assert.Empty(await revalidated.Content.ReadAsByteArrayAsync());
            }
            // Another picture's tag is not this one's.
            using (var stale = await client.SendAsync(Get(path, "\"0000\"")))
                Assert.Equal(HttpStatusCode.OK, stale.StatusCode);

            foreach (var (width, height) in new[] { (168, 252), (480, 720) })
            {
                using var thumbnail = await client.SendAsync(Get($"{path}?w={width}"));
                Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
                Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType?.MediaType);
                Assert.Equal($"\"{sha}-w{width}\"", thumbnail.Headers.ETag?.Tag);
                Assert.Equal(["immutable", "max-age=31536000", "private"], ProductMediaRenditionTests.CacheControl(thumbnail));
                Assert.Equal((width, height, "webpload_buffer"), ProductMediaRenditionTests.Measure(await thumbnail.Content.ReadAsByteArrayAsync()));
                using var again = await client.SendAsync(Get($"{path}?w={width}", $"\"{sha}-w{width}\""));
                Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
            }
            // An unknown width is the original.
            using (var other = await client.SendAsync(Get($"{path}?w=999")))
            {
                Assert.Equal("image/png", other.Content.Headers.ContentType?.MediaType);
                Assert.Equal($"\"{sha}\"", other.Headers.ETag?.Tag);
            }
        }

        // Signed out, neither the picture nor a revalidation answers.
        using var anonymous = fixture.Host!.CreateClient();
        using (var refused = await anonymous.SendAsync(Get($"{path}?w=168", $"\"{sha}-w168\"")))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [PostgreSqlFact]
    public async Task Marketplaces_keep_getting_the_public_original_with_a_public_immutable_tag()
    {
        var (assetId, sha) = await SeedAsync(live: true);
        using var anonymous = fixture.Host!.CreateClient();
        var path = $"/api/v1/public/product-media/{assetId:D}/content";
        using (var original = await anonymous.SendAsync(Get($"{path}?w=168")))
        {
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);
            Assert.Equal(["immutable", "max-age=31536000", "public"], ProductMediaRenditionTests.CacheControl(original));
            Assert.Equal($"\"{sha}\"", original.Headers.ETag?.Tag);
            Assert.Equal((1024, 1536), ProductMediaRenditionTests.Size(await original.Content.ReadAsByteArrayAsync()));
        }
        using (var revalidated = await anonymous.SendAsync(Get(path, $"\"{sha}\"")))
            Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);

        // A draft never sent is still not served, tag or not.
        var (draftId, draftSha) = await SeedAsync(live: false);
        using var hidden = await anonymous.SendAsync(Get($"/api/v1/public/product-media/{draftId:D}/content", $"\"{draftSha}\""));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [PostgreSqlFact]
    public async Task An_upload_stores_its_thumbnails_with_the_original()
    {
        var productId = Guid.CreateVersion7();
        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Products.Add(new Product { Id = productId, TenantId = fixture.TenantId, Title = $"Yükleme {productId:N}", Description = "", VatRate = 10m, VatInclusion = "INCLUDED", DefaultListPrice = 0m, DefaultSalePrice = 0m, CreatedAt = now, UpdatedAt = now, Version = 1 });
            await db.SaveChangesAsync();
        }
        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (admin)
        using (signIn)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(ProductMediaRenditionTests.Png(1200, 1800));
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "kapak.png");
            form.Add(new StringContent(productId.ToString("D")), "productId");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/files/product-media") { Content = form };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var uploaded = await admin.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var assetId = (await uploaded.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("fileAssetId").GetGuid();
            Assert.True(fixture.Host.Services.GetRequiredService<ProductMediaRenditions>().HasAll(fixture.TenantId, assetId));
        }
    }

    [PostgreSqlFact]
    public async Task The_backfill_gives_stored_photos_their_thumbnails_once()
    {
        var (assetId, _) = await SeedAsync(live: false);
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IPrivateFileStorage>();
        var renditions = scope.ServiceProvider.GetRequiredService<ProductMediaRenditions>();
        renditions.Delete(fixture.TenantId, assetId);

        var first = await ProductMediaRenditionBackfill.RunAsync(db, storage, renditions, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        Assert.True(first.Generated >= 1);
        Assert.True(renditions.HasAll(fixture.TenantId, assetId));
        await using var copy = renditions.TryOpen(fixture.TenantId, assetId, 168)!;
        var written = File.GetLastWriteTimeUtc(((FileStream)copy).Name);

        var second = await ProductMediaRenditionBackfill.RunAsync(db, storage, renditions, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        Assert.Equal(0, second.Generated);
        Assert.Equal(first.Checked, second.Checked);
        Assert.Equal(written, File.GetLastWriteTimeUtc(((FileStream)copy).Name));
    }
}
