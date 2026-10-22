using System.Xml.Linq;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Code lookup. BarkodKontrol(Barkod) answers the product a code belongs to (StokKontrolDetay; the service schema and
// the documentation name the parameter Barkod). GetProductsWithVariants lists a model's variants without their
// codes, so the product mapping asks PTT AVM about the panel's own barcode and stock code instead of guessing a
// variant from its name: a code PTT AVM answers under the model's product id is the code it keeps the variant by.
// An unknown code answers nil or a request fault; both read as "not found".
public sealed partial class PttAvmHttpClient : IProductVisualLookupPort
{
    internal const string CodeCheck = "BarkodKontrol";
    private static int codeCheckShapeLogged;

    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var code = barcode.Trim();
        if (code.Length == 0 || code.Length > PttAvmPriceInventoryRules.MaxBarcodeLength) return AdapterResult<RemoteProduct?>.Success(null);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteProduct?>(load);
        var result = await SendSoapAsync(load.Account, CodeCheck, [new("Barkod", code)], cancellationToken);
        if (!result.IsSuccess)
            return result.Error!.Code == "PTTAVM_REQUEST_REJECTED"
                ? AdapterResult<RemoteProduct?>.Success(null, result.RateLimit)
                : AdapterResult<RemoteProduct?>.Failure(result.Error!, result.RateLimit);
        // The element names of the first answer show the live shape (never its values).
        if (Interlocked.Exchange(ref codeCheckShapeLogged, 1) == 0)
            logger.LogInformation("PTT AVM code check answered. ConnectionId: {ConnectionId}, Shape: {Shape}", load.Account.Connection.Id, ElementShape(result.Value));
        return AdapterResult<RemoteProduct?>.Success(CodeCheckProduct(code, result.Value!), result.RateLimit);
    }

    // The product a BarkodKontrol answer names: its fields under the result element, or under a StokKontrolDetay in it.
    internal static RemoteProduct? CodeCheckProduct(string code, XElement result)
    {
        if (IsNil(result)) return null;
        var row = result.Elements().Any(x => x.Name.LocalName == "UrunId") ? result : result.Descendants().FirstOrDefault(x => x.Name.LocalName == "StokKontrolDetay");
        var productId = ContractValue(row, "UrunId");
        if (row is null || productId is null || productId == "0") return null;
        return new(productId, code, ContractValue(row, "Barkod"), ContractValue(row, "UrunKodu"), PttAvmProductMapper.Snapshot(row).ToJsonString());
    }
}
