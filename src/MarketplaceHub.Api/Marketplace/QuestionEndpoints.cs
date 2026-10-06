using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Marketplace;

public static class QuestionEndpoints
{
    public static IEndpointRouteBuilder MapQuestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        api.MapGet("/questions", async (HttpContext http, IMarketplaceQuestionService service, string? kind, string? status, string? platform, Guid? connectionId, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? search, int? page, int? limit, string? sort) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            return tenant is null ? Results.Unauthorized() : Results.Ok(await service.ListAsync(tenant.TenantId, new(kind ?? "ALL", status, platform, connectionId, dateFrom, dateTo, search, page ?? 1, limit ?? 50, sort ?? "NEWEST"), http.RequestAborted));
        });
        api.MapGet("/questions/{id:guid}", async (Guid id, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            var question = tenant is null ? null : await service.GetAsync(tenant.TenantId, id, http.RequestAborted);
            return question is null ? Results.NotFound() : Results.Ok(question);
        });
        api.MapPost("/questions/sync", async (QuestionSyncRequest request, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Results.Unauthorized();
            if (!HasIdempotencyKey(http)) return Results.BadRequest(new { code = "IDEMPOTENCY_KEY_REQUIRED", title = "X-Idempotency-Key başlığı zorunludur." });
            if (!string.IsNullOrWhiteSpace(request.PlatformCode) && request.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA"))
                return Results.BadRequest(new { code = "QUESTION_SYNC_PLATFORM_INVALID", title = "Soru aktarımı için desteklenen platformu seçin." });
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var sales = http.RequestServices.GetRequiredService<IMarketplaceSalesService>();
            var connections = await db.PlatformConnections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.PlatformCode == "TRENDYOL" || row.PlatformCode == "HEPSIBURADA")
                && (request.PlatformCode == null || row.PlatformCode == request.PlatformCode)
                && (row.Status == "ACTIVE" || row.Status == "VERIFIED"))
                .Select(row => row.Id).ToListAsync(http.RequestAborted);
            var jobs = new List<Guid>();
            foreach (var connectionId in connections)
            {
                var queued = await sales.EnqueueQuestionSyncAsync(tenant.TenantId, connectionId, request.Kind, http.TraceIdentifier, http.RequestAborted);
                if (!queued.Succeeded) return Results.Json(new { code = queued.Error!.Code, title = queued.Error.Message }, statusCode: queued.Error.Status);
                jobs.Add(queued.Value);
            }
            return Results.Accepted(value: new { jobs, queuedConnections = jobs.Count });
        });
        api.MapGet("/questions/sync-state", async (HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            return tenant is null ? Results.Unauthorized() : Results.Ok(await service.SyncStatesAsync(tenant.TenantId, http.RequestAborted));
        });
        api.MapPost("/questions/{id:guid}/answer", async (Guid id, AnswerQuestionCommand command, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Results.Unauthorized();
            var key = http.Request.Headers["X-Idempotency-Key"].ToString();
            if (!HasIdempotencyKey(http)) return Results.BadRequest(new { code = "IDEMPOTENCY_KEY_REQUIRED", title = "X-Idempotency-Key başlığı zorunludur." });
            var result = await service.AnswerAsync(tenant.TenantId, tenant.UserId, id, command.Version, command.Text ?? "", key, http.TraceIdentifier, http.RequestAborted);
            if (!result.Succeeded) return Results.Json(new { code = result.Error!.Code, title = result.Error.Message, status = result.Error.Status }, statusCode: result.Error.Status, contentType: "application/problem+json");
            http.Response.Headers.ETag = $"\"v{result.Value!.Version}\"";
            return Results.Ok(result.Value);
        });
        api.MapGet("/question-templates", async (HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            return tenant is null ? Results.Unauthorized() : Results.Ok(await service.TemplatesAsync(tenant.TenantId, http.RequestAborted));
        });
        api.MapPost("/question-templates", async (SaveQuestionTemplateCommand command, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Results.Unauthorized();
            var result = await service.SaveTemplateAsync(tenant.TenantId, null, null, command, http.RequestAborted);
            return result.Succeeded ? Results.Created($"/api/v1/question-templates/{result.Value!.Id:D}", result.Value) : Results.Json(new { code = result.Error!.Code, title = result.Error.Message }, statusCode: result.Error.Status);
        });
        api.MapPut("/question-templates/{id:guid}", async (Guid id, SaveQuestionTemplateCommand command, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Results.Unauthorized();
            if (!TryVersion(http.Request.Headers.IfMatch, out var version)) return Results.BadRequest(new { code = "IF_MATCH_REQUIRED", title = "Güncelleme için geçerli If-Match sürümü zorunludur." });
            var result = await service.SaveTemplateAsync(tenant.TenantId, id, version, command, http.RequestAborted);
            if (!result.Succeeded) return Results.Json(new { code = result.Error!.Code, title = result.Error.Message }, statusCode: result.Error.Status);
            http.Response.Headers.ETag = $"\"v{result.Value!.Version}\"";
            return Results.Ok(result.Value);
        });
        api.MapDelete("/question-templates/{id:guid}", async (Guid id, HttpContext http, IMarketplaceQuestionService service) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Results.Unauthorized();
            if (!TryVersion(http.Request.Headers.IfMatch, out var version)) return Results.BadRequest(new { code = "IF_MATCH_REQUIRED", title = "Silme için geçerli If-Match sürümü zorunludur." });
            var result = await service.DeleteTemplateAsync(tenant.TenantId, id, version, http.RequestAborted);
            return result.Succeeded ? Results.NoContent() : Results.Json(new { code = result.Error!.Code, title = result.Error.Message }, statusCode: result.Error.Status);
        });
        return endpoints;
    }

    private static bool HasIdempotencyKey(HttpContext http) => http.Request.Headers.TryGetValue("X-Idempotency-Key", out var key) && Guid.TryParse(key.ToString(), out _);
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
