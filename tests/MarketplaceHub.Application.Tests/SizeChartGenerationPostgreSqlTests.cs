using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.SizeCharts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// D4: a billed Claude call keeps its usage row and cost whatever fails after it, and a call the user abandons is logged as
// cancelled. A fake client stands in for Claude: nothing reaches the real API.
[Collection("postgres-integration")]
public sealed class SizeChartGenerationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string Key = "sk-ant-api03-generation-test-key-0000";

    // Claude as the skill would answer. An uploaded skill file (png): the PNG it drew (of the given size) and the
    // spec.json it drew from; the bundled skill: the JPEG and the chart.json echo.
    private sealed class FakeClaude(SizeChartTable table, Func<CancellationToken, Task>? beforeRun = null, int width = SizeChartService.Width, int height = SizeChartService.Height, bool png = true, bool svg = false) : IClaudeClient
    {
        public ClaudeSkillRequest? Request { get; private set; }
        public Task<ClaudeUsage> TestAsync(string apiKey, string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaudeSkillUpload> UploadSkillAsync(string apiKey, string displayTitle, IReadOnlyList<ClaudeSkillFile> files, string? existingSkillId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<ClaudeSkillResult> RunSkillAsync(ClaudeSkillRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            if (beforeRun is not null) await beforeRun(cancellationToken);
            return png
                ? new("end_turn", "OK", [new("file_img", svg ? "beden-tablosu.svg" : "beden-tablosu.png"), new("file_spec", "beden-tablosu-spec.json")], new ClaudeUsage(1500, 400, 0, 0), null)
                : new("end_turn", "OK", [new("file_img", "beden-tablosu.jpg"), new("file_spec", "beden-tablosu.json")], new ClaudeUsage(1500, 400, 0, 0), null);
        }

        public Task<byte[]> DownloadFileAsync(string apiKey, string fileId, long maxBytes, CancellationToken cancellationToken)
        {
            if (fileId != "file_spec" && svg) return Task.FromResult(System.Text.Encoding.UTF8.GetBytes($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}"><rect width="{width}" height="{height}" fill="#FBF7EE"/><text x="600" y="300" text-anchor="middle" font-family="Montserrat" font-weight="600" font-size="80">BEDEN TABLOSU</text></svg>"""));
            if (fileId != "file_spec") return Task.FromResult(png ? SizeChartRulesTests.Png(width, height) : SizeChartRulesTests.Jpeg(width, height));
            if (!png)
                return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new { unit = "cm", columns = table.Columns, rows = table.Rows.Select(row => new { size = row.Size, values = row.Values }) }));
            var spec = SizeChartSpec.Json(ProductTitle, SizeChartSpec.Map(table).Sizes!);
            spec["icon"] = new System.Text.Json.Nodes.JsonObject { ["sleeve"] = "short", ["neckline"] = "round", ["hem"] = "straight" };
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(spec.ToJsonString()));
        }
    }

    private const string ProductTitle = "Beden Bluz";

    private sealed class FailingStorage : IPrivateFileStorage
    {
        public Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken) => throw new IOException("disk dolu");
        public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private async Task<(Guid TenantId, Guid ProductId)> SeedAsync(AiSettingsService settings, bool skillFile = true)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"size-chart-gen-{Guid.NewGuid():N}", DisplayName = "size-chart-gen", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Beden Bluz", Description = "", VatRate = 10m, VatInclusion = "INCLUDED", DefaultListPrice = 0m, DefaultSalePrice = 0m, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Label = "Beden", NormalizedKey = "BEDEN", SortOrder = 0 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductOptions.Add(option);
            // The product picture Claude reads the icon from (a marketplace URL: nothing is read from the storage).
            var picture = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.invalid/bluz.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
            seed.FileAssets.Add(picture);
            seed.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = picture.Id, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" });
            seed.ProductOptionValues.AddRange(
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = option.Id, Label = "S", NormalizedKey = "S", SortOrder = 0 },
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = option.Id, Label = "M", NormalizedKey = "M", SortOrder = 1 });
            await seed.SaveChangesAsync();
        }
        await settings.SaveAsync(tenant.Id, Guid.Empty, "test", Key, "claude-sonnet-5-5", null, CancellationToken.None);
        var skill = AiSkillCatalog.Find(AiSkillCatalog.SizeChartKey)!;
        if (skillFile)
            await settings.RecordSkillAsync(tenant.Id, Guid.Empty, "test", skill.Key, new AiSkillBundle("beden-tablosu", skill.Files, "filehash0000"), AiUploadedSkillView.File, "ravencia-beden-tablosu.skill", new ClaudeSkillUpload("skill_test", "2"), CancellationToken.None);
        else
            await settings.RecordSkillAsync(tenant.Id, Guid.Empty, "test", skill, new ClaudeSkillUpload("skill_test", "1"), CancellationToken.None);
        return (tenant.Id, product.Id);
    }

    private static SizeChartInput Input() => new(["Boy", "En"], [new SizeChartRowInput("S", [64m, 50m]), new SizeChartRowInput("M", [66.5m, 53m])]);

    [PostgreSqlFact]
    public async Task A_storage_failure_after_the_billed_call_marks_its_usage_row_failed_and_keeps_the_cost()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings);
        var claude = new ClaudeService(settings, new FakeClaude(SizeChartRules.Validate(Input()).Table!), db, TimeProvider.System);
        var service = new SizeChartService(db, claude, new FailingStorage(), TimeProvider.System);

        await Assert.ThrowsAsync<IOException>(() => service.GenerateAsync(tenantId, Guid.Empty, productId, Input(), CancellationToken.None));

        await using var read = fixture.CreateContext();
        var row = await read.AiUsageLogs.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Feature == "SIZE_CHART");
        Assert.Equal("FAILED", row.Status);
        Assert.Contains("disk dolu", row.Error, StringComparison.Ordinal);
        Assert.Equal((1500L, 400L), (row.InputTokens, row.OutputTokens));
        Assert.NotNull(row.CostUsd);
    }

    [PostgreSqlFact]
    public async Task A_call_the_user_abandons_is_logged_as_cancelled_not_as_a_timeout()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings);
        using var abandon = new CancellationTokenSource();
        var claude = new ClaudeService(settings, new FakeClaude(SizeChartRules.Validate(Input()).Table!, async token =>
        {
            await abandon.CancelAsync();
            token.ThrowIfCancellationRequested();
        }), db, TimeProvider.System);
        var service = new SizeChartService(db, claude, new FailingStorage(), TimeProvider.System);

        var failure = await Assert.ThrowsAsync<SizeChartException>(() => service.GenerateAsync(tenantId, Guid.Empty, productId, Input(), abandon.Token));
        Assert.Equal(ClaudeClient.CancelledCode, failure.Code);

        await using var read = fixture.CreateContext();
        var row = await read.AiUsageLogs.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Feature == "SIZE_CHART");
        Assert.Equal(ClaudeService.CancelledStatus, row.Status);
    }

    private sealed class MemoryStorage : IPrivateFileStorage
    {
        public List<string> Saved { get; } = [];
        public Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken)
        {
            Saved.Add($"{relativeName} {mimeType}");
            return Task.FromResult(relativeName);
        }
        public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [PostgreSqlFact]
    public async Task A_1200x1800_png_is_stored_and_claude_got_the_product_picture_and_the_mapped_spec()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings);
        var fake = new FakeClaude(SizeChartRules.Validate(Input()).Table!);
        var storage = new MemoryStorage();
        var service = new SizeChartService(db, new ClaudeService(settings, fake, db, TimeProvider.System), storage, TimeProvider.System);

        var generated = await service.GenerateAsync(tenantId, Guid.Empty, productId, Input(), CancellationToken.None);

        Assert.Equal($"{generated.FileAssetId:N}.png image/png", Assert.Single(storage.Saved));
        Assert.Equal("https://cdn.example.invalid/bluz.jpg", Assert.Single(fake.Request!.Images!).Url);
        Assert.Equal(("skill_test", "2"), (fake.Request.SkillId, fake.Request.SkillVersion));
        Assert.Contains("\"product_name\": \"Beden Bluz\"", fake.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"gogus\": 53", fake.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"boy\": 66.5", fake.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("SVG", fake.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("/mnt/user-data/outputs/beden-tablosu.svg", fake.Request.System, StringComparison.Ordinal);
        Assert.Contains("no browser", fake.Request.System, StringComparison.Ordinal);
        Assert.Contains("1200x1800", fake.Request.System, StringComparison.Ordinal);
    }

    // 12 Nov 2026: the owner's skill draws an SVG for Chromium, which Claude's container lacks; the panel draws the PNG.
    [PostgreSqlFact]
    public async Task The_svg_the_skill_built_is_drawn_to_a_1200x1800_png_and_shown_as_the_newest_chart()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings);
        var fake = new FakeClaude(SizeChartRules.Validate(Input()).Table!, svg: true);
        var storage = new MemoryStorage();
        var service = new SizeChartService(db, new ClaudeService(settings, fake, db, TimeProvider.System), storage, TimeProvider.System);

        var generated = await service.GenerateAsync(tenantId, Guid.Empty, productId, Input(), CancellationToken.None);

        Assert.Equal($"{generated.FileAssetId:N}.png image/png", Assert.Single(storage.Saved));
        var view = await service.GetAsync(tenantId, productId, CancellationToken.None);
        Assert.Equal(generated.Url, view!.LastGeneratedUrl);
    }

    [PostgreSqlFact]
    public async Task Without_an_uploaded_file_the_bundled_skill_draws_its_1200x1800_jpeg()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings, skillFile: false);
        var fake = new FakeClaude(SizeChartRules.Validate(Input()).Table!, png: false);
        var storage = new MemoryStorage();
        var service = new SizeChartService(db, new ClaudeService(settings, fake, db, TimeProvider.System), storage, TimeProvider.System);

        var generated = await service.GenerateAsync(tenantId, Guid.Empty, productId, Input(), CancellationToken.None);

        Assert.Equal($"{generated.FileAssetId:N}.jpg image/jpeg", Assert.Single(storage.Saved));
        Assert.Null(fake.Request!.Images);
        Assert.Contains("chart.json", fake.Request.Prompt, StringComparison.Ordinal);
        Assert.Equal(("skill_test", "1"), (fake.Request.SkillId, fake.Request.SkillVersion));
    }

    [PostgreSqlFact]
    public async Task A_chart_of_another_size_is_refused_with_its_size_and_nothing_is_stored()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings);
        var storage = new MemoryStorage();
        var service = new SizeChartService(db, new ClaudeService(settings, new FakeClaude(SizeChartRules.Validate(Input()).Table!, width: 2400, height: 3600), db, TimeProvider.System), storage, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<SizeChartException>(() => service.GenerateAsync(tenantId, Guid.Empty, productId, Input(), CancellationToken.None));

        Assert.Equal("SIZE_CHART_WRONG_SIZE", failure.Code);
        Assert.Equal("Beden tablosu 1200×1800 olmalı; üretilen görsel 2400×3600 geldi, tekrar deneyin.", failure.Message);
        Assert.Empty(storage.Saved);
        await using var read = fixture.CreateContext();
        Assert.False(await read.FileAssets.AnyAsync(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA"));
        Assert.Null((await read.ProductSizeCharts.SingleAsync(x => x.TenantId == tenantId && x.ProductId == productId)).LastGeneratedAssetId);
        Assert.Equal("FAILED", (await read.AiUsageLogs.SingleAsync(x => x.TenantId == tenantId && x.Feature == "SIZE_CHART")).Status);
    }

    [PostgreSqlFact]
    public async Task A_column_the_uploaded_skill_cannot_show_is_refused_before_claude_is_called()
    {
        await using var db = fixture.CreateContext();
        var settings = new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var (tenantId, productId) = await SeedAsync(settings);
        SizeChartInput input = new(["Boy", "En", "Kol Boyu"], [new SizeChartRowInput("S", [64m, 50m, 20m]), new SizeChartRowInput("M", [66.5m, 53m, 21m])]);
        var fake = new FakeClaude(SizeChartRules.Validate(input).Table!);
        var service = new SizeChartService(db, new ClaudeService(settings, fake, db, TimeProvider.System), new MemoryStorage(), TimeProvider.System);

        Assert.True((await service.GetAsync(tenantId, productId, CancellationToken.None))!.AiReady);
        var failure = await Assert.ThrowsAsync<SizeChartException>(() => service.GenerateAsync(tenantId, Guid.Empty, productId, input, CancellationToken.None));
        Assert.Equal("SIZE_CHART_INVALID", failure.Code);
        Assert.Contains("'Kol Boyu'", failure.Message, StringComparison.Ordinal);
        Assert.Null(fake.Request);
    }
}
