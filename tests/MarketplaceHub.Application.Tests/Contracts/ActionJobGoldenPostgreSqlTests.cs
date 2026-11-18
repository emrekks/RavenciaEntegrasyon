using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 3a (16 Nov 2026): the package and return decision writes (SHIPMENT_ACTION, RETURN_ACTION),
// pinned before they move out of the processor. Each scenario records the exact command the marketplace receives
// (every field), the read-back, the job result, the effect record's state and what is stored afterwards: an accepted
// send, a lasting refusal, a quota refusal, an unclear answer, the same job run again after each of them, a record closed
// as unresolved, the switches that stop a write before it is sent, and the payloads that cannot be read.
[Collection("postgres-integration")]
public sealed class ActionJobGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;
    private const string WritesOn = "{\"ExternalWritesEnabled\":true}";

    private static RemoteOrder Order(string packageStatus, string? cargoProvider = "ARAS") =>
        new("5001", "5001", Start.AddDays(-1), Start.AddHours(-3), "TRY", 100m, 0m, 100m,
            "{\"customerFirstName\":\"Golden\"}", "{\"city\":\"İstanbul\"}", "{\"city\":\"İstanbul\"}",
            [new RemoteOrderLine("L1", "SKU-1", "8690000000001", "Ürün", 2m, 50m, 20m, packageStatus)],
            [new RemotePackage("P1", null, packageStatus, Start.AddHours(-3), cargoProvider, "TRK1", [new RemotePackageAllocation("L1", 2m, 0, 0, 0, 0)], 100m, 0m, 100m)], "{}", StoreFrontCode: "TR");

    private static AdapterError Refusal(string scenario) => scenario switch
    {
        "uncertain" => GoldenMarketplace.Error(AdapterErrorClass.TransientNetwork, "NETWORK_TIMEOUT"),
        "quota" => GoldenMarketplace.Error(AdapterErrorClass.RateLimit, "RATE_LIMITED", 429),
        _ => GoldenMarketplace.Error(AdapterErrorClass.Validation, "REMOTE_VALIDATION", 400)
    };

    private async Task<GoldenRun> SeedAsync(string platform, bool writesOn, bool claim)
    {
        var run = new GoldenRun(fixture, platform) { ConnectionSettingsJson = writesOn ? WritesOn : null };
        JobGolden.KnowFixtureIds(run.Known, ContractFixture.Read("Trendyol/getClaims-docs-sample.json"));
        run.Settings["FeatureFlags:ExternalWrites"] = "true";
        await run.SeedAsync();
        await using (var db = fixture.CreateContext(new FrozenClock(Start.AddMinutes(-30))))
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), new FrozenClock(Start.AddMinutes(-30)));
            if (claim)
                // The docs sample's first claim carries a replacement package (an exchange request, which the panel never
                // approves); these scenarios are about a return decision, so it is read as a plain return claim.
                await processor.UpsertReturn(run.Tenant.Id, run.Connection.Id, TrendyolJsonMapper.Returns(ContractFixture.Read("Trendyol/getClaims-docs-sample.json")).Items[0] with { CustomerRequest = null }, CancellationToken.None);
            else
                await processor.UpsertOrders(run.Tenant.Id, run.Connection.Id, [Order("Created")], CancellationToken.None);
        }
        await using var check = fixture.CreateContext();
        foreach (var order in await check.Orders.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[order.Id] = $"order-{order.ExternalOrderId}";
        foreach (var package in await check.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[package.Id] = $"package-{package.ExternalPackageId}";
        foreach (var line in await check.OrderLines.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[line.Id] = $"order-line-{line.ExternalLineId}";
        foreach (var line in await check.ReturnLines.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).OrderBy(x => x.ExternalLineId).ToListAsync()) run.Known[line.Id] = $"return-line-{line.ExternalLineId}";
        if (await check.ReturnClaims.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).SingleOrDefaultAsync() is { } stored) run.Known[stored.Id] = "claim";
        return run;
    }

    // ---- SHIPMENT_ACTION -----------------------------------------------------------------------------------------------

    public static TheoryData<string, string> ShipmentScenarios => new()
    {
        { "TRENDYOL", "accepted" }, { "TRENDYOL", "accepted-readback-failed" }, { "TRENDYOL", "refused" }, { "TRENDYOL", "quota" },
        { "TRENDYOL", "uncertain" }, { "TRENDYOL", "unresolved-applied" }, { "TRENDYOL", "unresolved-unknown" }, { "TRENDYOL", "writes-off" },
        { "TRENDYOL", "payload-invalid" }, { "TRENDYOL", "package-missing" }, { "TRENDYOL", "cargo-change-accepted" },
        { "N11", "accepted" }, { "HEPSIBURADA", "unpack-accepted" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(ShipmentScenarios))]
    public async Task AShipmentActionSendsAndStoresTheSameThings(string platform, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = await SeedAsync(platform, writesOn: scenario != "writes-off", claim: false);
        Guid packageId;
        await using (var db = fixture.CreateContext())
            packageId = await db.ShipmentPackages.Where(x => x.TenantId == run.Tenant.Id).Select(x => x.Id).SingleAsync();
        var action = scenario switch { "cargo-change-accepted" => "CHANGE_CARGO_PROVIDER", "unpack-accepted" => "UNPACK", _ => "PICKING" };
        var actionPayload = action == "CHANGE_CARGO_PROVIDER" ? "{\"cargoProvider\":\"YURTICI\"}" : "{\"lines\":[{\"lineId\":1,\"quantity\":2}]}";
        var jobId = run.NewId();
        run.Known[jobId] = "job";
        var effectKey = $"shipment-action:{jobId:N}";
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            var payload = JsonSerializer.Serialize(new ShipmentActionJobPayload(jobId, scenario == "package-missing" ? run.NewId() : packageId, action, actionPayload));
            db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, JobType = MarketplaceJobTypes.ShipmentAction, PayloadJson = payload, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"golden-shipment:{jobId:N}", EffectIdempotencyKey = effectKey, Status = JobStatus.Leased, AvailableAt = Start, CorrelationId = "golden", CreatedAt = Start, Version = 1 });
            if (platform == "N11")
                db.PlatformCapabilities.Add(new PlatformCapability { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, Code = MarketplaceCapabilities.ShipmentWrite, SupportLevel = CapabilitySupportLevel.Unknown, ApiVersion = "V1", Environment = "PRODUCTION", StoreScope = "store-golden", Version = 0 });
            if (scenario.StartsWith("unresolved", StringComparison.Ordinal))
            {
                var effect = new ExternalEffectRecord { Id = run.NewId(), TenantId = run.Tenant.Id, EffectType = MarketplaceJobTypes.ShipmentAction, IdempotencyKey = effectKey, CreatedAt = Start.AddHours(-2), SentAt = Start.AddHours(-2), CompletedAt = Start.AddHours(-1), Outcome = ExternalEffectReconcilePolicy.UnresolvedOutcome, ConnectionId = run.Connection.Id, TargetId = packageId, Action = "PICKING" };
                run.Known[effect.Id] = "effect";
                db.ExternalEffectRecords.Add(effect);
            }
            await db.SaveChangesAsync();
        }
        string payloadJson;
        await using (var db = fixture.CreateContext())
            payloadJson = await db.IntegrationJobs.Where(x => x.Id == jobId).Select(x => x.PayloadJson).SingleAsync();
        if (scenario == "payload-invalid") payloadJson = "{\"jobId\":\"not-a-guid\"";

        var port = new GoldenMarketplace();
        var sends = 0;
        port.Answers["orders.PackageAction"] = args => scenario is "refused" or "quota" or "uncertain" && sends++ == 0
            ? AdapterResult<PackageActionResult>.Failure(Refusal(scenario))
            : AdapterResult<PackageActionResult>.Success(new(((PackageActionCommand)args[0]!).ExternalPackageId, "ACCEPTED", "op-1"));
        port.Answers["orders.GetHinted"] = _ => scenario is "accepted-readback-failed" or "unresolved-unknown"
            ? AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503))
            : AdapterResult<RemoteOrder>.Success(Order(action == "PICKING" ? "Picking" : "Created", action == "CHANGE_CARGO_PROVIDER" ? "YURTICI" : "ARAS"));

        await run.RunAsync("send", MarketplaceJobTypes.ShipmentAction, port, payloadJson, jobId);
        run.Clock.Advance(TimeSpan.FromMinutes(1));
        await run.RunAsync("same-job-again", MarketplaceJobTypes.ShipmentAction, port, payloadJson, jobId);
        await run.MatchesAsync($"ActionJobs/{platform.ToLowerInvariant()}-shipment_action-{scenario}.golden.json");
    }

    // ---- RETURN_ACTION -------------------------------------------------------------------------------------------------

    public static TheoryData<string> ReturnScenarios => new()
    {
        "approve-accepted", "reject-accepted-readback-failed", "refused", "quota", "uncertain", "done-before", "unresolved",
        "decision-succeeded", "writes-off", "payload-invalid", "decision-missing", "no-lines"
    };

    [PostgreSqlTheory]
    [MemberData(nameof(ReturnScenarios))]
    public async Task AReturnDecisionSendsAndStoresTheSameThings(string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = await SeedAsync("TRENDYOL", writesOn: scenario != "writes-off", claim: true);
        Guid claimId;
        await using (var db = fixture.CreateContext())
            claimId = await db.ReturnClaims.Where(x => x.TenantId == run.Tenant.Id).Select(x => x.Id).SingleAsync();
        var decision = new ReturnDecision
        {
            Id = run.NewId(),
            TenantId = run.Tenant.Id,
            ClaimId = claimId,
            Action = scenario.StartsWith("reject", StringComparison.Ordinal) ? "REJECT" : "APPROVE",
            ReasonCode = scenario.StartsWith("reject", StringComparison.Ordinal) ? "DAMAGED" : null,
            Explanation = "golden karar",
            IdempotencyKey = "golden-return-decision",
            Status = scenario == "decision-succeeded" ? "SUCCEEDED" : "PENDING",
            ActorUserId = run.NewId(),
            CreatedAt = Start.AddMinutes(-1)
        };
        run.Known[decision.Id] = "decision";
        run.Known[decision.ActorUserId] = "actor";
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            db.ReturnDecisions.Add(decision);
            if (scenario is "done-before" or "unresolved")
            {
                var effect = new ExternalEffectRecord { Id = run.NewId(), TenantId = run.Tenant.Id, EffectType = MarketplaceJobTypes.ReturnAction, IdempotencyKey = decision.IdempotencyKey, CreatedAt = Start.AddHours(-2), SentAt = Start.AddHours(-2), CompletedAt = Start.AddHours(-1), Outcome = scenario == "unresolved" ? ExternalEffectReconcilePolicy.UnresolvedOutcome : ExternalEffectReconcilePolicy.AppliedOutcome, ConnectionId = run.Connection.Id, TargetId = claimId, Action = "RETURN_APPROVE" };
                run.Known[effect.Id] = "effect";
                db.ExternalEffectRecords.Add(effect);
            }
            await db.SaveChangesAsync();
        }
        var payloadJson = scenario switch
        {
            "payload-invalid" => "{\"decisionId\":\"x\"}",
            "decision-missing" => $$"""{"decisionId":"{{run.NewId()}}"}""",
            "no-lines" => $$"""{"decisionId":"{{decision.Id}}","returnLineIds":["{{run.NewId()}}"]}""",
            _ => $$"""{"decisionId":"{{decision.Id}}"}"""
        };

        var claimRemote = TrendyolJsonMapper.Returns(ContractFixture.Read("Trendyol/getClaims-docs-sample.json")).Items[0];
        var port = new GoldenMarketplace();
        var sends = 0;
        port.Answers["returns.Execute"] = _ => scenario is "refused" or "quota" or "uncertain" && sends++ == 0
            ? AdapterResult<ReturnActionResult>.Failure(Refusal(scenario))
            : AdapterResult<ReturnActionResult>.Success(new(claimRemote.ExternalClaimId, "ACCEPTED", "return-op-1"));
        port.Answers["returns.Get"] = _ => scenario == "reject-accepted-readback-failed"
            ? AdapterResult<RemoteReturnClaim>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503))
            : AdapterResult<RemoteReturnClaim>.Success(claimRemote with { RawStatus = "Accepted", LastModifiedAt = Start.AddMinutes(-1), Lines = [.. claimRemote.Lines.Select(line => line with { RawStatus = "Accepted" })] });

        await run.RunAsync("send", MarketplaceJobTypes.ReturnAction, port, payloadJson, run.NewId());
        run.Clock.Advance(TimeSpan.FromMinutes(1));
        await run.RunAsync("same-job-again", MarketplaceJobTypes.ReturnAction, port, payloadJson, run.NewId());
        await run.MatchesAsync($"ActionJobs/trendyol-return_action-{scenario}.golden.json");
    }
}
