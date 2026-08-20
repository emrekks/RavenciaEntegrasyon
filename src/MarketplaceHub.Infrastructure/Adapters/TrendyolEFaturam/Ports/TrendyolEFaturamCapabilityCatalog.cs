using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Ports;

public static class TrendyolEFaturamCapabilityCatalog
{
    public static IReadOnlyList<string> All { get; } = [InvoicingCapabilities.ConnectionTest, InvoicingCapabilities.InvoiceSubmit, InvoicingCapabilities.InvoiceStatusRead, InvoicingCapabilities.InvoiceDocumentRead, InvoicingCapabilities.InvoiceCancel];
}
