using System.Text;

namespace MarketplaceHub.Infrastructure.Adapters;

// Marketplace answers are read into memory whole; none is legitimately near 16 MB (a 200-package order page
// is a few hundred KB). A larger body, or one announced larger, fails as an HttpRequestException (the adapters'
// network-error path) instead of exhausting the process's memory.
internal static class MarketplaceResponseLimits
{
    internal const long MaxResponseBytes = 16 * 1024 * 1024;

    // A client whose buffered answers (the default HttpCompletionOption.ResponseContentRead) stop at the limit.
    internal static HttpClient CreateBoundedClient(this IHttpClientFactory clients, string name)
    {
        var client = clients.CreateClient(name);
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        return client;
    }

    // The body of an answer read with ResponseHeadersRead, which the client buffer limit does not cover. A connection
    // that breaks while the body streams (HttpIOException, an IOException) or a corrupt compressed body
    // (InvalidDataException) also fails as an HttpRequestException, so it takes the callers' network-error path instead
    // of escaping the adapter unclassified (ADAPTOR-TY-HB-2); a buffered read already reports it that way.
    internal static async Task<string> ReadBoundedStringAsync(this HttpContent content, CancellationToken cancellationToken, long maxBytes = MaxResponseBytes)
    {
        if (content.Headers.ContentLength > maxBytes) throw TooLarge(maxBytes);
        using var buffer = new MemoryStream();
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw TooLarge(maxBytes);
                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The marketplace response ended before its body was read.", exception);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Charset(content), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static Encoding Charset(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        try { return Encoding.GetEncoding(charset); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static HttpRequestException TooLarge(long maxBytes) =>
        new($"The marketplace response is larger than the {maxBytes / (1024 * 1024)} MB limit.");
}
