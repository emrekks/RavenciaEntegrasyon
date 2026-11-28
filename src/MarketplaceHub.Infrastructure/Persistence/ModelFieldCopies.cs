using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 D5 step B2b: whether the colour cards and variants still keep their copy columns of the model's shared
// fields (description, VAT rate and inclusion, category, warranty, delivery days, desi). Every reader reads the model
// (EffectiveCatalog, step B2a), so the copies are only a rollback aid; with the switch off no save writes them.
// - The name is not one of them: it is each colour card's own and is written as before. The brand is the card's link to
//   its model (the model key) and is written as before. Constraint-forced writes stay: a new card's description (the
//   column is NOT NULL) and a reset's "CategoryId"/"BrandId" = NULL before categories or brands go (RESTRICT keys).
// - Switching it on again rewrites nothing by itself (no backfill, owner rule): copies of rows edited while it was off
//   stay stale, which is harmless because nothing reads them.
// Process-wide, read once at startup from Catalog:MirrorModelFields in the API's and the Worker's appsettings.json (the
// two must carry the same value; production takes no environment override without a compose edit, so a flip is a release,
// docs/RUNBOOK-GERI-ALMA.md). Tests set it for a whole run (MARKETPLACEHUB_TEST_MIRROR_MODEL_FIELDS) or for one flow (Use).
public static class ModelFieldCopies
{
    public const string Key = "Catalog:MirrorModelFields";

    private static volatile bool configured = true;
    private static volatile bool pinned;
    private static readonly AsyncLocal<bool?> Scoped = new();

    // True: the copies are still written (the shipped default until the flip release).
    public static bool Written => Scoped.Value ?? configured;

    // A host started inside a test run (an API test) reads its appsettings.json too; a value the run pinned wins.
    public static void Configure(IConfiguration configuration)
    {
        if (!pinned) configured = configuration.GetValue(Key, true);
    }

    // The whole process, pinned (a test run's module initializer).
    public static void Set(bool written)
    {
        configured = written;
        pinned = true;
    }

    // One flow (a test that compares both positions): the value holds for this async flow until disposed.
    public static IDisposable Use(bool written)
    {
        var previous = Scoped.Value;
        Scoped.Value = written;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }
}
