using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Pre-launch scan follow-ups (13 Nov 2026): observe-only imports give a product they open its mapped category; a full
// Hepsiburada read resumes its month of packages instead of starting again; a Pazarama order page that repeats ends the
// window; a new root category of a structure import takes a key that is free.
public sealed class AdapterScanFollowUpTests
{
    [Fact]
    public void An_observe_only_read_gives_a_product_it_opens_the_mapped_category_and_leaves_an_existing_one()
    {
        var mapped = new Category { Id = Guid.CreateVersion7(), Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz" };

        Assert.Same(mapped, MarketplaceJobProcessor.ObservedProductCategory(observeOnly: true, addOnly: false, isNewProduct: true, mapped));
        Assert.Null(MarketplaceJobProcessor.ObservedProductCategory(observeOnly: true, addOnly: false, isNewProduct: false, mapped));
        Assert.Null(MarketplaceJobProcessor.ObservedProductCategory(observeOnly: false, addOnly: false, isNewProduct: true, mapped));
        Assert.Null(MarketplaceJobProcessor.ObservedProductCategory(observeOnly: true, addOnly: false, isNewProduct: true, null));
    }

    private static string PackageState(DateTimeOffset anchor, DateTimeOffset start, int window, int offset = 0) =>
        JsonSerializer.Serialize(new MarketplaceJobProcessor.HepsiburadaPackageSyncState(MarketplaceJobProcessor.HepsiburadaPackageSyncStateVersion, anchor, start, window, offset));

    [Fact]
    public void A_full_Hepsiburada_read_resumes_its_unfinished_month_of_packages()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var anchor = now.AddHours(-1);
        var baseline = PackageState(anchor, OpenOrderLifecyclePolicy.HepsiburadaPackageStatusHistoryCutoff(anchor), 7);

        // Resumed on a full read while younger than a day; an ordinary saved state is not taken for a full read.
        Assert.Equal(7, MarketplaceJobProcessor.ReadHepsiburadaPackageSyncState(baseline, now, forceBaseline: true)!.WindowIndex);
        Assert.Null(MarketplaceJobProcessor.ReadHepsiburadaPackageSyncState(baseline, now.AddDays(2), forceBaseline: true));
        Assert.Null(MarketplaceJobProcessor.ReadHepsiburadaPackageSyncState(PackageState(anchor, anchor.AddDays(-1), 1), now, forceBaseline: true));
        Assert.NotNull(MarketplaceJobProcessor.ReadHepsiburadaPackageSyncState(PackageState(anchor, anchor.AddDays(-1), 1), now, forceBaseline: false));

        // The orders of that full read finished before the package month began: they are not read again.
        Assert.True(MarketplaceJobProcessor.HepsiburadaOrdersDoneForPackageBaseline(null, anchor.AddMinutes(-5), baseline, now));
        Assert.False(MarketplaceJobProcessor.HepsiburadaOrdersDoneForPackageBaseline("{}", anchor.AddMinutes(-5), baseline, now));
        Assert.False(MarketplaceJobProcessor.HepsiburadaOrdersDoneForPackageBaseline(null, anchor.AddMinutes(5), baseline, now));
        Assert.False(MarketplaceJobProcessor.HepsiburadaOrdersDoneForPackageBaseline(null, anchor.AddMinutes(-5), null, now));
    }

    [Fact]
    public void A_Pazarama_order_page_that_repeats_the_one_before_ends_the_window()
    {
        Assert.True(MarketplaceJobProcessor.PazaramaOrderPageRepeated("A,B", "A,B"));
        Assert.False(MarketplaceJobProcessor.PazaramaOrderPageRepeated(null, "A,B"));
        Assert.False(MarketplaceJobProcessor.PazaramaOrderPageRepeated("A,B", "C,D"));
        Assert.False(MarketplaceJobProcessor.PazaramaOrderPageRepeated(null, null));
        Assert.True(MarketplaceJobProcessor.PazaramaOrderPageCap >= 100);
    }

    [Fact]
    public void A_new_structure_root_category_takes_a_free_key()
    {
        var keys = MarketplaceJobProcessor.StructureCategoryKeys("Bluz (Hepsiburada)", "KADIN BLUZ", "123");

        Assert.Equal("KADIN BLUZ", MarketplaceJobProcessor.FreeStructureCategoryKey(keys, new HashSet<string>(), "Bluz (Hepsiburada)"));
        // The path's key is held (a switched-off root, or the category mapped to another remote one): the name's key.
        Assert.Equal(keys[1], MarketplaceJobProcessor.FreeStructureCategoryKey(keys, new HashSet<string> { "KADIN BLUZ" }, "Bluz (Hepsiburada)"));
        var allTaken = keys.ToHashSet();
        var fallback = MarketplaceJobProcessor.FreeStructureCategoryKey(keys, allTaken, "Bluz (Hepsiburada)");
        Assert.DoesNotContain(fallback, allTaken);
    }
}
