using MarketplaceHub.Api.Security;

namespace MarketplaceHub.Api.Common;

// Scan 23 Nov 2026: an Excel export reads a whole catalogue or job and writes the workbook in memory or a temporary file,
// and nothing limited how many ran at once. A tenant runs at most PerTenant exports at a time on this API process; one
// more is answered 429 EXPORT_BUSY ("Dışa Aktarım Sürüyor") at once instead of queueing behind them. A slot is held until
// the response has been sent (the file streamed), not only until the handler returns.
public sealed class ExportGate
{
    public const int PerTenant = 2;
    public const string BusyCode = "EXPORT_BUSY";
    public static readonly string BusyMessage = $"Dışa Aktarım Sürüyor: aynı anda en fazla {PerTenant} Excel dosyası hazırlanabilir; biri bitince tekrar deneyin.";

    private readonly object sync = new();
    private readonly Dictionary<Guid, int> running = [];

    /// <summary>A lease to dispose when the export ends; null while the tenant already runs PerTenant exports.</summary>
    public IDisposable? TryEnter(Guid tenantId)
    {
        lock (sync)
        {
            var count = running.GetValueOrDefault(tenantId);
            if (count >= PerTenant) return null;
            running[tenantId] = count + 1;
            return new Lease(this, tenantId);
        }
    }

    internal int Running(Guid tenantId)
    {
        lock (sync) return running.GetValueOrDefault(tenantId);
    }

    private void Leave(Guid tenantId)
    {
        lock (sync)
        {
            var left = running.GetValueOrDefault(tenantId) - 1;
            if (left <= 0) running.Remove(tenantId);
            else running[tenantId] = left;
        }
    }

    private sealed class Lease(ExportGate gate, Guid tenantId) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Leave(tenantId);
        }
    }
}

public static class ExportGateEndpointExtensions
{
    // The export endpoints take one of the tenant's export slots (ExportGate) for the whole request. A request without a
    // tenant goes on to the handler, which refuses it itself.
    public static RouteHandlerBuilder WithExportLimit(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (EndpointHelpers.Tenant(http) is not { } tenant) return await next(context);
            var lease = http.RequestServices.GetRequiredService<ExportGate>().TryEnter(tenant.TenantId);
            if (lease is null)
            {
                http.Response.Headers.RetryAfter = "5";
                return ApiProblems.Of(http, new(ExportGate.BusyCode, ExportGate.BusyMessage, StatusCodes.Status429TooManyRequests));
            }
            http.Response.RegisterForDispose(lease);
            try
            {
                return await next(context);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });
}
