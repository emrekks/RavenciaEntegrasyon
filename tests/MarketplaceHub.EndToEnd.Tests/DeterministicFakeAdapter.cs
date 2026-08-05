using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.EndToEnd.Tests;

public enum FakeScenario
{
    Success,
    Empty,
    Partial,
    Authentication,
    RateLimit,
    RemoteError,
    Timeout,
    Validation,
    ContractViolation
}

internal sealed class DeterministicFakeAdapter(FakeScenario scenario, TimeProvider timeProvider, bool writesEnabled = false)
    : IConnectionPort, IReferenceDataPort, IProductPort, IInventoryPricePort, IOrderPort, IReturnPort
{
    private readonly Dictionary<string, object> effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> productBatchBarcodes = new(StringComparer.Ordinal);
    private int publicationStatusReadCount;

    public int ExternalEffectCount { get; private set; }
    public int PublicationStatusReadCount => Volatile.Read(ref publicationStatusReadCount);

    public Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Result(new ConnectionIdentity("FAKE", "TEST", "synthetic-store", "test-v1", "synthetic-scope"));

    public Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<CapabilityEvidence>>(
        [
            new("FAKE_READ", "UNKNOWN", "test-v1", "TEST", "synthetic-store", "test://deterministic-fake", "test-v1", null, null,
                "Test-only deterministic evidence; never promotes a real platform capability.", null, timeProvider.GetUtcNow())
        ]);

    public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Result(Page(resource.ResourceType == "CATEGORY_ATTRIBUTES"
            ? new RemoteReferenceItem(resource.ResourceType, "synthetic-reference", resource.ParentExternalId, "Synthetic Reference", "Synthetic Reference", 0, true, true, "{}", true, true, false)
            : new RemoteReferenceItem(resource.ResourceType, "synthetic-reference", resource.ParentExternalId, "Synthetic Reference", "Synthetic Reference", 0, true, true, "{}")));

    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
        Result(Page(new RemoteProduct("synthetic-product", "synthetic-variant", "0000000000000", "SYNTHETIC-SKU", "{}")));

    public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) =>
        BatchWrite(context, publication.PayloadJson, "PRODUCT_CREATE");

    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        BatchWrite(context, publication.UnapprovedPayloadJson, "PRODUCT_UPDATE_UNAPPROVED");

    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        BatchWrite(context, publication.ApprovedContentPayloadJson, "PRODUCT_UPDATE_CONTENT");

    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        BatchWrite(context, publication.ApprovedVariantPayloadJson, "PRODUCT_UPDATE_VARIANTS");

    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        BatchWrite(context, publication.ApprovedDeliveryPayloadJson, "PRODUCT_UPDATE_DELIVERY");

    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (!productBatchBarcodes.TryGetValue(externalOperationId, out var barcodes)) return Result(new RemoteOperationStatus(externalOperationId, "COMPLETED", []));
        var lines = barcodes.Select((barcode, index) => scenario == FakeScenario.Partial && index > 0
            ? new RemoteOperationLine(barcode, false, null, "FAKE_PARTIAL_REJECTION", false)
            : new RemoteOperationLine(barcode, true, $"fake-content-{index + 1}", null, false)).ToList();
        return Result(new RemoteOperationStatus(externalOperationId, "COMPLETED", lines));
    }

    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref publicationStatusReadCount);
        if (scenario == FakeScenario.Empty) return Result(new RemotePublicationStatus(barcode, "NOT_FOUND", null, null, null, "{}"));
        var rejected = scenario == FakeScenario.Partial && barcode.EndsWith('2');
        return Result(rejected
            ? new RemotePublicationStatus(barcode, "REJECTED", null, null, "FAKE_APPROVAL_REJECTION", "{}")
            : new RemotePublicationStatus(barcode, "APPROVED", $"fake-content-{barcode}", $"fake-variant-{barcode}", null, "{}"));
    }

    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        BatchWrite(context, payloadJson, "PRODUCT_ARCHIVE");

    public Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        BatchWrite(context, payloadJson, "PRICE_AND_INVENTORY");

    public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Result(Page(Order()));

    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
        Result(Order() with { ExternalOrderId = externalOrderId });

    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
        Write(context, () => new PackageActionResult(command.ExternalPackageId, "SYNTHETIC", $"fake-operation-{context.IdempotencyKey}"));

    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
        Write(context, () => true);

    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken) =>
        Result(new CommonLabelDocument(cargoTrackingNumber, "ZPL", System.Text.Encoding.UTF8.GetBytes("^XA^FO20,20^FDSYNTHETIC^FS^XZ")));

    Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> IReturnPort.PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Result(Page(ReturnClaim()));

    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
        Result(ReturnClaim() with { ExternalClaimId = externalReturnId });

    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) =>
        Write(context, () => new ReturnActionResult(command.ExternalClaimId, "SYNTHETIC", $"fake-operation-{context.IdempotencyKey}"));

    private async Task<AdapterResult<RemoteOperationRef>> BatchWrite(AdapterContext context, string payloadJson, string kind)
    {
        var result = await Write(context, () => new RemoteOperationRef($"fake-operation-{context.IdempotencyKey}", kind, timeProvider.GetUtcNow()));
        if (!result.IsSuccess) return result;
        using var document = JsonDocument.Parse(payloadJson);
        var keys = document.RootElement.GetProperty("items").EnumerateArray().Select(item =>
            item.TryGetProperty("barcode", out var barcode) ? barcode.ToString() :
            item.TryGetProperty("contentId", out var contentId) ? contentId.ToString() : "synthetic-item").ToList();
        productBatchBarcodes[result.Value!.ExternalOperationId] = keys;
        return result;
    }

    private Task<AdapterResult<T>> Result<T>(T value)
    {
        var error = Error();
        return Task.FromResult(error is null ? AdapterResult<T>.Success(value) : AdapterResult<T>.Failure(error));
    }

    private Task<AdapterResult<T>> Write<T>(AdapterContext context, Func<T> create)
    {
        if (!writesEnabled)
            return Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "FAKE_WRITE_DISABLED", "Fake external writes are disabled.", null, null, null)));
        var error = Error();
        if (error is not null) return Task.FromResult(AdapterResult<T>.Failure(error));
        if (effects.TryGetValue(context.IdempotencyKey, out var existing)) return Task.FromResult(AdapterResult<T>.Success((T)existing));
        var value = create();
        effects.Add(context.IdempotencyKey, value!);
        ExternalEffectCount++;
        return Task.FromResult(AdapterResult<T>.Success(value));
    }

    private AdapterError? Error() => scenario switch
    {
        FakeScenario.Authentication => new(AdapterErrorClass.Authentication, "FAKE_AUTHENTICATION", "Synthetic authentication failure.", 401, null, "fake-request"),
        FakeScenario.RateLimit => new(AdapterErrorClass.RateLimit, "FAKE_RATE_LIMIT", "Synthetic rate limit.", 429, TimeSpan.FromSeconds(1), "fake-request"),
        FakeScenario.RemoteError => new(AdapterErrorClass.Remote5xx, "FAKE_REMOTE_ERROR", "Synthetic remote failure.", 503, TimeSpan.FromSeconds(1), "fake-request"),
        FakeScenario.Timeout => new(AdapterErrorClass.TransientNetwork, "FAKE_TIMEOUT", "Synthetic timeout.", null, TimeSpan.FromSeconds(1), null),
        FakeScenario.Validation => new(AdapterErrorClass.Validation, "FAKE_VALIDATION", "Synthetic validation failure.", 422, null, "fake-request"),
        FakeScenario.ContractViolation => new(AdapterErrorClass.ContractViolation, "FAKE_CONTRACT", "Synthetic contract violation.", null, null, "fake-request"),
        _ => null
    };

    private AdapterPageResult<T> Page<T>(T value) => scenario == FakeScenario.Empty ? new([], null, false) : new([value], null, false);

    private BatchResult<BatchLineResult> Batch(IReadOnlyList<Guid> ids)
    {
        var lines = ids.Select((id, index) => scenario != FakeScenario.Partial || index == 0
            ? new BatchLineResult(id, true, null, false)
            : new BatchLineResult(id, false, "FAKE_VALIDATION", false)).ToArray();
        return new(lines, "fake-batch", lines.Any(x => !x.Succeeded) && lines.Any(x => x.Succeeded));
    }

    private RemoteOrder Order()
    {
        var now = timeProvider.GetUtcNow();
        return new("synthetic-order", "SYNTHETIC-ORDER", now, now, "TRY", 10, 0, 10, "{}", "{}", "{}",
            [new("synthetic-line", "SYNTHETIC-SKU", "0000000000000", "Synthetic Product", 1, 10, 0, "SYNTHETIC")], [], "{}");
    }

    private RemoteReturnClaim ReturnClaim() => new("synthetic-return", "synthetic-order", "SYNTHETIC", null, null, null,
        timeProvider.GetUtcNow(), [new("synthetic-return-line", "synthetic-line", 1)], "{}");
}
