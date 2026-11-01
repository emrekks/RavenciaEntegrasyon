using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// The order gross of a Hepsiburada order leaves cancelled quantities out; the discount a read reports
// for its lines loses the cancelled lines' share in the same proportion, so the net is not understated.
internal static class HepsiburadaOrderDiscountPolicy
{
    public static decimal RemainingDiscount(
        decimal readDiscount,
        IEnumerable<(decimal UnitPrice, decimal OrderedQuantity, decimal CancelledQuantity)> readLines)
    {
        if (readDiscount <= 0m) return readDiscount;
        var lines = readLines.ToArray();
        var orderedGross = lines.Sum(line => line.UnitPrice * Math.Max(0m, line.OrderedQuantity));
        // Without a known line value the discount cannot be apportioned and stays as reported.
        if (orderedGross <= 0m) return readDiscount;
        var remainingGross = lines.Sum(line => line.UnitPrice * OrderLinePresentationPolicy.ActiveQuantity(line.OrderedQuantity, line.CancelledQuantity));
        if (remainingGross >= orderedGross) return readDiscount;
        if (remainingGross <= 0m) return 0m;
        return Math.Round(readDiscount * remainingGross / orderedGross, 2, MidpointRounding.AwayFromZero);
    }
}
