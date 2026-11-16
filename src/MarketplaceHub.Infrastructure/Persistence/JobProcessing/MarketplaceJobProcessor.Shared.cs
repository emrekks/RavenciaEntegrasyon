using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ISLEYICI-9 stage 1 (16 Nov 2026): cursors and issues are kept by their own classes on this job's AppDbContext.
    private readonly SyncCursorStore cursors = new(db);
    private readonly IssueRecorder issues = new(db);

    private static bool IsAmbiguous(AdapterError error) => WriteOutcomePolicy.IsUncertain(error);

    // The payload readers, cursors and issues live in JobPayloadReader, SyncCursorStore and IssueRecorder; these names
    // stay so the call sites read as before.
    private static int ReadBoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum) =>
        JobPayloadReader.BoundedInt(payloadJson, propertyName, fallback, minimum, maximum);

    private static bool ReadBoolean(string payloadJson, string propertyName) => JobPayloadReader.Boolean(payloadJson, propertyName);

    private static bool ReadBooleanOrDefault(string payloadJson, string propertyName, bool fallback) =>
        JobPayloadReader.BooleanOrDefault(payloadJson, propertyName, fallback);

    private static string? ReadText(string payloadJson, string propertyName) => JobPayloadReader.Text(payloadJson, propertyName);

    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    // Looks at the issues this unit of work already holds first, as RecordIssue does (ISLEYICI-8): an issue opened and
    // resolved before the next save is resolved, not left open.
    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => TextValues.Sha256Hex(value);
    internal static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
