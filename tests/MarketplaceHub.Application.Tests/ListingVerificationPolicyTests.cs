using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ListingVerificationPolicyTests
{
    private static readonly TimeSpan Istanbul = TimeSpan.FromHours(3);
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new DateTimeOffset(2026, 10, day, hour, minute, 0, Istanbul).ToUniversalTime();

    [Fact]
    public void A_connection_never_verified_waits_for_the_night_window_and_runs_inside_it()
    {
        Assert.False(ListingVerificationPolicy.IsDue(At(29, 2, 59), null, null, 0));
        Assert.True(ListingVerificationPolicy.IsDue(At(29, 3, 0), null, null, 0));
        Assert.True(ListingVerificationPolicy.IsDue(At(29, 5, 59), null, null, 0));
        // Deployed in the afternoon: the first run is the next night, not now.
        Assert.False(ListingVerificationPolicy.IsDue(At(29, 14, 0), null, null, 0));
        // The jitter shifts the start inside the window.
        Assert.False(ListingVerificationPolicy.IsDue(At(29, 3, 30), null, null, 3600));
        Assert.True(ListingVerificationPolicy.IsDue(At(29, 4, 0), null, null, 3600));
    }

    [Fact]
    public void A_run_completed_in_the_cycle_is_not_repeated_until_the_next_night()
    {
        var completed = At(29, 3, 40);
        Assert.False(ListingVerificationPolicy.IsDue(At(29, 10, 0), completed, At(29, 3, 5), 0));
        Assert.False(ListingVerificationPolicy.IsDue(At(30, 2, 0), completed, At(29, 3, 5), 0));
        Assert.True(ListingVerificationPolicy.IsDue(At(30, 3, 0), completed, At(29, 3, 5), 0));
    }

    [Fact]
    public void A_missed_or_failed_night_is_caught_up_at_the_first_chance_after_an_hour()
    {
        // The host was down all night: the run starts in the morning instead of skipping the day.
        Assert.True(ListingVerificationPolicy.IsDue(At(29, 9, 15), At(28, 3, 30), At(28, 3, 2), 0));
        // Queued at 03:10 but the connection failed: queued again an hour later, not before.
        Assert.False(ListingVerificationPolicy.IsDue(At(29, 3, 50), At(28, 3, 30), At(29, 3, 10), 0));
        Assert.True(ListingVerificationPolicy.IsDue(At(29, 4, 10), At(28, 3, 30), At(29, 3, 10), 0));
        // Yesterday's run never completed and today's window has not opened yet: it still runs now.
        Assert.True(ListingVerificationPolicy.IsDue(At(29, 1, 0), At(27, 3, 30), At(28, 3, 10), 0));
    }

    [Fact]
    public void The_cycle_starts_at_three_in_the_morning_istanbul_time()
    {
        Assert.Equal(At(29, 3, 0), ListingVerificationPolicy.CycleStart(At(29, 3, 0)));
        Assert.Equal(At(28, 3, 0), ListingVerificationPolicy.CycleStart(At(29, 2, 59)));
        Assert.Equal(TimeSpan.Zero, ListingVerificationPolicy.CycleStart(At(29, 12, 0)).Offset);
    }

    [Theory]
    [InlineData("N11", 0, true)]
    [InlineData("PAZARAMA", 1, true)]
    [InlineData("PTTAVM", 1, true)]
    [InlineData("N11", 2, false)]
    [InlineData("PTTAVM", 5, false)]
    [InlineData("TRENDYOL", 0, false)]
    [InlineData("HEPSIBURADA", 0, false)]
    [InlineData("SHOPIFY", 0, false)]
    public void Flat_catalogs_only_observe_their_first_two_completed_runs(string platform, int completedRuns, bool observes) =>
        Assert.Equal(observes, ListingVerificationPolicy.ObservesOnly(platform, completedRuns));

    [Fact]
    public void Only_a_complete_small_answer_without_the_code_is_not_found()
    {
        (string?, string?, string?)[] other = [("V9", "SKU9", "B9")];
        Assert.Equal(MarketplaceListingLookupOutcome.Found, ListingVerificationPolicy.Outcome(["B1"], [("V1", "SKU1", "b1")], answerComplete: false));
        Assert.Equal(MarketplaceListingLookupOutcome.NotFound, ListingVerificationPolicy.Outcome(["B1"], [], answerComplete: true));
        Assert.Equal(MarketplaceListingLookupOutcome.NotFound, ListingVerificationPolicy.Outcome(["B1"], other, answerComplete: true));
        Assert.Equal(MarketplaceListingLookupOutcome.Unknown, ListingVerificationPolicy.Outcome(["B1"], other, answerComplete: false));
        var unfiltered = Enumerable.Range(0, ListingVerificationPolicy.MaxLookupVariants + 1).Select(i => ((string?)$"V{i}", (string?)$"S{i}", (string?)$"B{i}x")).ToList();
        Assert.Equal(MarketplaceListingLookupOutcome.Unknown, ListingVerificationPolicy.Outcome(["B1"], unfiltered, answerComplete: true));
        Assert.Equal(MarketplaceListingLookupOutcome.Unsupported, ListingVerificationPolicy.Outcome([null, " "], [], answerComplete: true));
    }

    [Fact]
    public void Each_platform_is_asked_by_its_own_code_and_never_by_a_placeholder()
    {
        var trendyol = new MarketplaceVariantLink { ExternalId = "TY-ID", ExternalBarcode = "869001" };
        Assert.Equal(("869001", false), ListingVerificationPolicy.LookupKey("TRENDYOL", trendyol));
        Assert.Equal(("HBCV001", true), ListingVerificationPolicy.LookupKey("HEPSIBURADA", new MarketplaceVariantLink { ExternalId = "HBCV001", ExternalSku = "M-1" }));
        Assert.Equal(("SC-1", false), ListingVerificationPolicy.LookupKey("N11", new MarketplaceVariantLink { ExternalId = "SC-1" }));
        Assert.Null(ListingVerificationPolicy.LookupKey("N11", new MarketplaceVariantLink { ExternalId = "n11:4455" }));
        Assert.Equal(("869002", false), ListingVerificationPolicy.LookupKey("PAZARAMA", new MarketplaceVariantLink { ExternalId = "869002" }));
        Assert.Equal(("869003", false), ListingVerificationPolicy.LookupKey("PTTAVM", new MarketplaceVariantLink { ExternalId = "869003" }));
        Assert.Equal(("869004", false), ListingVerificationPolicy.LookupKey("SHOPIFY", new MarketplaceVariantLink { ExternalId = "listing:SKU-4", ExternalBarcode = "869004" }));
        Assert.Null(ListingVerificationPolicy.LookupKey("SHOPIFY", new MarketplaceVariantLink { ExternalId = "listing:SKU-5" }));
    }

    [Fact]
    public void A_code_the_marketplace_no_longer_knows_gets_no_send_and_a_twice_refused_one_only_variant_sends()
    {
        Assert.True(MarketplaceVariantLinkHealth.SkipsPush(MarketplaceVariantLinkHealth.NotFound, variantScoped: false));
        Assert.True(MarketplaceVariantLinkHealth.SkipsPush(MarketplaceVariantLinkHealth.NotFound, variantScoped: true));
        Assert.True(MarketplaceVariantLinkHealth.SkipsPush(MarketplaceVariantLinkHealth.Rejected, variantScoped: false));
        Assert.False(MarketplaceVariantLinkHealth.SkipsPush(MarketplaceVariantLinkHealth.Rejected, variantScoped: true));
        Assert.False(MarketplaceVariantLinkHealth.SkipsPush(MarketplaceVariantLinkHealth.Suspect, variantScoped: false));
        Assert.False(MarketplaceVariantLinkHealth.SkipsPush(null, variantScoped: false));
    }

    [Fact]
    public void A_listing_placeholder_is_never_a_remote_variant_id()
    {
        Assert.Null(MarketplaceVariantLinkHealth.RemoteVariantId("listing:12345"));
        Assert.Equal("44791234567890", MarketplaceVariantLinkHealth.RemoteVariantId("44791234567890"));
        Assert.Equal("44791234567890", MarketplaceJobProcessor.ShopifyVariantIdOrNull("gid://shopify/ProductVariant/44791234567890"));
        Assert.Equal("447", MarketplaceJobProcessor.ShopifyVariantIdOrNull(" 447 "));
        Assert.Null(MarketplaceJobProcessor.ShopifyVariantIdOrNull("SKU-1"));
        Assert.Null(MarketplaceJobProcessor.ShopifyVariantIdOrNull(null));
    }

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("SHOPIFY")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    public void Every_platform_gets_the_nightly_verification_policy_switched_on(string platform)
    {
        var supported = platform switch
        {
            "HEPSIBURADA" => ScheduledJobProducer.SupportsHepsiburadaScheduledPolicy(ListingVerificationPolicy.ResourceType),
            "SHOPIFY" => ScheduledJobProducer.SupportsShopifyPolicy(ListingVerificationPolicy.ResourceType),
            "N11" => ScheduledJobProducer.SupportsN11ScheduledPolicy(ListingVerificationPolicy.ResourceType),
            "PAZARAMA" => ScheduledJobProducer.SupportsPazaramaScheduledPolicy(ListingVerificationPolicy.ResourceType),
            "PTTAVM" => ScheduledJobProducer.SupportsPttAvmScheduledPolicy(ListingVerificationPolicy.ResourceType),
            _ => true
        };
        Assert.True(supported);
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled(platform, ListingVerificationPolicy.ResourceType));
        Assert.True(ListingVerificationPolicy.Supports(platform));
    }

    [Fact]
    public void The_verification_reads_in_the_product_lane_and_never_takes_a_background_slot()
    {
        Assert.Equal("products", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.ListingVerify));
        Assert.Equal("listing-lookup", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.ListingLookup));
        var priority = JobPriorityPolicy.For(MarketplaceJobTypes.ListingVerify, userInitiated: false);
        Assert.Equal("product-catalog", WorkerLanes.LaneFor(MarketplaceJobTypes.ListingVerify, priority));
        Assert.Equal("product-catalog", WorkerLanes.LaneFor(MarketplaceJobTypes.ListingLookup, JobPriorityPolicy.For(MarketplaceJobTypes.ListingLookup, userInitiated: false)));
        Assert.Contains(MarketplaceJobTypes.ListingVerify, MarketplaceJobTypes.ProcessorJobTypes);
        Assert.Contains(MarketplaceJobTypes.ListingLookup, MarketplaceJobTypes.ProcessorJobTypes);
    }

    [Fact]
    public void The_run_summary_names_the_platform_and_the_products()
    {
        Assert.Equal("Trendyol: 3 ürün pazaryerinde bulunamadı.", ListingVerificationPolicy.NotFoundSummary("TRENDYOL", 3, 3));
        Assert.Equal("n11: 2 ürün (5 varyant) pazaryerinde bulunamadı.", ListingVerificationPolicy.NotFoundSummary("N11", 2, 5));
        Assert.Equal("/products?platform=TRENDYOL:NOT_FOUND", ListingVerificationPolicy.NotFoundHref("trendyol"));
    }

    [Fact]
    public void The_run_state_keeps_its_completed_runs_across_a_new_run_and_reads_back_from_json()
    {
        var at = At(29, 3, 10);
        var state = new ListingVerificationState().Begin(Guid.NewGuid(), at) with { Next = "p2", Pages = 2, Received = 200 };
        var completed = ListingVerificationState.Parse(state.Complete(at.AddMinutes(20)).ToJson());
        Assert.Equal(1, completed.CompletedRuns);
        Assert.Equal(at.AddMinutes(20), completed.LastCompletedAt);
        Assert.Null(completed.RunJobId);
        var next = completed.Begin(Guid.NewGuid(), at.AddDays(1));
        Assert.Equal(1, next.CompletedRuns);
        Assert.Equal(0, next.Pages);
        Assert.Null(next.Next);
        Assert.Equal(new ListingVerificationState(), ListingVerificationState.Parse("not json"));
    }
}
