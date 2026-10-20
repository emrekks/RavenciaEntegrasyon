using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Every date and time sent to E-Faturam is Istanbul time.
public sealed class EfaturamIstanbulTimeTests
{
    [Fact]
    public void Order_placed_at_01_30_istanbul_carries_the_istanbul_day_and_the_issue_time_carries_the_istanbul_clock()
    {
        // 01:30 on 20 Oct in Istanbul is 22:30Z on 19 Oct; the parcel left at 00:15 Istanbul (21:15Z the day before).
        // The stored instants are UTC, exactly as InvoicingJobProcessor serializes them.
        const string canonical = """
        {
          "Id":"invoice-istanbul-day",
          "InvoiceType":"EARSIVFATURA",
          "Currency":"TRY",
          "Note":"Satış faturası",
          "IssuedAt":"2026-10-19T22:45:10.123+00:00",
          "Order":{
            "OrderNumber":"4227866665",
            "OrderedAt":"2026-10-19T22:30:00+00:00",
            "CustomerSnapshotJson":"{\"customerFirstName\":\"Ali\",\"customerLastName\":\"Veli\"}",
            "InvoiceAddressSnapshotJson":"{\"fullAddress\":\"Test adres\",\"city\":\"İstanbul\",\"district\":\"Şişli\"}"
          },
          "Package":{"CargoProviderExternalId":"YKMP","StatusOccurredAt":"2026-10-19T21:15:00+00:00"},
          "Lines":[{"DescriptionSnapshot":"Ürün","UnitSnapshot":"ADET","Quantity":1,"UnitPrice":100,"LineTotal":120,"VatAmount":20,"VatRate":20,"DiscountAmount":0}]
        }
        """;

        using var payload = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), canonical));
        var root = payload.RootElement;

        Assert.Equal("2026-10-20", root.GetProperty("orderInfo").GetProperty("orderDate").GetString());
        Assert.Equal("2026-10-20", root.GetProperty("deliveryInfo").GetProperty("sentAt").GetString());
        // The gateway accepts only a trailing Z and prints the digits as written: they are the Istanbul clock.
        Assert.Equal("2026-10-20T01:45:10.123Z", root.GetProperty("issuedAt").GetString());
        Assert.Equal("2026-10-20T01:30:00.000Z", root.GetProperty("paymentInfo").GetProperty("paymentDate").GetString());
    }

    [Theory]
    [InlineData("2026-10-19T20:59:59Z", "2026-10-19")]
    [InlineData("2026-10-19T21:00:00Z", "2026-10-20")]
    [InlineData("2026-10-20T01:30:00+03:00", "2026-10-20")]
    public void Istanbul_day_turns_at_21_00_utc(string instant, string expectedDay) =>
        Assert.Equal(DateOnly.Parse(expectedDay), EfaturamTurkeyTime.Day(DateTimeOffset.Parse(instant)));

    [Fact]
    public void Issue_time_is_the_moment_of_submission_until_the_provider_holds_the_invoice()
    {
        var draftedAt = DateTimeOffset.Parse("2026-10-18T08:00:00Z");
        var submittedAt = DateTimeOffset.Parse("2026-10-19T22:45:10Z");
        var invoice = NewInvoice(draftedAt);

        InvoicingJobProcessor.StampIssueTime(invoice, submittedAt);

        Assert.Equal(submittedAt, invoice.IssuedAt);
        Assert.Equal("2026-10-20T01:45:10.000Z", EfaturamTurkeyTime.ProviderDateTime(invoice.IssuedAt!.Value));

        var issued = NewInvoice(draftedAt);
        issued.ExternalReference = "remote-1";
        InvoicingJobProcessor.StampIssueTime(issued, submittedAt);
        Assert.Equal(draftedAt, issued.IssuedAt);
    }

    private static Invoice NewInvoice(DateTimeOffset issuedAt) => new()
    {
        Status = InvoiceStatus.Submitting,
        IssuedAt = issuedAt,
        InvoiceType = "EARSIVFATURA",
        SequencePurpose = "SALE",
        Currency = "TRY",
        Note = string.Empty,
        IdempotencyKey = "istanbul-issue-time"
    };
}
