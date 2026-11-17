using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient(
    IHttpClientFactory clients,
    HepsiburadaAuthenticationHandler authentication,
    IOptions<HepsiburadaOptions> options,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<HepsiburadaHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IOrderPackageReadPort, IReturnPort, IInvoiceMarketplacePort, IHepsiburadaInvoiceStatusPort
{
    private readonly HepsiburadaOptions settings = options.Value;
    private bool GlobalWritesEnabled => ExternalWriteGate.GlobalWritesEnabled(configuration);
    // The first listed property matching any name (ignoring case) with a string or number (RemoteFields).
    private static string? Text(JsonElement element, params string[] names) => RemoteFields.FirstListedTextIgnoringCase(element, names);

    // Hepsiburada's documented page limits: paid orders 100, the status and missing-invoice lists 50,
    // the package lists 10 (the default for everything else).
    private static (int Offset, int Limit) Page(AdapterPageRequest page, int configuredLimit, int documentedMaximum = 10)
    {
        var offset = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : 0;
        return (offset, Math.Clamp(page.Limit, 1, Math.Max(Math.Clamp(configuredLimit, 1, 10), documentedMaximum)));
    }

    private static bool ConnectionWritesEnabled(string settingsJson) => ExternalWriteGate.ConnectionWritesEnabled(settingsJson);

    private static Task<AdapterResult<T>> Unsupported<T>(string message) => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_CAPABILITY_NOT_ENABLED", message, (int)HttpStatusCode.NotImplemented, null, null)));
    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) => AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
