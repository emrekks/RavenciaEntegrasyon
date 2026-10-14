using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceFiscalDeliveryPackagePolicyTests
{
    [Fact]
    public void Accepts_a_frozen_valid_package_for_order_level_e_archive_invoice()
    {
        var package = new InvoiceFiscalDeliveryPackageSnapshot(
            "package-123",
            "HepsiJet",
            new DateTimeOffset(2026, 10, 7, 13, 3, 1, TimeSpan.FromHours(3)));

        Assert.True(InvoicingBillingService.IsValidFiscalDeliveryPackage(package));
    }

    [Theory]
    [InlineData("unknown carrier", true)]
    [InlineData(null, true)]
    [InlineData("HepsiJet", false)]
    public void Rejects_incomplete_or_unrecognized_fiscal_delivery_package(
        string? carrier,
        bool hasSentAt)
    {
        var package = new InvoiceFiscalDeliveryPackageSnapshot(
            "package-123",
            carrier,
            hasSentAt ? new DateTimeOffset(2026, 10, 7, 13, 3, 1, TimeSpan.FromHours(3)) : null);

        Assert.False(InvoicingBillingService.IsValidFiscalDeliveryPackage(package));
    }

    [Fact]
    public void Reads_frozen_fiscal_delivery_package_from_receiver_snapshot()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "FiscalDeliveryPackage": {
            "ExternalPackageId": "package-123",
            "CargoProviderExternalId": "HepsiJet",
            "StatusOccurredAt": "2026-10-07T13:03:01+03:00"
          }
        }
        """);

        var package = InvoicingBillingService.ReadFiscalDeliveryPackage(document.RootElement);

        Assert.True(InvoicingBillingService.IsValidFiscalDeliveryPackage(package));
        Assert.Equal("package-123", package!.ExternalPackageId);
    }
}
