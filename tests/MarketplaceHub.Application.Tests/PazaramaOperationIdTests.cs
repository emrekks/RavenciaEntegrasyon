using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A Pazarama product write is polled by whichever process (API or worker) picks the job up next, so its
// result travels in the operation id instead of one process's memory.
public sealed class PazaramaOperationIdTests
{
    [Fact]
    public async Task AnInlineResultIsReadBackByAnotherClientInstanceWithoutARequest()
    {
        IReadOnlyList<RemoteOperationLine> lines =
        [
            new("8690000000001", true, null, null, false, "Ürün ekleme isteği iletildi."),
            new("8690000000002", false, null, "PAZARAMA_SALE_STATUS_REJECTED", false, "Barkod bulunamadı")
        ];
        var operationId = PazaramaOperationId.Completed(lines);

        var status = await Client().GetOperationAsync(Context(), operationId, CancellationToken.None);

        Assert.True(status.IsSuccess, status.Error?.Code);
        Assert.Equal("COMPLETED", status.Value!.Status);
        Assert.Equal(lines, status.Value.Lines);
        Assert.StartsWith("c:", operationId, StringComparison.Ordinal);
        Assert.DoesNotContain('|', operationId);
    }

    [Fact]
    public void ABatchIdCarriesItsBarcodes()
    {
        var batchId = Guid.NewGuid().ToString();
        var operationId = PazaramaOperationId.Batch(batchId, ["B1", "B2"]);

        Assert.True(PazaramaOperationId.TryBatch(operationId, out var parsed, out var codes));
        Assert.Equal(Guid.Parse(batchId), parsed);
        Assert.Equal(["B1", "B2"], codes);
        Assert.StartsWith($"b:{batchId}:2:", operationId, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchFinishedWithOnlyFailuresListedReportsTheOtherBarcodesFromTheId()
    {
        var operationId = PazaramaOperationId.Batch(Guid.NewGuid().ToString(), ["B1", "B2"]);
        PazaramaOperationId.TryBatch(operationId, out _, out var codes);
        using var json = JsonDocument.Parse("""{"data":{"status":2,"failedProducts":[{"productCode":"B2","errorReason":"Özellik eksik"}]}}""");

        var status = PazaramaProductWriteMapper.BatchStatus(json.RootElement, operationId, codes);

        Assert.True(status.Lines.Single(line => line.ExternalKey == "B1").Succeeded);
        Assert.False(status.Lines.Single(line => line.ExternalKey == "B2").Succeeded);
    }

    [Fact]
    public void ABatchIdFromBeforeTheEncodingIsStillRead()
    {
        var batchId = Guid.NewGuid();

        Assert.True(PazaramaOperationId.TryBatch($"b:{batchId}:3", out var parsed, out var codes));
        Assert.Equal(batchId, parsed);
        Assert.Null(codes);
    }

    [Theory]
    [InlineData("b:not-a-guid:2")]
    [InlineData("b:3f2504e0-4f89-11d3-9a0c-0305e82c3301:2:%%%")]
    [InlineData("x:3f2504e0-4f89-11d3-9a0c-0305e82c3301:2")]
    public void AMalformedBatchIdIsRefused(string operationId) =>
        Assert.False(PazaramaOperationId.TryBatch(operationId, out _, out _));

    [Fact]
    public async Task AnOldInMemoryResultIdIsReportedAsForgotten()
    {
        var status = await Client().GetOperationAsync(Context(), $"i:{Guid.NewGuid():N}:1", CancellationToken.None);

        Assert.Equal("PAZARAMA_OPERATION_FORGOTTEN", status.Error!.Code);
    }

    [Fact]
    public async Task AGarbledInlineResultIsRefused()
    {
        var status = await Client().GetOperationAsync(Context(), "c:%%%", CancellationToken.None);

        Assert.Equal("PAZARAMA_OPERATION_ID_INVALID", status.Error!.Code);
    }

    private static AdapterContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), "correlation", "effect", DateTimeOffset.UtcNow.AddMinutes(1));

    private static PazaramaHttpClient Client() => new(
        new NoHttpFactory(), null!, new PazaramaTokenCache(TimeProvider.System), Options.Create(new PazaramaOptions()), TimeProvider.System, NullLogger<PazaramaHttpClient>.Instance, new ConfigurationBuilder().Build());

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No request is expected.");
    }
}
