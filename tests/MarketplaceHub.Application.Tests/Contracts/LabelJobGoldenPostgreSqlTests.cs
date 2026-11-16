using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 3b (16 Nov 2026): the common label (TRENDYOL_COMMON_LABEL), the stage capability probe and the
// stage test order, pinned before they move out of the processor: the exact label request and reads, the stored file,
// document and attempt, the effect record, the capabilities and audit rows, and the job's next payload, for a send that
// is accepted, refused for good, refused for quota or left unclear, the same job run again, the read that is not ready,
// a document of the wrong format, an expired window, a carrier without the label and Hepsiburada's read-only label.
[Collection("postgres-integration")]
public sealed class LabelJobGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;

    private static RemoteOrder Order(string externalId, string cargoProvider, string tracking) =>
        new(externalId, externalId, Start.AddDays(-1), Start.AddHours(-3), "TRY", 100m, 0m, 100m,
            "{\"customerFirstName\":\"Golden\"}", "{\"city\":\"İstanbul\"}", "{\"city\":\"İstanbul\"}",
            [new RemoteOrderLine("31000001", "SKU-1", "8690000000001", "Ürün", 1m, 100m, 20m, "Created")],
            [new RemotePackage($"P{externalId}", null, "Created", Start.AddHours(-3), cargoProvider, tracking, [new RemotePackageAllocation("31000001", 1m, 0, 0, 0, 0)], 100m, 0m, 100m)], "{}");

    private async Task<(GoldenRun Run, Guid PackageId)> SeedAsync(string platform, string cargoProvider, string environment = "PRODUCTION", string store = "store-golden")
    {
        var run = new GoldenRun(fixture, platform) { Environment = environment, ExternalStoreId = store, ConnectionSettingsJson = "{\"ExternalWritesEnabled\":true}" };
        run.Settings["FeatureFlags:ExternalWrites"] = "true";
        await run.SeedAsync();
        await using (var db = fixture.CreateContext(new FrozenClock(Start.AddMinutes(-30))))
            await new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), new FrozenClock(Start.AddMinutes(-30)))
                .UpsertOrders(run.Tenant.Id, run.Connection.Id, [Order("7101", cargoProvider, "7330000000001")], CancellationToken.None);
        await using var check = fixture.CreateContext();
        foreach (var order in await check.Orders.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[order.Id] = $"order-{order.ExternalOrderId}";
        var package = await check.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).SingleAsync();
        run.Known[package.Id] = "package";
        return (run, package.Id);
    }

    // ---- TRENDYOL_COMMON_LABEL -----------------------------------------------------------------------------------------

    public static TheoryData<string, string> CommonLabelScenarios => new()
    {
        { "TRENDYOL", "accepted" }, { "TRENDYOL", "refused" }, { "TRENDYOL", "quota" }, { "TRENDYOL", "uncertain" },
        { "TRENDYOL", "not-ready" }, { "TRENDYOL", "wrong-format" }, { "TRENDYOL", "expired" }, { "TRENDYOL", "carrier-unsupported" },
        { "TRENDYOL", "payload-invalid" }, { "HEPSIBURADA", "accepted" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(CommonLabelScenarios))]
    public async Task ACommonLabelSendsAndStoresTheSameThings(string platform, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var (run, packageId) = await SeedAsync(platform, scenario == "carrier-unsupported" ? "YURTICI" : platform == "HEPSIBURADA" ? "HEPSIJET" : "ARAS Kargo");
        var jobId = run.NewId();
        run.Known[jobId] = "job";
        var format = platform == "HEPSIBURADA" ? "PDF" : "ZPL";
        var payload = new CommonLabelJobPayload(jobId, packageId, "SUBMIT", 1, 10m, Start.AddMinutes(-1), scenario == "expired" ? Start.AddSeconds(-30) : Start.AddMinutes(30), format);
        var payloadJson = scenario == "payload-invalid" ? "{\"jobId\":\"x\"}" : JsonSerializer.Serialize(payload);
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, JobType = MarketplaceJobTypes.CommonLabel, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"golden-label:{jobId:N}", EffectIdempotencyKey = $"common-label:{jobId:N}", Status = JobStatus.Leased, AvailableAt = Start, CorrelationId = "golden", CreatedAt = Start, Version = 1 });
            var attempt = new ShipmentDocumentAttempt { Id = run.NewId(), TenantId = run.Tenant.Id, PackageId = packageId, IdempotencyKey = $"common-label:{jobId:N}", Status = "PENDING", CreatedAt = Start };
            run.Known[attempt.Id] = "attempt";
            db.ShipmentDocumentAttempts.Add(attempt);
            await db.SaveChangesAsync();
        }

        var port = new GoldenMarketplace();
        var sends = 0;
        port.Answers["orders.CreateCommonLabel"] = _ => scenario is "refused" or "quota" or "uncertain" && sends++ == 0
            ? AdapterResult<bool>.Failure(scenario switch
            {
                "uncertain" => GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503),
                "quota" => GoldenMarketplace.Error(AdapterErrorClass.RateLimit, "RATE_LIMITED", 429),
                _ => GoldenMarketplace.Error(AdapterErrorClass.Validation, "REMOTE_VALIDATION", 400)
            })
            : AdapterResult<bool>.Success(true);
        var reads = 0;
        port.Answers["orders.GetCommonLabel"] = args => scenario == "not-ready" && reads++ == 0
            ? AdapterResult<CommonLabelDocument>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "LABEL_NOT_READY", 404))
            : AdapterResult<CommonLabelDocument>.Success(new((string)args[0]!, scenario == "wrong-format" ? "PDF" : (string)args[1]!, Encoding.UTF8.GetBytes("^XA^FDgolden label^FS^XZ")));

        // The queue runs the same job again with the payload the last run left on it, up to the label or a stop.
        for (var attemptRun = 0; attemptRun < 4; attemptRun++)
        {
            await using (var db = fixture.CreateContext())
                payloadJson = await db.IntegrationJobs.Where(x => x.Id == jobId).Select(x => x.PayloadJson).SingleAsync();
            var result = await run.RunAsync($"run-{attemptRun}", MarketplaceJobTypes.CommonLabel, port, payloadJson, jobId);
            if (result.Kind != JobCompletionKind.Retry && result.Kind != JobCompletionKind.ManualReview || attemptRun > 0 && result.Kind == JobCompletionKind.ManualReview) break;
            run.Clock.Advance(TimeSpan.FromSeconds(30));
        }
        await run.MatchesAsync($"LabelJobs/{platform.ToLowerInvariant()}-common_label-{scenario}.golden.json");
    }

    // ---- Stage capability probe and stage test order ----------------------------------------------------------------------

    public static TheoryData<string> ProbeScenarios => new() { "label-read", "label-write", "label-write-stale-fixture", "read-refused", "not-stage" };

    [PostgreSqlTheory]
    [MemberData(nameof(ProbeScenarios))]
    public async Task AStageCapabilityProbeAsksAndStoresTheSameThings(string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var (run, packageId) = await SeedAsync("TRENDYOL", "ARAS Kargo", environment: scenario == "not-stage" ? "PRODUCTION" : "STAGE", store: "2738");
        var actor = run.NewId();
        run.Known[actor] = "actor";
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            foreach (var capabilityCode in new[] { MarketplaceCapabilities.LabelRead, MarketplaceCapabilities.LabelWrite, MarketplaceCapabilities.ShipmentWrite })
            {
                var capability = new PlatformCapability { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, Code = capabilityCode, SupportLevel = CapabilitySupportLevel.Unknown, ApiVersion = "V2", Environment = "STAGE", StoreScope = "2738", Version = 0 };
                run.Known[capability.Id] = $"capability-{capabilityCode}";
                db.PlatformCapabilities.Add(capability);
            }
            db.AuditLogs.Add(new AuditLog { TenantId = run.Tenant.Id, ActorUserId = actor, Action = "STAGE_TEST_ORDER_CREATED", TargetType = "StageTestOrder", TargetId = scenario == "label-write-stale-fixture" ? "9999" : "7101", Reason = "fresh-label-write-fixture:TRENDYOL", CorrelationId = "golden", CreatedAt = Start.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }
        var jobId = run.NewId();
        run.Known[jobId] = "job";
        var code = scenario.StartsWith("label-write", StringComparison.Ordinal) ? MarketplaceCapabilities.LabelWrite : MarketplaceCapabilities.LabelRead;
        var payloadJson = JsonSerializer.Serialize(new CapabilityProbeJobPayload(jobId, packageId, actor, code, 1, 10m, Start, Start.AddMinutes(30)));
        var port = new GoldenMarketplace();
        port.Answers["orders.PackageAction"] = args => AdapterResult<PackageActionResult>.Success(new(((PackageActionCommand)args[0]!).ExternalPackageId, "ACCEPTED", null));
        port.Answers["orders.CreateCommonLabel"] = _ => AdapterResult<bool>.Success(true);
        port.Answers["orders.GetCommonLabel"] = args => scenario == "read-refused"
            ? AdapterResult<CommonLabelDocument>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Authentication, "AUTH_REJECTED", 401))
            : AdapterResult<CommonLabelDocument>.Success(new((string)args[0]!, "ZPL", Encoding.UTF8.GetBytes("^XA^FDprobe^FS^XZ")));
        await run.RunAsync("probe", MarketplaceJobTypes.CapabilityProbe, port, payloadJson, jobId);
        await run.MatchesAsync($"LabelJobs/trendyol-capability_probe-{scenario}.golden.json");
    }

    public static TheoryData<string, string> StageOrderScenarios => new() { { "TRENDYOL", "created" }, { "TRENDYOL", "refused" }, { "TRENDYOL", "wrong-store" }, { "HEPSIBURADA", "sku-not-linked" }, { "HEPSIBURADA", "listing-ambiguous" } };

    [PostgreSqlTheory]
    [MemberData(nameof(StageOrderScenarios))]
    public async Task AStageTestOrderAsksAndStoresTheSameThings(string platform, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = new GoldenRun(fixture, platform) { Environment = "STAGE", ExternalStoreId = scenario == "wrong-store" ? "1234" : "2738" };
        await run.SeedAsync();
        var actor = run.NewId();
        run.Known[actor] = "actor";
        var jobId = run.NewId();
        run.Known[jobId] = "job";
        var payloadJson = JsonSerializer.Serialize(new StageTestOrderJobPayload(jobId, actor, "8690000000001", Start, scenario == "sku-not-linked" ? "HBV00000GOLDEN" : null));
        var port = new GoldenMarketplace();
        port.Answers["orders.CreateStageTestOrder"] = _ => scenario == "refused"
            ? AdapterResult<StageTestOrderResult>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Validation, "STAGE_BARCODE_UNKNOWN", 400))
            : AdapterResult<StageTestOrderResult>.Success(new("7202"));
        await run.RunAsync("create", MarketplaceJobTypes.StageTestOrder, port, payloadJson, jobId);
        await run.MatchesAsync($"LabelJobs/{platform.ToLowerInvariant()}-stage_test_order-{scenario}.golden.json");
    }
}
