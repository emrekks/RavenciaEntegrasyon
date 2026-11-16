using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 round 10 step 2 (16 Nov 2026): the reference reads, moved out of the job processor as they were: the
// category, field, value and brand lists (SyncReferences, the brand list read whole and stored by row digest), the
// mapping revalidation after a new list, and the follow-ups of a reference job (the field lists of mapped categories,
// the value lists of a category's fields). It works for one run of one job (run) on the job's AppDbContext. The brand
// list is read in full and the user maps it: nothing is looked up or mapped by name. Reads only: nothing is sent to a
// marketplace.
internal sealed partial class ReferenceSyncService(
    AppDbContext db,
    IReferenceDataPort references,
    TimeProvider timeProvider,
    OrderIngestionService orderIngestion,
    JobRunContext run)
{
    private const int MaxReferenceItems = 500_000;
    private const int MaxBrandReferenceItems = 2_000_000;

    public JobRunContext Run => run;

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static string Hash(string value) => TextValues.Sha256Hex(value);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
}
