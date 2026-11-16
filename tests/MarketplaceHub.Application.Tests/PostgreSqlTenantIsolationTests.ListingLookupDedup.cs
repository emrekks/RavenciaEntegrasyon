using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// J4 (15 Nov 2026): the listing scan queues its lookup job under a key that names its run. A run that completed and saved
// the job, but whose verification job ran again (the worker stopped after the save, before closing the job), reached the
// same key again and failed on the unique index on every retry. The lookup now goes through JobDedupPolicy.ReuseAnyWithKey:
// the job already there is the lookup, and nothing is added.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task J4_TheScansLookupForARunThatAlreadyQueuedItIsNotAddedTwice()
    {
        var tenant = NewTenant("j4-listing-lookup");
        var connection = NewQuestionConnection(tenant);
        await SeedTenantAndConnectionAsync(tenant, connection);
        var runJobId = Guid.CreateVersion7();
        var dedup = $"listing-lookup:{connection.Id:N}:scan:{runJobId:N}";
        var payload = new ListingVerificationService.ListingLookupJobPayload(connection.Id, [Guid.CreateVersion7()], ListingVerificationService.ListingLookupScanSource, false, runJobId);
        try
        {
            foreach (var expectedCreated in new[] { true, false })
            {
                await using var db = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
                Assert.Equal(expectedCreated, await processor.AddListingLookupJobAsync(tenant.Id, connection.Id, dedup, payload, fixture.Now, CancellationToken.None));
                await db.SaveChangesAsync();
            }
            await using var check = fixture.CreateContext();
            Assert.Equal(1, await check.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup && x.JobDedupKey == dedup));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }
}
