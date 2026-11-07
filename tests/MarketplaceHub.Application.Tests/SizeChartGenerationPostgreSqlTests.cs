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

    private sealed class FakeClaude(SizeChartTable table, Func<CancellationToken, Task>? beforeRun = null) : IClaudeClient
    {
        public Task<ClaudeUsage> TestAsync(string apiKey, string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaudeSkillUpload> UploadSkillAsync(string apiKey, string displayTitle, IReadOnlyList<ClaudeSkillFile> files, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<ClaudeSkillResult> RunSkillAsync(ClaudeSkillRequest request, CancellationToken cancellationToken)
        {
            if (beforeRun is not null) await beforeRun(cancellationToken);
            return new("end_turn", "OK", [new("file_img", "beden-tablosu.jpg"), new("file_echo", "beden-tablosu.json")], new ClaudeUsage(1500, 400, 0, 0), null);
        }

        public Task<byte[]> DownloadFileAsync(string apiKey, string fileId, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(fileId == "file_echo"
                ? JsonSerializer.SerializeToUtf8Bytes(new { unit = "cm", columns = table.Columns, rows = table.Rows.Select(row => new { size = row.Size, values = row.Values }) })
                : Jpeg(SizeChartService.Width, SizeChartService.Height));
    }

    private sealed class FailingStorage : IPrivateFileStorage
    {
        public Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken) => throw new IOException("disk dolu");
        public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // A complete baseline JPEG header with the given size (SOI, SOF0, EOI): enough for SizeChartService.JpegSize.
    private static byte[] Jpeg(int width, int height) =>
        [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9];

    private async Task<(Guid TenantId, Guid ProductId)> SeedAsync(AiSettingsService settings)
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
            seed.ProductOptionValues.AddRange(
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = option.Id, Label = "S", NormalizedKey = "S", SortOrder = 0 },
                new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = option.Id, Label = "M", NormalizedKey = "M", SortOrder = 1 });
            await seed.SaveChangesAsync();
        }
        await settings.SaveAsync(tenant.Id, Guid.Empty, "test", Key, "claude-sonnet-5-5", null, CancellationToken.None);
        await settings.RecordSkillAsync(tenant.Id, Guid.Empty, "test", AiSkillCatalog.Find(AiSkillCatalog.SizeChartKey)!, new ClaudeSkillUpload("skill_test", "1"), CancellationToken.None);
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
}
