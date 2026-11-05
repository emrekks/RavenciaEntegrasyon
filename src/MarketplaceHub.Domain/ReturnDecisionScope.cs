namespace MarketplaceHub.Domain;

// Report item 8: how much of a claim one decision covers is the marketplace's rule, and the screens take it from here.
// Hepsiburada accepts or rejects a claim as a whole (hepsiburada-api-son.md 25118-25157) and Shopify approves or declines
// a whole return (shopify-rehberler-son.md 18614): the panel locks the line choice there. Trendyol decides per claim item;
// n11 and Pazarama open one claim per product, so a line choice never splits one of their claims.
public static class ReturnDecisionScope
{
    public static bool IsWholeClaim(string? platformCode) =>
        string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase);
}

// "Kısmen Onaylandı" at order level (owner rule 5 Nov 2026): n11 and Pazarama put each product in a claim of its own, so an
// order whose return was approved for one product and rejected for another never has a claim decided both ways. Read
// over the order's claims, the order is approved in part when one of its claims is approved (or completed) and another
// rejected, or when one claim is itself approved in part. Claims still waiting or cancelled do not count either way.
public static class OrderReturnPartialApprovalPolicy
{
    public static bool IsPartiallyApproved(IEnumerable<(ReturnClaimStatus Status, IReadOnlyCollection<string?> LineStatuses)> claims)
    {
        var approved = false;
        var rejected = false;
        foreach (var (status, lineStatuses) in claims)
        {
            if (ReturnClaimPartialApprovalPolicy.IsPartiallyApproved(status, lineStatuses)) return true;
            approved |= status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed;
            rejected |= status == ReturnClaimStatus.Rejected;
        }
        return approved && rejected;
    }
}

// n11 ClaimReturnDeny (https://developer.n11.com/documentation/iade-entgrasyonu/iade-talepleri-servisi/): a denial needs
// the deny reason id (ClaimReturnDenyReasonTypes) and a note. Reason 41 ("İade Ürün Mağazaya Ulaşmadı") sends no return
// shipment: n11 uses the buyer's tracking number. Any other reason sends the product back to the buyer, and the panel
// only sends it as CUSTOM with the carrier (shipmentCompanyId) and the tracking number of that shipment. CAMPAIGN is
// never sent: n11 approves the return when the seller ships with the campaign code, the opposite of a denial.
public static class N11ReturnDenyRule
{
    public const string ProductNotReceivedReasonId = "41";
    public const string CustomShipment = "CUSTOM";

    public static bool NeedsReturnShipment(string? denyReasonId) =>
        !string.Equals(denyReasonId?.Trim(), ProductNotReceivedReasonId, StringComparison.Ordinal);

    // null when the denial can be sent, otherwise why not (Turkish, for the screen).
    public static string? Problem(string? denyReasonId, string? note, string? shipmentCompanyId, string? trackingNumber)
    {
        if (string.IsNullOrWhiteSpace(denyReasonId)) return "n11 ret nedeni seçilmelidir.";
        if (string.IsNullOrWhiteSpace(note)) return "n11 ret açıklaması yazılmalıdır.";
        if (!NeedsReturnShipment(denyReasonId))
            return string.IsNullOrWhiteSpace(shipmentCompanyId) && string.IsNullOrWhiteSpace(trackingNumber)
                ? null
                : "\"İade Ürün Mağazaya Ulaşmadı\" nedeniyle geri gönderim bilgisi gönderilmez; n11 alıcının takip numarasını kullanır.";
        if (string.IsNullOrWhiteSpace(shipmentCompanyId)) return "Ürünü alıcıya geri gönderdiğiniz kargo firması seçilmelidir.";
        if (string.IsNullOrWhiteSpace(trackingNumber)) return "Geri gönderimin kargo takip numarası yazılmalıdır.";
        return null;
    }
}
