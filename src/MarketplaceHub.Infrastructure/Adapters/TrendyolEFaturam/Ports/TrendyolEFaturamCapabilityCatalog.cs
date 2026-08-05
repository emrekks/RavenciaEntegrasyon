using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Ports;

public static class TrendyolEFaturamCapabilityCatalog
{
    public static IReadOnlyList<string> All { get; } = [F4Capabilities.ConnectionTest, F4Capabilities.InvoiceSubmit, F4Capabilities.InvoiceStatusRead, F4Capabilities.InvoiceDocumentRead, F4Capabilities.InvoiceCancel];
}
