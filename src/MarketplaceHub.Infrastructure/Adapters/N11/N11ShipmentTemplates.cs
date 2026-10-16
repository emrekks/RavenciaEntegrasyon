using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Cargo templates defined on the seller's n11 panel: SOAP ShipmentService/GetShipmentTemplateList
// (https://api.n11.com/ws/ShipmentService.wsdl). Read only; the panel offers the names as choices
// for the product create setting, which must match a template name exactly.
public sealed partial class N11HttpClient
{
    private const string ShipmentService = "ws/shipmentService/";

    public async Task<AdapterResult<IReadOnlyList<string>>> ShipmentTemplatesAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(tenantId, connectionId, cancellationToken);
        if (account is null) return CredentialMissing<IReadOnlyList<string>>();
        var listed = await SendSoapAsync(account, ShipmentService, "GetShipmentTemplateList", "", cancellationToken);
        if (!listed.IsSuccess) return AdapterResult<IReadOnlyList<string>>.Failure(listed.Error!, listed.RateLimit);
        IReadOnlyList<string> names = listed.Value!.Descendants().Where(x => x.Name.LocalName == "templateName")
            .Select(x => x.Value.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), false))
            .ToList();
        return AdapterResult<IReadOnlyList<string>>.Success(names);
    }
}
