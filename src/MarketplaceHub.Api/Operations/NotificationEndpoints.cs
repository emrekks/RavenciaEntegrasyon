using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Operations;

// The notification center (İşlem Takibi › Bildirimler). The bell sends every notification it shows (POST), marks them
// read and removes them from itself (dismiss); the center lists them for a week: a person's own notifications and the
// tenant's shared notices. Reads are open to every signed-in role, writes to every role that works in the panel.
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/notifications");
        // { items, nextCursor, hasMore }, newest first. categories and severities are comma lists; state is UNREAD or READ.
        api.MapGet("", async (HttpContext http, IPanelNotificationService service, int? limit, string? after, string? categories, string? severities,
            DateTimeOffset? from, DateTimeOffset? to, string? search, string? state) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var result = await service.PageAsync(tenant.TenantId, tenant.UserId, new PanelNotificationQuery(limit, after, List(categories), List(severities), from, to, search, state), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(http, result.Error!);
        });
        api.MapGet("/summary", async (HttpContext http, IPanelNotificationService service) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.SummaryAsync(tenant.TenantId, tenant.UserId, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("", async (HttpContext http, IPanelNotificationService service, SaveRequest request) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var items = (request.Items ?? []).Select(item => new PanelNotificationInput(item.ClientKey ?? string.Empty, item.Severity, item.Category, item.Title, item.Message ?? string.Empty,
                item.Href, item.ActionLabel, item.PlatformCode, item.StoreName, item.Count,
                item.Payload is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } payload ? payload.GetRawText() : null,
                item.CreatedAt, item.Shared, item.Read)).ToList();
            var result = await service.SaveAsync(tenant.TenantId, tenant.UserId, items, http.RequestAborted);
            return result.Succeeded ? Results.Ok(new { saved = result.Value }) : Problem(http, result.Error!);
        });
        api.MapPost("/read", async (HttpContext http, IPanelNotificationService service, PanelNotificationSelection selection) =>
            Tenant(http) is { } tenant ? Results.Ok(new { updated = await service.MarkReadAsync(tenant.TenantId, tenant.UserId, selection, http.RequestAborted) }) : Unauthorized(http));
        api.MapPost("/dismiss", async (HttpContext http, IPanelNotificationService service, PanelNotificationSelection selection) =>
            Tenant(http) is { } tenant ? Results.Ok(new { updated = await service.DismissAsync(tenant.TenantId, tenant.UserId, selection, http.RequestAborted) }) : Unauthorized(http));
        return endpoints;
    }

    public sealed record SaveRequest(IReadOnlyList<SaveItem>? Items);

    public sealed record SaveItem(string? ClientKey, string? Severity, string? Category, string? Title, string? Message, string? Href, string? ActionLabel,
        string? PlatformCode, string? StoreName, int? Count, JsonElement? Payload, DateTimeOffset? CreatedAt, bool Shared, bool Read);

    private static string[]? List(string? value) => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => MarketplaceHub.Api.Security.ApiProblems.Of(http, error);
}
