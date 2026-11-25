using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
    public async Task<AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>> ReturnLineAuditsAsync(AdapterContext context, string externalLineId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ClaimItemAudit(authorized.Connection.ExternalStoreId, externalLineId.Trim()), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Success(TrendyolJsonMapper.ReturnLineAudits(response.Value!), response.RateLimit); }
        catch (JsonException) { return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Configuration());
        var endpoint = ClaimsEndpoint(authorized.Connection.ExternalStoreId, page.Limit, Page(page.Cursor), window);
        // The documented getClaims endpoint is global and does not define the
        // order-stream storeFrontCode header. Keep this read independent from
        // storefront selection so international claims are not hidden or timed out.
        // Production returns default to the Created workflow bucket when no
        // claimItemStatus is supplied. Read each documented status so the
        // local model also receives WaitingInAction (the seller's shipped tab).
        if (string.Equals(authorized.Connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return await PollClaimsByStatusAsync(authorized, window, page, cancellationToken);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken, storeFrontCode: null);
        if (response.Error?.HttpStatus == (int)HttpStatusCode.NotFound && string.Equals(authorized.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase))
            return await PollClaimsByStatusAsync(authorized, window, page, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(TrendyolJsonMapper.Returns(response.Value!, ClaimPageSize(page.Limit), Page(page.Cursor)), response.RateLimit); } catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Configuration());
        if (string.IsNullOrWhiteSpace(externalReturnId)) return AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Contract());
        var endpoint = TrendyolEndpoints.Claims(authorized.Connection.ExternalStoreId) + "?claimIds=" + Uri.EscapeDataString(externalReturnId);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken, storeFrontCode: null); if (!response.IsSuccess) return AdapterResult<RemoteReturnClaim>.Failure(response.Error!, response.RateLimit);
        try
        {
            var page = TrendyolJsonMapper.Returns(response.Value!);
            var claim = page.Items.FirstOrDefault(x => string.Equals(x.ExternalClaimId, externalReturnId, StringComparison.OrdinalIgnoreCase));
            return claim is null ? AdapterResult<RemoteReturnClaim>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_RETURN_NOT_FOUND", "Platform iade kaydı bulunamadı.", 404, null, null), response.RateLimit) : AdapterResult<RemoteReturnClaim>.Success(claim, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<IReadOnlyList<RemoteReturnClaim>>> GetManyAsync(AdapterContext context, IReadOnlyList<string> externalReturnIds, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Configuration());
        var ids = externalReturnIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Success([]);
        var response = await SendAsync(authorized, HttpMethod.Get, ClaimsByIdsEndpoint(authorized.Connection.ExternalStoreId, ids), null, cancellationToken, storeFrontCode: null);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var page = TrendyolJsonMapper.Returns(response.Value!);
            var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Success(page.Items.Where(claim => wanted.Contains(claim.ExternalClaimId)).ToArray(), response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    // claimIds takes one id or several, comma separated; each id is escaped, the commas are not.
    internal static string ClaimsByIdsEndpoint(string sellerId, IReadOnlyList<string> externalReturnIds) =>
        TrendyolEndpoints.Claims(sellerId) + "?claimIds=" + string.Join(",", externalReturnIds.Select(Uri.EscapeDataString));

    public async Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ClaimIssueReasons, null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success(TrendyolJsonMapper.ReturnIssueReasons(response.Value!), response.RateLimit); }
        catch (JsonException) { return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Configuration());
        return await ExecuteReturnActionAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<ReturnActionResult>> ExecuteReturnActionAsync(TrendyolRequestContext authorized, AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        if (!CanWrite(authorized, context)) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (string.IsNullOrWhiteSpace(command.ExternalClaimId) || command.ExternalLineItemIds.Count == 0) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Contract());
        var action = command.Action.Trim().ToUpperInvariant();
        if (action == "APPROVE")
        {
            var response = await SendAsync(authorized, HttpMethod.Put, TrendyolEndpoints.ApproveClaim(authorized.Connection.ExternalStoreId, command.ExternalClaimId), JsonContent.Create(new { claimLineItemIdList = command.ExternalLineItemIds, @params = new { } }), cancellationToken);
            return response.IsSuccess ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "ACCEPTED", null), response.RateLimit) : AdapterResult<ReturnActionResult>.Failure(response.Error!, response.RateLimit);
        }
        if (action != "REJECT" || string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Contract());
        var evidenceOptional = command.ReasonCode is "1651" or "451" or "2101";
        if (!evidenceOptional && command.EvidenceFiles.Count == 0) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Unsupported("Bu ret nedeni için kanıt dosyası zorunludur."));
        // C11: createClaimIssue accepts evidence files of at most 5 MB each; a bigger file is refused here, not by Trendyol.
        if (command.EvidenceFiles.Any(file => file.Content.LongLength > MaxClaimEvidenceBytes))
            return AdapterResult<ReturnActionResult>.Failure(new(AdapterErrorClass.Validation, "TRENDYOL_RETURN_EVIDENCE_TOO_LARGE", "Trendyol iade kanıtı dosya başına en fazla 5 MB olabilir; daha küçük bir dosya yükleyin.", 422, null, null));
        using var multipart = new MultipartFormDataContent();
        foreach (var file in command.EvidenceFiles)
        {
            var part = new ByteArrayContent(file.Content); part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.MimeType); multipart.Add(part, "files", Path.GetFileName(file.FileName));
        }
        var endpoint = TrendyolEndpoints.RejectClaim(authorized.Connection.ExternalStoreId, command.ExternalClaimId, command.ReasonCode, command.ExternalLineItemIds, command.Explanation.Trim());
        var rejected = await SendAsync(authorized, HttpMethod.Post, endpoint, multipart, cancellationToken);
        return rejected.IsSuccess ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "ISSUE_CREATED", null), rejected.RateLimit) : AdapterResult<ReturnActionResult>.Failure(rejected.Error!, rejected.RateLimit);
    }

    internal static readonly string[] ClaimStatuses = ["Created", "WaitingInAction", "WaitingFraudCheck", "Accepted", "Cancelled", "Rejected", "Unresolved", "InAnalysis"];

    internal static int ClaimPageSize(int limit) => Math.Clamp(limit, 1, 200);

    internal static string ClaimsEndpoint(string sellerId, int limit, int pageNumber, ReturnPollWindow window, string? claimItemStatus = null)
    {
        var query = new List<string> { $"size={ClaimPageSize(limit).ToString(CultureInfo.InvariantCulture)}", $"page={pageNumber.ToString(CultureInfo.InvariantCulture)}" };
        if (window.ModifiedAfter is { } after) query.Add("startDate=" + after.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (window.ModifiedBefore is { } before) query.Add("endDate=" + before.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (claimItemStatus is not null) query.Add("claimItemStatus=" + claimItemStatus);
        return TrendyolEndpoints.Claims(sellerId) + "?" + string.Join('&', query);
    }

    // Each status bucket pages on its own: the cursor "s:Created=2,Accepted=5" names the next page of every
    // bucket that still has claims, so an exhausted bucket is not asked again for empty pages. A plain page
    // number (or no cursor) reads every bucket at that page.
    internal static IReadOnlyList<(string Status, int Page)> ClaimBucketPages(string? cursor)
    {
        if (cursor is not null && cursor.StartsWith("s:", StringComparison.Ordinal))
        {
            var pages = new List<(string Status, int Page)>();
            foreach (var entry in cursor[2..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('=');
                if (parts.Length == 2 && ClaimStatuses.Contains(parts[0], StringComparer.Ordinal) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    pages.Add((parts[0], number));
            }
            if (pages.Count > 0) return pages;
        }
        var shared = Page(cursor);
        return ClaimStatuses.Select(status => (status, shared)).ToArray();
    }

    internal static string? ClaimBucketCursor(IReadOnlyList<(string Status, int Page)> nextPages) =>
        nextPages.Count == 0 ? null : "s:" + string.Join(',', nextPages.Select(next => $"{next.Status}={next.Page.ToString(CultureInfo.InvariantCulture)}"));

    private async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollClaimsByStatusAsync(TrendyolRequestContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol claims are read with the documented claimItemStatus buckets so all seller return tabs are synchronized.");
        var buckets = ClaimBucketPages(page.Cursor);
        var claims = new List<RemoteReturnClaim>();
        var nextPages = new List<(string Status, int Page)>();
        RateLimitMetadata? rateLimit = null;
        var responses = await Task.WhenAll(buckets.Select(bucket =>
            SendAsync(context, HttpMethod.Get, ClaimsEndpoint(context.Connection.ExternalStoreId, page.Limit, bucket.Page, window, bucket.Status), null, cancellationToken, storeFrontCode: null)));
        for (var index = 0; index < responses.Length; index++)
        {
            var response = responses[index];
            var bucket = buckets[index];
            rateLimit = response.RateLimit ?? rateLimit;
            if (response.Error?.HttpStatus == (int)HttpStatusCode.NotFound) continue;
            if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
            try
            {
                var result = TrendyolJsonMapper.Returns(response.Value!, ClaimPageSize(page.Limit), bucket.Page);
                logger.LogInformation("Trendyol claims bucket {Status} page {Page} returned {Count} claims.", bucket.Status, bucket.Page, result.Items.Count);
                claims.AddRange(result.Items);
                if (result.HasMore) nextPages.Add((bucket.Status, bucket.Page + 1));
            }
            catch (JsonException exception)
            {
                logger.LogWarning("Trendyol claims bucket {Status} page {Page} broke the page contract: {Reason}", bucket.Status, bucket.Page, exception.Message);
                return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit);
            }
        }
        var uniqueClaims = claims.GroupBy(x => x.ExternalClaimId, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var primary = group.OrderByDescending(x => x.LastModifiedAt).First();
            var cargo = group.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CargoTrackingNumber) || !string.IsNullOrWhiteSpace(x.CargoProviderName));
            var mergedLines = group.SelectMany(x => x.Lines)
                .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
                .Select(lines => lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.RawStatus)) ?? lines.First())
                .ToList();
            return primary with
            {
                Lines = mergedLines,
                CargoProviderName = primary.CargoProviderName ?? cargo?.CargoProviderName,
                CargoTrackingNumber = primary.CargoTrackingNumber ?? cargo?.CargoTrackingNumber,
                CargoTrackingLink = primary.CargoTrackingLink ?? cargo?.CargoTrackingLink
            };
        }).OrderByDescending(x => x.LastModifiedAt).ToArray();
        logger.LogInformation("Trendyol claims status buckets merged to {Count} unique claims: {Statuses}.", uniqueClaims.Length, string.Join(",", uniqueClaims.GroupBy(x => x.RawStatus).Select(x => $"{x.Key}:{x.Count()}")));
        var nextCursor = ClaimBucketCursor(nextPages);
        return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(uniqueClaims, nextCursor, nextCursor is not null), rateLimit);
    }

    // Trendyol documents 5 MB per evidence file; the decimal megabyte is the safe reading.
    internal const long MaxClaimEvidenceBytes = 5_000_000;
}
