using Microsoft.Net.Http.Headers;

namespace MarketplaceHub.Api.Catalog;

// Product photo answers are cached by the browser for a year: a stored file asset never changes (a new upload is a
// new id), so its address names one content for good. The entity tag is the stored SHA-256 (plus the thumbnail
// width), and a request that already holds it is answered 304 without the body.
internal static class ProductMediaCaching
{
    public const string PrivateImmutable = "private, max-age=31536000, immutable";
    public const string PublicImmutable = "public, max-age=31536000, immutable";
    // A marketplace address the panel proxies can change behind the same URL; a week, then revalidated.
    public const string PrivateRemote = "private, max-age=604800";
    // The original sent in place of a thumbnail that could not be made: short, so a later visit gets the thumbnail.
    public const string PrivateShort = "private, max-age=300";
    // Opened on its own the answer is an inert image: no script, no plugins, no same-origin rights (A13).
    public const string InertImagePolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";

    public static string EntityTag(string sha256, int? width = null) => width is int value ? $"\"{sha256}-w{value}\"" : $"\"{sha256}\"";

    public static bool IsNotModified(HttpRequest request, string entityTag)
    {
        var tags = request.GetTypedHeaders().IfNoneMatch;
        if (tags is null || tags.Count == 0) return false;
        var tag = new EntityTagHeaderValue(entityTag);
        return tags.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(tag, useStrongComparison: false));
    }

    public static void Apply(HttpResponse response, string cacheControl, string entityTag)
    {
        response.Headers.CacheControl = cacheControl;
        response.Headers.ETag = entityTag;
    }

    public static IResult NotModified(HttpResponse response, string cacheControl, string entityTag)
    {
        Apply(response, cacheControl, entityTag);
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }
}
