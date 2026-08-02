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

    public int ExternalEffectCount { get; private set; }

    public Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Result(new ConnectionIdentity("FAKE", "TEST", "synthetic-store", "test-v1", "synthetic-scope"));

    public Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<CapabilityEvidence>>(
        [
            new("FAKE_READ", "UNKNOWN", "test-v1", "TEST", "synthetic-store", "test://deterministic-fake", "test-v1", null, null,
                "Test-only deterministic evidence; never promotes a real platform capability.", null, timeProvider.GetUtcNow())
        ]);

    public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Result(Page(new RemoteReferenceItem(resource.ResourceType, "synthetic-reference", resource.ParentExternalId, "Synthetic Reference", "Synthetic Reference", 0, true, true, "{}")));

    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
        Result(Page(new RemoteProduct("synthetic-product", "synthetic-variant", "0000000000000", "SYNTHETIC-SKU", "{}")));

    public Task<AdapterResult<RemoteOperationRef>> UpsertAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) =>
        Write(context, () => new RemoteOperationRef($"fake-operation-{context.IdempotencyKey}", "PRODUCT", timeProvider.GetUtcNow()));

    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
        Result(new RemoteOperationStatus(externalOperationId, "SYNTHETIC", []));

    public Task<AdapterResult<bool>> ArchiveAsync(AdapterContext context, ExternalProductIdentity identity, CancellationToken cancellationToken) =>
        Write(context, () => true);

    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushStockAsync(AdapterContext context, IReadOnlyList<StockPushLine> lines, CancellationToken cancellationToken) =>
        Write(context, () => Batch(lines.Select(x => x.VariantId).ToArray()));

    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushPricesAsync(AdapterContext context, IReadOnlyList<PricePushLine> lines, CancellationToken cancellationToken) =>
        Write(context, () => Batch(lines.Select(x => x.VariantId).ToArray()));

    public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Result(Page(Order()));

    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
        Result(Order() with { ExternalOrderId = externalOrderId });

    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
        Write(context, () => new PackageActionResult(command.ExternalPackageId, "SYNTHETIC", $"fake-operation-{context.IdempotencyKey}"));

    Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> IReturnPort.PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Result(Page(ReturnClaim()));

    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
        Result(ReturnClaim() with { ExternalClaimId = externalReturnId });

    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) =>
        Write(context, () => new ReturnActionResult(command.ExternalClaimId, "SYNTHETIC", $"fake-operation-{context.IdempotencyKey}"));

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
