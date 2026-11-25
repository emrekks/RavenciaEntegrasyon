using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 25 Nov 2026 C-2: whether a claim counts as a return is decided by how it closed first (ResolvedWith), then by the
// request; the invoice, the overview and the reports read it from one database expression
// (AcceptedReturnPolicy.CountsAsReturnInDatabase) that gives ReturnCustomerRequestPolicy.CountsAsReturn's answer.
[Collection("postgres-integration")]
public sealed class ReturnCountsAsReturnPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string?[] Requests = [null, ReturnCustomerRequestPolicy.Refund, ReturnCustomerRequestPolicy.Change];
    private static readonly string?[] Outcomes = [null, ReturnCustomerRequestPolicy.Refund, ReturnCustomerRequestPolicy.Change];
    // Refunded written with white space .NET trims (a tab, an ideographic space) or another case is still Refunded.
    private static readonly string[] RawStatuses = ["Accepted", "Refunded", "Rejected", " refunded\t", "REFUNDED" + (char)0x3000];
    private static readonly string[] Platforms = ["HEPSIBURADA", "TRENDYOL"];

    [Fact]
    public void How_the_claim_closed_decides_before_the_request()
    {
        // A Hepsiburada exchange request that Hepsiburada closed with a refund is a return (it was none before).
        Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Accepted", "HEPSIBURADA", "REFUND"));
        // A claim closed as an exchange is no return, whatever was asked (it counted before when the request was unknown).
        Assert.False(ReturnCustomerRequestPolicy.CountsAsReturn(null, "Accepted", "HEPSIBURADA", "CHANGE"));
        Assert.False(ReturnCustomerRequestPolicy.CountsAsReturn("REFUND", "Accepted", "TRENDYOL", "CHANGE"));
        // A refunded claim is a return; a claim that does not say how it closed keeps the request rule.
        Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Refunded", "HEPSIBURADA", "CHANGE"));
        Assert.False(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Accepted", "HEPSIBURADA"));
        Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Accepted", "TRENDYOL"));
    }

    [PostgreSqlFact]
    public async Task The_database_expression_gives_the_domain_answer_for_every_combination()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"counts-return-{Guid.NewGuid():N}", DisplayName = "counts-return", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var expected = new Dictionary<Guid, bool>();
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            foreach (var platform in Platforms)
            {
                var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
                var order = new Order
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    ConnectionId = connection.Id,
                    ExternalOrderId = $"ORD-{platform}",
                    OrderNumber = $"ORD-{platform}",
                    Currency = "TRY",
                    GrossAmount = 100m,
                    NetAmount = 100m,
                    OrderedAt = now,
                    LastRemoteModifiedAt = now,
                    CustomerSnapshotJson = "{}",
                    ShipmentAddressSnapshotJson = "{}",
                    InvoiceAddressSnapshotJson = "{}",
                    DerivedStatus = "DELIVERED",
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                };
                db.PlatformConnections.Add(connection);
                db.Orders.Add(order);
                foreach (var request in Requests)
                    foreach (var outcome in Outcomes)
                        foreach (var raw in RawStatuses)
                        {
                            var claim = new ReturnClaim
                            {
                                Id = Guid.CreateVersion7(),
                                TenantId = tenant.Id,
                                ConnectionId = connection.Id,
                                OrderId = order.Id,
                                ExternalClaimId = $"claim-{Guid.NewGuid():N}",
                                Status = ReturnClaimStatus.Approved,
                                RawStatus = raw,
                                CustomerRequest = request,
                                ResolvedWith = outcome,
                                LastRemoteModifiedAt = now,
                                CreatedAt = now,
                                UpdatedAt = now,
                                Version = 1
                            };
                            db.ReturnClaims.Add(claim);
                            expected[claim.Id] = ReturnCustomerRequestPolicy.CountsAsReturn(request, raw, platform, outcome);
                        }
            }
            await db.SaveChangesAsync();
        }

        try
        {
            await using var read = fixture.CreateContext();
            var counted = (await read.ReturnClaims.AsNoTracking().Where(claim => claim.TenantId == tenant.Id).Where(AcceptedReturnPolicy.CountsAsReturnInDatabase(read))
                .Select(claim => claim.Id).ToListAsync()).ToHashSet();
            Assert.Equal(expected.Count, Requests.Length * Outcomes.Length * RawStatuses.Length * Platforms.Length);
            foreach (var (id, counts) in expected) Assert.Equal(counts, counted.Contains(id));
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.ReturnClaims.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
