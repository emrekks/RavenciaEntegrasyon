using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Alerts;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Settings;

// Settings → Alarm Bildirimleri (owner and administrator only; RoleAuthorizationMiddleware) and the host's sync.
//   GET  /api/v1/settings/alert-email        sender, recipients, whether an app password is saved (never the password)
//   PUT  /api/v1/settings/alert-email        save; a blank appPassword keeps the saved one; audit-logged
//   POST /api/v1/settings/alert-email/test   sends the test alert with the saved settings (Gmail SMTPS 465)
//   GET  /internal/alert-settings             host sync: the owner's settings as alert_smtp.txt lines; 204 when none
//   POST /internal/alert-settings/import      host sync: the host file becomes the settings when the panel has none
// The /internal routes answer only requests from the server's own networks (Caddy also returns 404 to public
// sources) that carry the host token from deploy/secrets/alert_sync_token.txt (Alerts:HostSyncToken); without a
// configured token they do not exist (404).
public static class AlertEmailEndpoints
{
    public const string HostTokenHeader = "X-Ravencia-Host-Token";

    public sealed record SaveRequest(string? SenderAddress, List<string?>? Recipients, string? AppPassword);

    public static IEndpointRouteBuilder MapAlertEmailEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/settings/alert-email");
        api.MapGet("", GetAsync);
        api.MapPut("", SaveAsync);
        api.MapPost("/test", TestAsync);
        endpoints.MapGet("/internal/alert-settings", HostReadAsync);
        endpoints.MapPost("/internal/alert-settings/import", HostImportAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext http, AlertEmailSettingsService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var view = await service.GetAsync(tenant.TenantId, http.RequestAborted);
        return Versioned(http, view);
    }

    private static async Task<IResult> SaveAsync(SaveRequest request, HttpContext http, AlertEmailSettingsService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (IfMatchVersion(http, out var expected) is { } invalidEtag) return invalidEtag;
        try
        {
            var result = await service.SaveAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, request.SenderAddress, request.Recipients, request.AppPassword, expected, http.RequestAborted);
            return result.Settings is { } saved
                ? Versioned(http, saved)
                : Problem(http, new("ALERT_EMAIL_SETTINGS_INVALID", result.Error ?? "Alarm e-postası ayarları geçersiz.", 400));
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException || SettingsEndpoints.IsUniqueViolation(exception))
        {
            return Problem(http, new("PRECONDITION_FAILED", "Ayarlar başka bir oturumda değişti; sayfayı yenileyip yeniden deneyin.", 412));
        }
    }

    private static async Task<IResult> TestAsync(HttpContext http, AlertEmailSettingsService service, IAlertEmailSender sender)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var credentials = await service.LoadCredentialsAsync(tenant.TenantId, http.RequestAborted);
        if (credentials is null) return Problem(http, new("ALERT_EMAIL_NOT_CONFIGURED", "Önce gönderen adresi, alıcıları ve uygulama şifresini kaydedin.", 409));
        try
        {
            await sender.SendAsync(credentials, AlertEmailRules.TestMessage, http.RequestAborted);
        }
        catch (AlertEmailException exception)
        {
            await service.RecordTestAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, exception.Message, CancellationToken.None);
            return Problem(http, new(exception.Code, exception.Message, 502));
        }
        await service.RecordTestAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, null, CancellationToken.None);
        return Results.Ok(new { sent = true, recipients = credentials.Recipients });
    }

    private static async Task<IResult> HostReadAsync(HttpContext http, AlertEmailSettingsService service, IConfiguration configuration)
    {
        if (!HostAllowed(http, configuration)) return Results.NotFound();
        var credentials = await service.LoadForHostAsync(http.RequestAborted);
        http.Response.Headers.CacheControl = "no-store";
        return credentials is null ? Results.NoContent() : Results.Text(AlertEmailRules.ToHostFile(credentials), "text/plain; charset=utf-8");
    }

    private static async Task<IResult> HostImportAsync(HttpContext http, AlertEmailSettingsService service, IConfiguration configuration)
    {
        if (!HostAllowed(http, configuration)) return Results.NotFound();
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var buffer = new char[4096];
        var read = await reader.ReadBlockAsync(buffer, http.RequestAborted);
        if (read >= buffer.Length) return Results.BadRequest();
        var credentials = AlertEmailRules.FromHostFile(new string(buffer, 0, read));
        if (credentials is null) return Results.UnprocessableEntity();
        return await service.ImportFromHostAsync(credentials, http.TraceIdentifier, http.RequestAborted) ? Results.Created() : Results.Conflict();
    }

    // Exposed for tests.
    internal static bool HostAllowed(HttpContext http, IConfiguration configuration)
    {
        var expected = configuration["Alerts:HostSyncToken"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32) return false;
        if (!FetchMetadata.IsFromServerNetwork(http)) return false;
        var presented = http.Request.Headers[HostTokenHeader].ToString();
        return presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected.Trim()));
    }

    private static IResult? IfMatchVersion(HttpContext http, out long? expected)
    {
        expected = null;
        var value = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length < 4 || !value.StartsWith("\"v", StringComparison.Ordinal) || !value.EndsWith('"') || !long.TryParse(value[2..^1], out var version) || version <= 0)
            return Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400));
        expected = version;
        return null;
    }

    private static IResult Versioned(HttpContext http, AlertEmailSettingsView view)
    {
        if (view.Version > 0) http.Response.Headers.ETag = $"\"v{view.Version}\"";
        return Results.Ok(new { senderAddress = view.SenderAddress, recipients = view.Recipients, appPasswordSaved = view.AppPasswordSaved, updatedAt = view.UpdatedAt, version = view.Version });
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => ApiProblems.Of(http, error);
}
