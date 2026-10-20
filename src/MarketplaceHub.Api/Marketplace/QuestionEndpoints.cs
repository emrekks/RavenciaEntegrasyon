using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Marketplace;

public static class QuestionEndpoints
{
    // Every marketplace whose questions the panel reads; "Yenile" queues all of them.
    internal static readonly string[] QuestionPlatforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA"];

    public static IEndpointRouteBuilder MapQuestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        api.MapGet("/questions", async (HttpContext http, IMarketplaceQuestionService service, string? kind, string? status, string? platform, Guid? connectionId, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? search, int? page, int? limit, string? sort) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            return tenant is null ? MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http) : Results.Ok(await service.ListAsync(tenant.TenantId, new(kind ?? "ALL", status, platform, connectionId, dateFrom, dateTo, search, page ?? 1, limit ?? 50, sort ?? "NEWEST"), http.RequestAborted));
        });
        api.MapGet("/questions/{id:guid}", async (Guid id, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            var question = await service.GetAsync(tenant.TenantId, id, http.RequestAborted);
            return question is null ? MarketplaceHub.Api.Security.ApiProblems.NotFound(http, "Soru bulunamadı.") : Results.Ok(question);
        });
        api.MapPost("/questions/sync", async (QuestionSyncRequest request, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!HasIdempotencyKey(http)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400);
            if (!string.IsNullOrWhiteSpace(request.PlatformCode) && !QuestionPlatforms.Contains(request.PlatformCode))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "QUESTION_SYNC_PLATFORM_INVALID", "Soru aktarımı için desteklenen platformu seçin.", 400);
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var sales = http.RequestServices.GetRequiredService<IMarketplaceSalesService>();
            var connections = await db.PlatformConnections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && QuestionPlatforms.Contains(row.PlatformCode)
                && (request.PlatformCode == null || row.PlatformCode == request.PlatformCode)
                && (row.Status == "ACTIVE" || row.Status == "VERIFIED"))
                .Select(row => row.Id).ToListAsync(http.RequestAborted);
            var jobs = new List<Guid>();
            ServiceError? firstError = null;
            // One store that cannot read questions right now does not keep the others from refreshing.
            foreach (var connectionId in connections)
            {
                var queued = await sales.EnqueueQuestionSyncAsync(tenant.TenantId, connectionId, request.Kind, http.TraceIdentifier, http.RequestAborted);
                if (queued.Succeeded) jobs.Add(queued.Value);
                else firstError ??= queued.Error;
            }
            if (jobs.Count == 0 && firstError is not null) return MarketplaceHub.Api.Security.ApiProblems.Of(http, firstError);
            return Results.Accepted(value: new { jobs, queuedConnections = jobs.Count, skippedConnections = connections.Count - jobs.Count });
        });
        api.MapGet("/questions/sync-state", async (HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            return tenant is null ? MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http) : Results.Ok(await service.SyncStatesAsync(tenant.TenantId, http.RequestAborted));
        });
        api.MapPost("/questions/{id:guid}/answer", async (Guid id, AnswerQuestionCommand command, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!ApiIdempotencyHeaderPolicy.TryGetKey(http.Request.Headers, out var key, out var conflictingKeys))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, conflictingKeys ? "IDEMPOTENCY_KEY_CONFLICT" : "IDEMPOTENCY_KEY_REQUIRED", conflictingKeys ? "Idempotency-Key ile X-Idempotency-Key aynı değeri taşımalıdır." : "Idempotency-Key başlığı zorunludur.", 400);
            var result = await service.AnswerAsync(tenant.TenantId, tenant.UserId, id, command.Version, command.Text ?? "", key, http.TraceIdentifier, http.RequestAborted);
            // The answer is sent to the marketplace live; its status is mapped to the panel's.
            if (!result.Succeeded) return MarketplaceHub.Api.Security.ApiProblems.Of(http, MarketplaceErrorStatus.ForPanel(result.Error!));
            http.Response.Headers.ETag = $"\"v{result.Value!.Version}\"";
            return Results.Ok(result.Value);
        }).WithServerBudget();
        api.MapGet("/question-templates", async (HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            return tenant is null ? MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http) : Results.Ok(await service.TemplatesAsync(tenant.TenantId, http.RequestAborted));
        });
        api.MapPost("/question-templates", async (SaveQuestionTemplateCommand command, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            var result = await service.SaveTemplateAsync(tenant.TenantId, null, null, command, http.RequestAborted);
            return result.Succeeded ? Results.Created($"/api/v1/question-templates/{result.Value!.Id:D}", result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        api.MapPut("/question-templates/{id:guid}", async (Guid id, SaveQuestionTemplateCommand command, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!TryVersion(http.Request.Headers.IfMatch, out var version)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "IF_MATCH_REQUIRED", "Güncelleme için geçerli If-Match sürümü zorunludur.", 400);
            var result = await service.SaveTemplateAsync(tenant.TenantId, id, version, command, http.RequestAborted);
            if (!result.Succeeded) return MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
            http.Response.Headers.ETag = $"\"v{result.Value!.Version}\"";
            return Results.Ok(result.Value);
        });
        api.MapDelete("/question-templates/{id:guid}", async (Guid id, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!TryVersion(http.Request.Headers.IfMatch, out var version)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "IF_MATCH_REQUIRED", "Silme için geçerli If-Match sürümü zorunludur.", 400);
            var result = await service.DeleteTemplateAsync(tenant.TenantId, id, version, http.RequestAborted);
            return result.Succeeded ? Results.NoContent() : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        return endpoints;
    }

    // Any non-empty key: the panel prefixes its keys with a scope ("question-sync:{uuid}"), which a GUID-only check refused
    // with "Idempotency-Key başlığı zorunludur" (20 Oct 2026, the questions page's Yenile button).
    private static bool HasIdempotencyKey(HttpContext http) => ApiIdempotencyHeaderPolicy.TryGetKey(http.Request.Headers, out var key, out _) && !string.IsNullOrWhiteSpace(key) && key.Length <= 200;
    private static bool TryVersion(string? value, out long version)
    {
        version = 0;
        return value is { Length: >= 5 }
            && value.StartsWith("\"v", StringComparison.Ordinal)
            && value.EndsWith('"')
            && long.TryParse(value[2..^1], out version)
            && version > 0;
    }
    public sealed record QuestionSyncRequest(string? Kind, string? PlatformCode = null);
}
