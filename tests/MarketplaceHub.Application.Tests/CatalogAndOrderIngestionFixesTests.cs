using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit fixes of 5 Nov 2026: model-code mapping narrowed by colour and size on every marketplace (Y5), one allocation
// per package line (T2-DO1), the Hepsiburada open-return scan held while no claim is open, and Trendyol split parents
// and refused orders (O8, T2-O8).
public sealed class CatalogAndOrderIngestionFixesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");

    private static RemoteCatalogVariant Remote(string id, string? modelCode, Dictionary<string, string> options) =>
        new(id, $"SKU-{id}", null, modelCode, options, false, 100m, null, 20m, 1m, "TRY", "{}");

    private static MarketplaceJobProcessor.ModelCodeVariant Panel(Guid productId, string signature, string modelCode = "MZ100") => new(productId, modelCode, signature);

    // Y5: Trendyol and Hepsiburada bound a remote variant to the only panel variant holding its model code, whatever its
    // colour; the candidate is now taken only when its colour and size are the remote variant's.
    [Fact]
    public void ModelCodeCandidateOfAnotherColourIsNeverTaken()
    {
        var blueL = Panel(Guid.NewGuid(), "Renk: Mavi | Beden: L");
        var remoteRedL = new Dictionary<string, string> { ["Renk"] = "Kırmızı", ["Beden"] = "L" };

        Assert.Empty(MarketplaceJobProcessor.NarrowModelCodeCandidates([blueL], x => x.OptionSignature, remoteRedL, "Kırmızı Elbise"));
        Assert.Equal([blueL], MarketplaceJobProcessor.NarrowModelCodeCandidates([blueL], x => x.OptionSignature, new Dictionary<string, string> { ["Renk"] = "mavi", ["Beden"] = "l" }, null));
    }

    [Fact]
    public void ModelCodeCandidatesNarrowToTheOneWithTheRemoteColourAndSize()
    {
        var product = Guid.NewGuid();
        var blueM = Panel(product, "Renk: Mavi | Beden: M");
        var blueL = Panel(product, "Renk: Mavi | Beden: L");
        var greenL = Panel(product, "Renk: Yeşil | Beden: L");

        // Turkish letters and case do not matter; a marketplace "Color"/"Size" finds the panel's Renk/Beden.
        Assert.Equal([greenL], MarketplaceJobProcessor.NarrowModelCodeCandidates([blueM, blueL, greenL], x => x.OptionSignature, new Dictionary<string, string> { ["Color"] = "YEŞİL", ["Size"] = "L" }, null));
        // A colour without a size leaves both blue sizes: undecided, never the first of them.
        Assert.Equal(2, MarketplaceJobProcessor.NarrowModelCodeCandidates([blueM, blueL, greenL], x => x.OptionSignature, new Dictionary<string, string> { ["Renk"] = "Mavi" }, null).Count);
    }

    [Fact]
    public void ARemoteVariantWithoutColourOrSizeKeepsOnlyAnUncontradictedSoleCandidate()
    {
        var plain = Panel(Guid.NewGuid(), "");
        var coloured = Panel(Guid.NewGuid(), "Renk: Siyah | Beden: STD");
        var material = new Dictionary<string, string> { ["Materyal"] = "Pamuk" };

        Assert.Equal([plain], MarketplaceJobProcessor.NarrowModelCodeCandidates([plain], x => x.OptionSignature, material, null));
        Assert.Equal([coloured], MarketplaceJobProcessor.NarrowModelCodeCandidates([coloured], x => x.OptionSignature, new Dictionary<string, string>(), null));
        // The remote names an option, the panel variant a colour: nothing proves they are the same variant.
        Assert.Empty(MarketplaceJobProcessor.NarrowModelCodeCandidates([coloured], x => x.OptionSignature, material, null));
    }

    // Y5, Hepsiburada import: a model kept as one card per colour gives the product of the listing's colour; a listing
    // whose colour no card holds gets none, even when only one card holds the model code.
    [Fact]
    public void HepsiburadaModelCodeFindsTheCardOfTheListingsColour()
    {
        var navyCard = Guid.NewGuid();
        var blackCard = Guid.NewGuid();
        IReadOnlyList<MarketplaceJobProcessor.ModelCodeVariant> model =
        [
            Panel(navyCard, "Renk: Lacivert | Beden: S"), Panel(navyCard, "Renk: Lacivert | Beden: M"),
            Panel(blackCard, "Renk: Siyah | Beden: S"), Panel(blackCard, "Renk: Siyah | Beden: M")
        ];

        var black = MarketplaceJobProcessor.ModelCodeProducts(model, [Remote("1", "MZ100", new() { ["Renk"] = "Siyah", ["Beden"] = "S" }), Remote("2", "MZ100", new() { ["Renk"] = "Siyah", ["Beden"] = "M" })], null);
        Assert.Equal([blackCard], black);
        Assert.Empty(MarketplaceJobProcessor.ModelCodeProducts([model[0], model[1]], [Remote("3", "MZ100", new() { ["Renk"] = "Bordo", ["Beden"] = "S" })], null));
        // Variants of two colours in one listing name two cards: the caller links neither.
        Assert.Equal(2, MarketplaceJobProcessor.ModelCodeProducts(model, [Remote("4", "MZ100", new() { ["Renk"] = "Siyah", ["Beden"] = "S" }), Remote("5", "MZ100", new() { ["Renk"] = "Lacivert", ["Beden"] = "S" })], null).Count);
    }

    // T2-DO1: the one allocation of a package line is the package's latest event's where older rows remain.
    [Fact]
    public void AllocationsByLineKeepTheLatestEventOfEachPackageLine()
    {
        var package = Guid.NewGuid();
        var line = Guid.NewGuid();
        var other = Guid.NewGuid();
        PackageLineAllocation Row(Guid packageId, string eventId, decimal shipped) => new() { Id = Guid.CreateVersion7(), PackageId = packageId, OrderLineId = line, SourceEventId = eventId, AllocatedQuantity = 1, ShippedQuantity = shipped };

        var byLine = MarketplaceJobProcessor.AllocationsByLine([Row(package, "4302382330:1793743762226", 0), Row(package, "4302382330:1793876113007", 1), Row(package, "4302382330:1793791774541", 1), Row(package, "seed", 0), Row(other, "9:1793743762226", 0)]);

        Assert.Equal(2, byLine.Count);
        Assert.Equal("4302382330:1793876113007", byLine.Values.Single(x => x.PackageId == package).SourceEventId);
        Assert.Equal(1793876113007, MarketplaceJobProcessor.PackageEventInstant("4302382330:1793876113007"));
        Assert.Equal(1789229541000, MarketplaceJobProcessor.PackageEventInstant("order:PTT-1C2OSLF2R-120926:1789229541000"));
        Assert.Equal(long.MinValue, MarketplaceJobProcessor.PackageEventInstant("seed"));
    }

    // The Hepsiburada open-return scan (RETURN_LIFECYCLE) is not queued while no claim is open: live since 1 Nov 2026
    // 16:13Z all six Hepsiburada claims are Refunded, so the scan has nothing to follow and the panel shows it held.
    [Fact]
    public void HepsiburadaOpenReturnScanWaitsWhileEveryClaimIsSettled()
    {
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        ReturnClaim Claim(ReturnClaimStatus status, string raw) => new() { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, OrderId = Guid.NewGuid(), ExternalClaimId = Guid.NewGuid().ToString("N"), Status = status, RawStatus = raw };
        var settled = Enumerable.Range(0, 6).Select(_ => Claim(ReturnClaimStatus.Completed, "Refunded")).ToList();

        Assert.Empty(MarketplaceJobProcessor.ReturnLifecycleCandidates(settled.AsQueryable(), tenantId, connectionId, "HEPSIBURADA"));
        foreach (var open in MarketplaceJobProcessor.HepsiburadaOpenClaimStatuses)
            Assert.Single(MarketplaceJobProcessor.ReturnLifecycleCandidates(settled.Append(Claim(ReturnClaimStatus.ActionRequired, open)).AsQueryable(), tenantId, connectionId, "HEPSIBURADA"));
    }

    // O8: the packages a split child names as its origin, from the read and from the order's stored packages.
    [Fact]
    public void SplitParentsAreThePackagesAChildNamesAsItsOrigin()
    {
        var parents = MarketplaceJobProcessor.SplitParentIds([("4085035430", "4084999185"), ("4084999185", null), ("P-1", "P-1"), ("4111016887", " "), ("C-2", "4110933059")]);

        Assert.Equal(["4084999185", "4110933059"], parents.Order(StringComparer.Ordinal));
    }

    // T2-O8: a refused order is read again four times at most, 30 minutes, 2, 8 and 24 hours apart, three per run.
    [Fact]
    public void RefusedOrdersAreReadAgainAFewTimesFarApart()
    {
        var entries = RejectedOrderRereadPolicy.Note([], ["11515777689", "11515777689", "11547099440"], Now);
        Assert.Equal(2, entries.Count);
        Assert.Empty(RejectedOrderRereadPolicy.Due(entries, Now.AddMinutes(29)));
        var due = RejectedOrderRereadPolicy.Due(entries, Now.AddMinutes(30));
        Assert.Equal(2, due.Count);

        // Refused again by a stream read: its tries do not start over.
        var first = due[0];
        entries = RejectedOrderRereadPolicy.AfterRead(entries, first, stillRefused: true, Now.AddMinutes(30));
        entries = RejectedOrderRereadPolicy.Note(entries, [first.OrderId], Now.AddMinutes(31));
        var again = entries.Single(x => x.OrderId == first.OrderId);
        Assert.Equal((1, Now.AddMinutes(30) + TimeSpan.FromHours(2)), (again.Attempts, again.NextAt));

        // An order that lands leaves the list; one refused at its last try leaves it too.
        entries = RejectedOrderRereadPolicy.AfterRead(entries, due[1], stillRefused: false, Now.AddMinutes(30));
        Assert.DoesNotContain(entries, x => x.OrderId == due[1].OrderId);
        Assert.Empty(RejectedOrderRereadPolicy.AfterRead(entries, again with { Attempts = 3 }, stillRefused: true, Now));

        var saved = RejectedOrderRereadPolicy.Serialize(entries);
        Assert.Equal(entries, RejectedOrderRereadPolicy.Parse(saved));
        Assert.Null(RejectedOrderRereadPolicy.Serialize([]));
        Assert.Empty(RejectedOrderRereadPolicy.Parse("not json"));
    }

    [Fact]
    public void RefusedOrderListStaysWithinTheCursorAndThreePerRun()
    {
        var entries = RejectedOrderRereadPolicy.Note([], Enumerable.Range(0, 60).Select(i => $"1160000{i:D4}"), Now);

        Assert.Equal(RejectedOrderRereadPolicy.MaxTracked, entries.Count);
        Assert.True(RejectedOrderRereadPolicy.Serialize(entries)!.Length < 4096);
        Assert.Equal(RejectedOrderRereadPolicy.MaxPerRun, RejectedOrderRereadPolicy.Due(entries, Now.AddDays(1)).Count);
    }
}
