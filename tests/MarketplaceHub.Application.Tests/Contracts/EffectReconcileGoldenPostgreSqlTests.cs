using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 2 (16 Nov 2026): the check of an unclear marketplace answer (EXTERNAL_EFFECT_RECONCILE), pinned
// before it moves out of the processor; every write job uses the effect records it settles. Each scenario seeds one open
// effect record (a picking, a cargo provider change, a package creation, a return approval or rejection) and runs the
// check: what it reads from the marketplace (an order or a claim, by the record's idempotency key), the result, and the
// record, the decision, the issue and the stored order or claim afterwards. Repeated unclear reads close the record as
// unresolved; a clear "not applied" releases it; the owner job still open makes the check wait.
[Collection("postgres-integration")]
public sealed class EffectReconcileGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;

    public static TheoryData<string> Scenarios => new()
    {
        "payload-invalid", "already-completed", "owner-open",
        "picking-applied", "picking-not-applied", "picking-unclear",
        "cargo-change-applied", "create-package-applied", "create-package-not-applied",
        "return-approve-applied", "return-reject-not-applied", "return-unclear"
    };

    private static RemoteOrder Order(string packageStatus, string? cargoProvider = "ARAS", bool secondPackage = false)
    {
        RemotePackage Package(string id, decimal quantity) => new(id, null, packageStatus, Start.AddHours(-3), cargoProvider, "TRK1",
            [new RemotePackageAllocation("L1", quantity, 0, 0, 0, 0)], 100m, 0m, 100m);
        return new RemoteOrder("5001", "5001", Start.AddDays(-1), Start.AddHours(-3), "TRY", 100m, 0m, 100m,
            "{\"customerFirstName\":\"Golden\"}", "{\"city\":\"İstanbul\"}", "{\"city\":\"İstanbul\"}",
            [new RemoteOrderLine("L1", "SKU-1", "8690000000001", "Ürün", 2m, 50m, 20m, packageStatus)],
            secondPackage ? [Package("P1", 1m), Package("P2", 1m)] : [Package("P1", 2m)], "{}");
    }

    [PostgreSqlTheory]
    [MemberData(nameof(Scenarios))]
    public async Task AnUnclearAnswerIsCheckedTheSameWay(string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = new GoldenRun(fixture, "TRENDYOL");
        JobGolden.KnowFixtureIds(run.Known, ContractFixture.Read("Trendyol/getClaims-docs-sample.json"));
        await run.SeedAsync();
        var isReturn = scenario.StartsWith("return-", StringComparison.Ordinal);
        await using (var db = fixture.CreateContext(new FrozenClock(Start.AddMinutes(-30))))
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), new FrozenClock(Start.AddMinutes(-30)));
            if (isReturn)
                await processor.UpsertReturn(run.Tenant.Id, run.Connection.Id, TrendyolJsonMapper.Returns(ContractFixture.Read("Trendyol/getClaims-docs-sample.json")).Items[0], CancellationToken.None);
            else
                await processor.UpsertOrders(run.Tenant.Id, run.Connection.Id, [Order("Created")], CancellationToken.None);
        }

        Guid targetId;
        await using (var db = fixture.CreateContext())
        {
            foreach (var order in await db.Orders.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[order.Id] = $"order-{order.ExternalOrderId}";
            foreach (var package in await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[package.Id] = $"package-{package.ExternalPackageId}";
            foreach (var line in await db.OrderLines.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync()) run.Known[line.Id] = $"order-line-{line.ExternalLineId}";
            var claim = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).SingleOrDefaultAsync();
            if (claim is not null) run.Known[claim.Id] = "claim";
            targetId = isReturn ? claim!.Id
                : scenario.StartsWith("create-package", StringComparison.Ordinal) ? await db.Orders.Where(x => x.TenantId == run.Tenant.Id).Select(x => x.Id).SingleAsync()
                : await db.ShipmentPackages.Where(x => x.TenantId == run.Tenant.Id).Select(x => x.Id).SingleAsync();
        }

        var action = scenario switch
        {
            _ when scenario.StartsWith("cargo-change", StringComparison.Ordinal) => "CHANGE_CARGO_PROVIDER",
            _ when scenario.StartsWith("create-package", StringComparison.Ordinal) => "CREATE_PACKAGE",
            "return-approve-applied" or "return-unclear" => "RETURN_APPROVE",
            "return-reject-not-applied" => "RETURN_REJECT",
            _ => "PICKING"
        };
        var effect = new ExternalEffectRecord
        {
            Id = run.NewId(),
            TenantId = run.Tenant.Id,
            EffectType = isReturn ? MarketplaceJobTypes.ReturnAction : MarketplaceJobTypes.ShipmentAction,
            IdempotencyKey = $"golden-effect:{action.ToLowerInvariant()}",
            CreatedAt = Start.AddMinutes(-20),
            SentAt = Start.AddMinutes(-19),
            ConnectionId = run.Connection.Id,
            TargetId = targetId,
            Action = action,
            ExpectedValue = action switch { "CHANGE_CARGO_PROVIDER" => "YURTICI", "CREATE_PACKAGE" => "1", _ => null },
            CompletedAt = scenario == "already-completed" ? Start.AddMinutes(-5) : null,
            Outcome = scenario == "already-completed" ? "APPLIED" : null
        };
        run.Known[effect.Id] = "effect";
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            db.ExternalEffectRecords.Add(effect);
            if (isReturn)
            {
                var decision = new ReturnDecision { Id = run.NewId(), TenantId = run.Tenant.Id, ClaimId = targetId, Action = action == "RETURN_APPROVE" ? "APPROVE" : "REJECT", IdempotencyKey = effect.IdempotencyKey, Status = "SUBMITTING", ActorUserId = run.NewId(), CreatedAt = Start.AddMinutes(-20) };
                run.Known[decision.Id] = "decision";
                run.Known[decision.ActorUserId] = "actor";
                db.ReturnDecisions.Add(decision);
            }
            if (scenario == "owner-open")
            {
                var job = new IntegrationJob { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, JobType = effect.EffectType, PayloadJson = "{}", PayloadVersion = 1, PayloadHash = "h", JobDedupKey = "golden-owner", EffectIdempotencyKey = effect.IdempotencyKey, Status = JobStatus.Pending, AvailableAt = Start, CorrelationId = "owner", CreatedAt = Start.AddMinutes(-20), Version = 1 };
                run.Known[job.Id] = "owner-job";
                db.IntegrationJobs.Add(job);
            }
            await db.SaveChangesAsync();
        }

        var port = new GoldenMarketplace();
        var claimRemote = isReturn ? TrendyolJsonMapper.Returns(ContractFixture.Read("Trendyol/getClaims-docs-sample.json")).Items[0] : null;
        switch (scenario)
        {
            case "picking-applied":
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Success(Order("Picking"));
                break;
            case "picking-not-applied":
            case "create-package-not-applied":
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Success(Order("Created"));
                break;
            case "picking-unclear":
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503));
                break;
            case "cargo-change-applied":
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Success(Order("Created", cargoProvider: "YURTICI"));
                break;
            case "create-package-applied":
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Success(Order("Created", secondPackage: true));
                break;
            case "return-approve-applied":
                port.Answers["returns.Get"] = _ => AdapterResult<RemoteReturnClaim>.Success(claimRemote! with { RawStatus = "Accepted", LastModifiedAt = Start.AddMinutes(-1), Lines = [.. claimRemote.Lines.Select(line => line with { RawStatus = "Accepted" })] });
                break;
            case "return-reject-not-applied":
                port.Answers["returns.Get"] = _ => AdapterResult<RemoteReturnClaim>.Success(claimRemote!);
                break;
            case "return-unclear":
                port.Answers["returns.Get"] = _ => AdapterResult<RemoteReturnClaim>.Failure(GoldenMarketplace.Error(AdapterErrorClass.TransientNetwork, "NETWORK_TIMEOUT"));
                break;
        }

        var payload = scenario == "payload-invalid" ? "{}" : $$"""{"effectId":"{{effect.Id}}"}""";
        var result = await run.RunAsync("check", MarketplaceJobTypes.ExternalEffectReconcile, port, payload, run.NewId());
        // An unclear answer is read again, as the queue would run the same job again, until the record is closed.
        for (var again = 1; result.Continues && again <= 6; again++)
        {
            run.Clock.Advance(TimeSpan.FromMinutes(8));
            result = await run.RunAsync($"check-{again}", MarketplaceJobTypes.ExternalEffectReconcile, port, payload, run.NewId());
        }
        await run.MatchesAsync($"EffectReconcile/{scenario}.golden.json");
    }
}
