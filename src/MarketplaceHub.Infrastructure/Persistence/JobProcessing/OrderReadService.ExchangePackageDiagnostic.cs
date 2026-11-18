using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketplaceHub.Infrastructure.Persistence;

// Read-only diagnostic (owner, 18 Nov 2026): does Trendyol's order API return an exchange's new package? The claim names
// it (getClaims replacementOutboundpackageinfo, ReturnClaim.ReplacementPackageId), the 11 Nov resend diagnostic found
// that no order read returned a resend package. After a Trendyol recovery read, each exchange package not checked yet is
// read once with the same queries as that diagnostic (order number v2/v1, claims, v2 shipmentPackageIds with dates, the
// stream around the claim's last change); whether each answer holds the package is logged (no content) and the answers,
// buyer fields removed, are kept as a resolved operational issue. No order, package, line or invoice row is written; a
// cursor per package keeps it to one run.
internal sealed partial class OrderReadService
{
    internal const string ExchangePackageDiagnosticCursorPrefix = "TRENDYOL_EXCHANGE_PACKAGE_DIAGNOSTIC:";
    internal const string ExchangePackageDiagnosticIssueCode = "TRENDYOL_EXCHANGE_PACKAGE_DIAGNOSTIC";

    private async Task RunExchangePackageDiagnosticOnce(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        if (orders is not IOrderRawReadPort raw || cancellationToken.IsCancellationRequested) return;
        if (await ConnectionPlatformCode(tenantId, connectionId, cancellationToken) != "TRENDYOL") return;
        var candidates = await (from claim in db.ReturnClaims.AsNoTracking()
                                join order in db.Orders.AsNoTracking() on new { claim.TenantId, Id = claim.OrderId } equals new { order.TenantId, order.Id }
                                where claim.TenantId == tenantId && claim.ConnectionId == connectionId && claim.ReplacementPackageId != null
                                    && claim.CustomerRequest == ReturnCustomerRequestPolicy.Change
                                select new { claim.ReplacementPackageId, order.Id, order.OrderNumber, order.OrderedAt, claim.LastRemoteModifiedAt })
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            var resource = ExchangePackageDiagnosticCursorPrefix + candidate.ReplacementPackageId;
            if (await db.SyncCursors.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resource && x.LastSuccessAt != null, cancellationToken)) continue;
            var firstPackages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == candidate.Id).Select(x => x.ExternalPackageId).ToListAsync(cancellationToken);
            var packageIds = new[] { candidate.ReplacementPackageId! }.Concat(firstPackages).Distinct(StringComparer.Ordinal).Take(10).ToArray();
            var around = candidate.LastRemoteModifiedAt ?? timeProvider.GetUtcNow();
            TrackRequest();
            var read = await raw.ReadOrderRawAsync(Context(tenantId, connectionId, correlationId, $"exchange-package-diagnostic:{candidate.ReplacementPackageId}"),
                candidate.OrderNumber, packageIds, candidate.OrderedAt, around.AddMinutes(-15), around.AddMinutes(15), cancellationToken);
            var now = timeProvider.GetUtcNow();
            var cursor = await Cursor(tenantId, connectionId, resource, cancellationToken);
            cursor.LastAttemptAt = now;
            cursor.Version++;
            if (!read.IsSuccess)
            {
                cursor.LastError = read.Error?.Code;
                cursor.LastErrorAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            var found = read.Value!.Select(x => new { x.Query, x.HttpStatus, Holds = ExchangePackageDiagnostic.Holds(x.Body, candidate.ReplacementPackageId!) }).ToList();
            (db.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<OrderReadService>().LogInformation(
                "Exchange package {PackageId} diagnostic: {Answers}", candidate.ReplacementPackageId,
                string.Join("; ", found.Select(x => $"{x.Query}={x.HttpStatus?.ToString() ?? "error"}:{(x.Holds ? "package" : "none")}")));
            var key = $"trendyol-exchange-package-diagnostic:{connectionId}:{candidate.ReplacementPackageId}";
            var summary = JsonSerializer.Serialize(new { packageId = candidate.ReplacementPackageId, readAt = now, found, reads = read.Value });
            var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
            if (issue is null)
                db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = ExchangePackageDiagnosticIssueCode, Summary = summary, Status = IssueStatus.Resolved, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1, ResolvedAt = now });
            else
            {
                issue.Summary = summary;
                issue.LastSeenAt = now;
                issue.OccurrenceCount++;
            }
            cursor.LastSuccessAt = now;
            cursor.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

internal static class ExchangePackageDiagnostic
{
    // Whether a (redacted) answer holds a package with this id: a "shipmentPackageId", "id" or "packageId" equal to it,
    // anywhere in the document. Only the id is compared; nothing else is read.
    internal static bool Holds(string? body, string packageId)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return Walk(document.RootElement, packageId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Walk(JsonElement value, string packageId)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(item => Walk(item, packageId));
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "shipmentPackageId" or "id" or "packageId" or "packageid"
                && property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String && property.Value.ToString() == packageId) return true;
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && Walk(property.Value, packageId)) return true;
        }
        return false;
    }
}
