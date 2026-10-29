using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;
using Draft = MarketplaceHub.Infrastructure.Adapters.PttAvm.PttAvmProductPayloadRules.ProductDraft;
using VariantDraft = MarketplaceHub.Infrastructure.Adapters.PttAvm.PttAvmProductPayloadRules.VariantDraft;

namespace MarketplaceHub.Application.Tests;

// A PTT AVM model family is sent once: the colour cards' jobs each carry the whole family, and a card whose family a
// sibling's job already sent follows that send (live 2026-10-28: eight cards, one send accepted, seven refused as
// repeats and shown as blocked).
public sealed class PttAvmFamilySendTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 13, 50, 0, TimeSpan.Zero);

    private static readonly VariantDraft[] Family =
    [
        new("mzdb003lacs", "Lacivert", "S", 2), new("mzdb003lacm", "Lacivert", "M", 2),
        new("mzdb003yesls", "Yeşil", "S", 2), new("mzdb003yeslm", "Yeşil", "M", 2)
    ];

    private static string Payload(string parent = "MZDB003", params VariantDraft[] variants) =>
        PttAvmProductPayloadRules.Payload(new Draft(parent, "Kazak", "Yün kazak", "1632", parent, 499.90m, 20m, variants.Sum(x => x.Quantity), 2m, ["https://cdn.example.com/a.jpg"], variants));

    private static ProductPublicationJobPayload Sent(string payloadJson, TimeSpan ago, string phase = "POLL", string? operation = "t:trk-046c") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), phase, "hash", payloadJson, phase == "POLL" ? operation : null, phase == "POLL" ? Now - ago : null);

    [Fact]
    public void ACardWhoseFamilyASiblingSentFollowsThatSend()
    {
        var sibling = Sent(Payload(variants: Family), TimeSpan.FromSeconds(20));

        var covering = PttAvmFamilySend.Covering(Payload(variants: Family), [sibling], Now);

        Assert.Same(sibling, covering);
        Assert.Equal("t:trk-046c", covering!.ExternalOperationId);
    }

    [Fact]
    public void TheParentBarcodeAndVariantBarcodesMatchWithoutRegardToCase()
    {
        var sibling = Sent(Payload("mzdb003", [.. Family.Select(x => x with { Barcode = x.Barcode.ToUpperInvariant() })]), TimeSpan.FromMinutes(1));

        Assert.Same(sibling, PttAvmFamilySend.Covering(Payload(variants: Family), [sibling], Now));
    }

    [Fact]
    public void ASendThatLacksOneOfThisCardsVariantsDoesNotServeIt()
    {
        // A variant added to the family after the sibling went out still has to be sent.
        var sibling = Sent(Payload(variants: Family[..3]), TimeSpan.FromMinutes(1));

        Assert.Null(PttAvmFamilySend.Covering(Payload(variants: Family), [sibling], Now));
        // A send carrying more than this card knows (a newer family) serves it.
        Assert.NotNull(PttAvmFamilySend.Covering(Payload(variants: Family[..3]), [Sent(Payload(variants: Family), TimeSpan.FromMinutes(1))], Now));
    }

    [Fact]
    public void AnotherFamilyANotYetSentJobOrAnExpiredSendDoesNotServe()
    {
        var own = Payload(variants: Family);

        Assert.Null(PttAvmFamilySend.Covering(own, [Sent(Payload("MZDB004", Family), TimeSpan.FromMinutes(1))], Now));
        Assert.Null(PttAvmFamilySend.Covering(own, [Sent(Payload(variants: Family), TimeSpan.Zero, phase: "SUBMIT")], Now));
        Assert.Null(PttAvmFamilySend.Covering(own, [Sent(Payload(variants: Family), TimeSpan.FromMinutes(1), operation: "op-not-tracked")], Now));
        Assert.Null(PttAvmFamilySend.Covering(own, [Sent(Payload(variants: Family), TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1))], Now));
        // A product without variants is never a family send.
        Assert.Null(PttAvmFamilySend.Covering(PttAvmProductPayloadRules.Payload(new Draft("8690000000017", "Gömlek", "Pamuklu", "1632", "GML-1", 250m, 20m, 1, 1m, ["https://cdn.example.com/a.jpg"])),
            [Sent(Payload(variants: Family), TimeSpan.FromMinutes(1))], Now));
    }

    [Fact]
    public void ASendOfAnEarlierRoundDoesNotAnswerForAResend()
    {
        // The jobs of one Yayınla are queued seconds apart; a resend asked for later must go out itself instead of
        // following the earlier send PTT AVM did not apply.
        var resendQueuedAt = Now;

        Assert.Equal(Now - TimeSpan.FromMinutes(5), PttAvmFamilySend.RoundStart(resendQueuedAt));
        Assert.True(Now - TimeSpan.FromSeconds(3) >= PttAvmFamilySend.RoundStart(resendQueuedAt));
        Assert.False(Now - TimeSpan.FromMinutes(40) >= PttAvmFamilySend.RoundStart(resendQueuedAt));
    }

    [Fact]
    public void TheLatestCoveringSendWins()
    {
        var older = Sent(Payload(variants: Family), TimeSpan.FromHours(2), operation: "t:old");
        var newer = Sent(Payload(variants: Family), TimeSpan.FromMinutes(3), operation: "t:new");

        Assert.Equal("t:new", PttAvmFamilySend.Covering(Payload(variants: Family), [older, newer], Now)!.ExternalOperationId);
    }

    private static (string PayloadJson, bool Succeeded, DateTimeOffset CreatedAt) Job(ProductPublicationJobPayload payload, bool succeeded = true) =>
        (System.Text.Json.JsonSerializer.Serialize(payload), succeeded, payload.SubmittedAt ?? Now);

    // Live 2026-10-29: a bulk publication of MZ053TCD queued one card of eight; its request carried every variant and
    // finished, the other seven cards kept no state. A card the family's finished send carried is taken into it.
    [Fact]
    public void ACardTheFamilysFinishedSendCarriedIsTakenIntoIt()
    {
        var send = Sent(Payload(variants: Family), TimeSpan.FromHours(3), operation: "t:6ae35b6f");

        var carrying = PttAvmFamilySend.Carrying(["MZDB003YESLS", "mzdb003yeslm "], [Job(send)]);

        Assert.Equal("t:6ae35b6f", carrying!.ExternalOperationId);
        Assert.Equal("hash", carrying.PayloadHash);
    }

    [Fact]
    public void OnlyTheFamilysLatestFinishedSendCarryingEveryVariantOfTheCardTakesItIn()
    {
        var older = Sent(Payload(variants: Family), TimeSpan.FromHours(5), operation: "t:old");
        var newerWithoutTheCard = Sent(Payload(variants: Family[..2]), TimeSpan.FromHours(1), operation: "t:new");

        // The latest send left the card's variants out (PTT AVM deletes the variants a request leaves out): unknown.
        Assert.Null(PttAvmFamilySend.Carrying(["mzdb003yesls"], [Job(older), Job(newerWithoutTheCard)]));
        // One of the card's variants was not in the send.
        Assert.Null(PttAvmFamilySend.Carrying(["mzdb003yesls", "mzdb003yesll"], [Job(older)]));
        // The latest send did not finish (refused, manual review): unknown.
        Assert.Null(PttAvmFamilySend.Carrying(["mzdb003lacs"], [Job(older), Job(Sent(Payload(variants: Family), TimeSpan.FromMinutes(30), operation: "t:review"), succeeded: false)]));
        // A job that never reached PTT AVM is no send; the finished send before it answers.
        Assert.Equal("t:old", PttAvmFamilySend.Carrying(["mzdb003lacs"], [Job(older), Job(Sent(Payload(variants: Family), TimeSpan.Zero, phase: "SUBMIT"), succeeded: false)])!.ExternalOperationId);
        // A card without barcodes, or no send at all, is left as it is.
        Assert.Null(PttAvmFamilySend.Carrying(["mzdb003lacs", " "], [Job(older)]));
        Assert.Null(PttAvmFamilySend.Carrying(["mzdb003lacs"], []));
    }
}
