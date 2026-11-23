namespace MarketplaceHub.Domain;

// A re-shipment the owner adds by hand (owner, 23 Nov 2026: "Platform seçtirsin ve sipariş no yazdırsın"): an order of
// the panel that must be shipped again although no marketplace says so (a resend Trendyol never returns, an exchange a
// marketplace names nowhere). It lists the order under Siparişler › Yeniden Gönderimler marked "Elle Eklendi" until the
// owner marks it handed to the carrier ("Kargoya Verildi Olarak İşaretle"; ShippedAt). Panel only: nothing is sent to a
// marketplace and neither stock nor invoices change. One record per order (sales.manual_resends); it goes with its order.
public sealed class ManualResend
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    // When the owner marked the re-shipment handed to the carrier; null while it waits.
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}
