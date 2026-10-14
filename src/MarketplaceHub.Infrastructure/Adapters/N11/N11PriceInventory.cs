using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Price and stock writes (phase 4). n11 queues the update as a task and
// answers with its id; the per-SKU result is read from the task details.
public sealed partial class N11HttpClient : IInventoryPricePort
{
    internal const string PriceStockUpdate = "ms/product/tasks/price-stock-update";
    internal const string TaskDetails = "ms/product/task-details/page-query";

    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationRef>();
        if (!ConnectionWritesEnabled(account.Connection.SettingsJson))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "N11_EXTERNAL_WRITES_DISABLED", "N11 bağlantısında dış yazma kapalı; fiyat-stok gönderilmedi.", HttpStatusCode.Forbidden);
        var result = await SendAsync(account, HttpMethod.Post, PriceStockUpdate, cancellationToken, new StringContent(payloadJson, Encoding.UTF8, "application/json"));
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        return N11TaskMapper.Submitted(document.RootElement, timeProvider.GetUtcNow()) is { } submitted
            ? submitted.Error is null
                ? AdapterResult<RemoteOperationRef>.Success(submitted.Reference!, result.RateLimit)
                : AdapterResult<RemoteOperationRef>.Failure(submitted.Error, result.RateLimit)
            : Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "N11_TASK_CONTRACT_INVALID", "N11 fiyat-stok yanıtında görev numarası yok.", HttpStatusCode.BadGateway);
    }

    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(externalOperationId, NumberStyles.None, CultureInfo.InvariantCulture, out var taskId))
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "N11_TASK_ID_INVALID", "N11 görev numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationStatus>();
        var body = JsonSerializer.Serialize(new { taskId, pageable = new { page = 0, size = 1000 } });
        var result = await SendAsync(account, HttpMethod.Post, TaskDetails, cancellationToken, new StringContent(body, Encoding.UTF8, "application/json"));
        if (!result.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<RemoteOperationStatus>.Success(N11TaskMapper.Status(document.RootElement, externalOperationId), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "N11_TASK_CONTRACT_INVALID", "N11 görev sonucu beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }

    internal static bool ConnectionWritesEnabled(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}

public static class N11TaskMapper
{
    public sealed record SubmitResult(RemoteOperationRef? Reference, AdapterError? Error);

    // A REJECT answer means n11 refused the whole request before queueing it.
    public static SubmitResult? Submitted(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var status = Text(root, "status");
        if (string.Equals(status, "REJECT", StringComparison.OrdinalIgnoreCase))
            return new(null, new(AdapterErrorClass.Validation, "N11_TASK_REJECTED", $"N11 fiyat-stok isteğini reddetti: {Reasons(root) ?? "neden belirtilmedi"}", 422, null, null));
        var id = Text(root, "id");
        return id is null ? null : new(new(id, Text(root, "type") ?? "SKU_UPDATE", now), null);
    }

    // Task states: IN_QUEUE while processing, PROCESSED when done, REJECT when
    // n11 dropped the task. Item results carry the stock code as itemCode.
    public static RemoteOperationStatus Status(JsonElement root, string taskId)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("n11 task detail is not an object.");
        var status = Text(root, "status") ?? "";
        var lines = new List<RemoteOperationLine>();
        if (root.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Object && skus.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                var itemCode = Text(item, "itemCode");
                if (itemCode is null) continue;
                var succeeded = string.Equals(Text(item, "status"), "SUCCESS", StringComparison.OrdinalIgnoreCase);
                lines.Add(new(itemCode, succeeded, Text(item, "id"), succeeded ? null : "N11_SKU_REJECTED", false, succeeded ? Text(item, "status") : Reasons(item) ?? Text(item, "status")));
            }
        }
        var mapped = status.ToUpperInvariant() switch
        {
            "IN_QUEUE" => "IN_PROGRESS",
            "PROCESSED" => "COMPLETED",
            "REJECT" => "COMPLETED",
            _ => status
        };
        return new(taskId, mapped, lines);
    }

    private static string? Reasons(JsonElement element)
    {
        if (!element.TryGetProperty("reasons", out var reasons) || reasons.ValueKind != JsonValueKind.Array) return null;
        var text = string.Join(" ", reasons.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        return string.IsNullOrWhiteSpace(text) ? null : text.Length <= 300 ? text : text[..300];
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
