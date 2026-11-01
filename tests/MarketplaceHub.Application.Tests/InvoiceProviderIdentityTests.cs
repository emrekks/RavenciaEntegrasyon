using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Item 20 (1 Nov 2026): the panel chooses the invoice UUID before sending, so a lost answer is read, never resent.
public sealed class InvoiceProviderIdentityTests
{
    private const string Canonical = """
    {
      "Id":"invoice-uuid-check",
      "InvoiceUuid":"__UUID__",
      "InvoiceType":"EARSIVFATURA",
      "Currency":"TRY",
      "Note":"Satış faturası",
      "IssuedAt":"2026-11-01T10:00:00+00:00",
      "Order":{
        "OrderNumber":"4764437257",
        "OrderedAt":"2026-10-29T10:00:00+00:00",
        "CustomerSnapshotJson":"{\"customerFirstName\":\"Ali\",\"customerLastName\":\"Veli\"}",
        "InvoiceAddressSnapshotJson":"{\"fullAddress\":\"Test adres\",\"city\":\"İstanbul\",\"district\":\"Şişli\"}"
      },
      "Package":{"CargoProviderExternalId":"YKMP","StatusOccurredAt":"2026-10-30T10:00:00+00:00"},
      "Lines":[{"DescriptionSnapshot":"Ürün","UnitSnapshot":"ADET","Quantity":1,"UnitPrice":100,"LineTotal":120,"VatAmount":20,"VatRate":20,"DiscountAmount":0}]
    }
    """;

    [Fact]
    public void Every_attempt_of_an_invoice_carries_the_same_version_4_uuid()
    {
        var invoiceId = Guid.Parse("01a18813-b314-7e7f-9737-e36c7233dfe4");

        var first = InvoiceProviderIdentityPolicy.UuidFor(invoiceId);
        var second = InvoiceProviderIdentityPolicy.UuidFor(invoiceId);

        Assert.Equal(first, second);
        Assert.Equal('4', first.ToString("D")[14]);
        Assert.Contains(first.ToString("D")[19], "89ab");
        Assert.NotEqual(first, InvoiceProviderIdentityPolicy.UuidFor(Guid.CreateVersion7()));
    }

    [Fact]
    public void The_chosen_uuid_is_sent_in_invoice_info_before_anything_else_happens()
    {
        var uuid = InvoiceProviderIdentityPolicy.UuidFor(Guid.CreateVersion7()).ToString("D");

        using var payload = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), Canonical.Replace("__UUID__", uuid)));

        Assert.Equal(uuid, payload.RootElement.GetProperty("invoiceInfo").GetProperty("invoiceUuid").GetString());
        Assert.Equal("EARSIVFATURA", payload.RootElement.GetProperty("invoiceInfo").GetProperty("invoiceType").GetString());
    }

    [Fact]
    public void A_payload_without_a_uuid_leaves_the_field_out_and_a_malformed_one_is_refused()
    {
        using var withoutUuid = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), Canonical.Replace("\"InvoiceUuid\":\"__UUID__\",", string.Empty)));
        Assert.False(withoutUuid.RootElement.GetProperty("invoiceInfo").TryGetProperty("invoiceUuid", out _));

        Assert.ThrowsAny<JsonException>(() => TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), Canonical.Replace("__UUID__", "not-a-uuid")));
    }

    [Theory]
    [InlineData("0b6ae2d5-97c6-4597-a7a8-f356bcbad9ad", "0b6ae2d5-97c6-4597-a7a8-f356bcbad9ad")]
    // An attempt sent before the UUID was chosen carries none: nothing is read, the operator checks the portal.
    [InlineData(null, null)]
    [InlineData("remote-123", null)]
    public void Only_an_attempt_that_sent_a_uuid_is_read_by_it(string? attemptReference, string? expected) =>
        Assert.Equal(expected, InvoiceProviderIdentityPolicy.UuidToRead(attemptReference));

    [Fact]
    public void Not_found_counts_only_once_the_provider_had_time_to_create_the_invoice()
    {
        var sentAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.False(InvoiceProviderIdentityPolicy.NotFoundIsSettled(sentAt, sentAt.AddMinutes(1)));
        Assert.True(InvoiceProviderIdentityPolicy.NotFoundIsSettled(sentAt, sentAt.Add(InvoiceProviderIdentityPolicy.NotFoundSettleDelay)));
    }

    [Theory]
    [InlineData("https://api.trendyol.com/etransformation/gateway/earchive-not-found", TrendyolEFaturamProblemDetails.EArchiveNotFoundReference)]
    [InlineData("https://api.trendyol.com/etransformation/gateway/outgoing-einvoice-not-found", TrendyolEFaturamProblemDetails.OutgoingEInvoiceNotFoundReference)]
    [InlineData("/etransformation/gateway/earchive-not-found", TrendyolEFaturamProblemDetails.EArchiveNotFoundReference)]
    public void The_documented_not_found_problems_are_recognised(string problemType, string expected)
    {
        var reference = TrendyolEFaturamProblemDetails.Normalize(problemType);

        Assert.Equal(expected, reference);
        Assert.True(TrendyolEFaturamProblemDetails.IsInvoiceNotFound(reference));
    }

    [Theory]
    [InlineData("https://example.com/etransformation/gateway/earchive-not-found")]
    [InlineData("https://api.trendyol.com/etransformation/gateway/corporate-not-found")]
    [InlineData(null)]
    public void Any_other_404_is_not_proof_that_the_invoice_is_missing(string? problemType) =>
        Assert.False(TrendyolEFaturamProblemDetails.IsInvoiceNotFound(TrendyolEFaturamProblemDetails.Normalize(problemType)));

    [Fact]
    public void The_due_scan_runs_once_per_half_hour()
    {
        var start = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(1800, ScheduledJobProducer.InvoiceDueScanIntervalSeconds);
        Assert.Equal(ScheduledJobProducer.InvoiceDueScanBucket(start), ScheduledJobProducer.InvoiceDueScanBucket(start.AddMinutes(29)));
        Assert.NotEqual(ScheduledJobProducer.InvoiceDueScanBucket(start), ScheduledJobProducer.InvoiceDueScanBucket(start.AddMinutes(30)));
    }

    [Fact]
    public void The_shopify_invoice_scan_runs_every_six_hours_and_stays_a_known_default()
    {
        var cadence = ScheduledOrderPollingCadencePolicy.ForPlatform("SHOPIFY", "ORDER_INVOICE_RECONCILIATION", 900, 30);

        Assert.Equal(21_600, cadence.IntervalSeconds);
        Assert.True(ScheduledJobProducer.IsKnownDefault(new MarketplaceHub.Domain.ConnectionSyncPolicy
        {
            ResourceType = "ORDER_INVOICE_RECONCILIATION",
            IntervalSeconds = 21_600,
            OverlapSeconds = 0,
            JitterSeconds = 30
        }, "SHOPIFY"));
        // Other marketplaces keep their own cadence.
        Assert.Equal(900, ScheduledOrderPollingCadencePolicy.ForPlatform("TRENDYOL", "ORDER_INVOICE_RECONCILIATION", 900, 30).IntervalSeconds);
    }
}
