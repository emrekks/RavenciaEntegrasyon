using MarketplaceHub.Domain;
using Microsoft.Net.Http.Headers;

namespace MarketplaceHub.Api.Catalog;

// What of a completed response is kept for an idempotent replay, and how the replay restores it.
public static class IdempotentResponse
{
    public const int MaximumStoredBodyBytes = 1024 * 1024;
    private const int MaximumContentTypeLength = 128;
    private const int MaximumETagLength = 128;
    private const int MaximumLocationLength = 2048;
    private const string DefaultContentType = "application/json; charset=utf-8";

    // JSON answers, problem details included (application/problem+json, any application/*+json).
    public static bool IsStorableBody(string? contentType, long length)
    {
        if (length > MaximumStoredBodyBytes || string.IsNullOrWhiteSpace(contentType)) return false;
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed)) return false;
        var mediaType = parsed.MediaType.Value ?? string.Empty;
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    public static void Capture(ApiIdempotencyRecord record, HttpResponse response, string? body)
    {
        record.ResponseStatus = response.StatusCode;
        record.ResponseBody = body;
        record.ResponseContentType = body is null ? null : Bounded(response.ContentType, MaximumContentTypeLength);
        record.ResponseETag = Bounded(response.Headers.ETag.ToString(), MaximumETagLength);
        record.ResponseLocation = Bounded(response.Headers.Location.ToString(), MaximumLocationLength);
    }

    public static async Task ReplayAsync(ApiIdempotencyRecord record, HttpResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = record.ResponseStatus ?? StatusCodes.Status200OK;
        response.Headers["Idempotency-Replayed"] = "true";
        if (!string.IsNullOrEmpty(record.ResponseETag)) response.Headers.ETag = record.ResponseETag;
        if (!string.IsNullOrEmpty(record.ResponseLocation)) response.Headers.Location = record.ResponseLocation;
        if (string.IsNullOrEmpty(record.ResponseBody)) return;
        response.ContentType = string.IsNullOrWhiteSpace(record.ResponseContentType) ? DefaultContentType : record.ResponseContentType;
        await response.WriteAsync(record.ResponseBody, cancellationToken);
    }

    // A value too long for its column is left out rather than cut: a cut ETag or address would be wrong.
    private static string? Bounded(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ? null : value;
}
