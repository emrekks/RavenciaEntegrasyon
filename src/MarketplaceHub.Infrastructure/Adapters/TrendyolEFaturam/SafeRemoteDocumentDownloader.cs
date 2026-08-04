using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class SafeRemoteDocumentDownloader(IHttpClientFactory clients, IOptions<TrendyolEFaturamOptions> options)
{
    public const long MaximumDocumentBytes = 20_000_000;
    private const int MaximumRedirects = 3;
    private readonly HashSet<string> allowedHosts = BuildAllowedHosts(options.Value);
    private readonly TimeSpan timeout = options.Value.Timeout > TimeSpan.Zero && options.Value.Timeout <= TimeSpan.FromMinutes(2)
        ? options.Value.Timeout
        : TimeSpan.FromSeconds(30);

    public async Task<SafeDocumentDownloadResult> DownloadPdfAsync(string url, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var effectiveToken = timeoutSource.Token;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current)) return SafeDocumentDownloadResult.Rejected("DOCUMENT_URL_INVALID");
        for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
        {
            var validation = await ValidateUriAsync(current, effectiveToken);
            if (validation is not null) return SafeDocumentDownloadResult.Rejected(validation);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("application/pdf");
            using var response = await clients.CreateClient("TrendyolEFaturamDocument").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirects == MaximumRedirects || response.Headers.Location is null) return SafeDocumentDownloadResult.Rejected("DOCUMENT_REDIRECT_REJECTED");
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            if (!response.IsSuccessStatusCode) return SafeDocumentDownloadResult.RemoteFailure(response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaximumDocumentBytes) return SafeDocumentDownloadResult.Rejected("DOCUMENT_TOO_LARGE");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return SafeDocumentDownloadResult.Rejected("DOCUMENT_CONTENT_TYPE_INVALID");

            await using var source = await response.Content.ReadAsStreamAsync(effectiveToken);
            await using var destination = new MemoryStream();
            var buffer = new byte[81_920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), effectiveToken);
                if (read == 0) break;
                total += read;
                if (total > MaximumDocumentBytes) return SafeDocumentDownloadResult.Rejected("DOCUMENT_TOO_LARGE");
                await destination.WriteAsync(buffer.AsMemory(0, read), effectiveToken);
            }
            var content = destination.ToArray();
            if (content.Length < 5 || content[0] != (byte)'%' || content[1] != (byte)'P' || content[2] != (byte)'D' || content[3] != (byte)'F' || content[4] != (byte)'-')
                return SafeDocumentDownloadResult.Rejected("DOCUMENT_PDF_SIGNATURE_INVALID");
            return SafeDocumentDownloadResult.Success(content, current.AbsoluteUri);
        }
        return SafeDocumentDownloadResult.Rejected("DOCUMENT_REDIRECT_REJECTED");
    }

    public static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var publicAddresses = addresses.Where(IsPublicAddress).ToArray();
        if (publicAddresses.Length == 0) throw new HttpRequestException("Remote document host did not resolve to an allowed public address.");
        Exception? last = null;
        foreach (var address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                last = exception;
                socket.Dispose();
                if (exception is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("Remote document host could not be reached through a public address.", last);
    }

    private async Task<string?> ValidateUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return "DOCUMENT_HTTPS_REQUIRED";
        if (!uri.IsDefaultPort && uri.Port != 443) return "DOCUMENT_PORT_REJECTED";
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return "DOCUMENT_URL_COMPONENT_REJECTED";
        if (!allowedHosts.Contains(uri.IdnHost)) return "DOCUMENT_HOST_REJECTED";
        var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken);
        return addresses.Length > 0 && addresses.All(IsPublicAddress) ? null : "DOCUMENT_PRIVATE_ADDRESS_REJECTED";
    }

    private static HashSet<string> BuildAllowedHosts(TrendyolEFaturamOptions value)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            value.StageBaseAddress.IdnHost,
            value.ProductionBaseAddress.IdnHost
        };
        foreach (var host in value.DocumentAllowedHosts.Where(x => !string.IsNullOrWhiteSpace(x))) hosts.Add(NormalizeConfiguredHost(host));
        return hosts;
    }

    private static string NormalizeConfiguredHost(string configured)
    {
        var candidate = configured.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = $"https://{candidate}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || Uri.CheckHostName(uri.IdnHost) == UriHostNameType.Unknown)
            throw new InvalidOperationException($"Invalid E-Faturam document allow-list host: {configured}");
        return uri.IdnHost;
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique-local
            if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8) return false; // 2001:db8::/32 documentation
            if (v6[0] == 0x20 && v6[1] == 0x01 && (v6[2] & 0xF0) is 0x10 or 0x20) return false; // ORCHID ranges
            return true;
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
        if (b[0] == 192 && b[1] is 0 or 168) return false;
        if (b[0] == 192 && b[1] == 88 && b[2] == 99) return false;
        if (b[0] == 198 && b[1] is 18 or 19) return false;
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return false;
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
        return true;
    }
}

public sealed record SafeDocumentDownloadResult(byte[]? Content, string? FinalUrl, string? ErrorCode, HttpStatusCode? RemoteStatus)
{
    public bool Succeeded => Content is not null && ErrorCode is null && RemoteStatus is null;
    public static SafeDocumentDownloadResult Success(byte[] content, string finalUrl) => new(content, finalUrl, null, null);
    public static SafeDocumentDownloadResult Rejected(string code) => new(null, null, code, null);
    public static SafeDocumentDownloadResult RemoteFailure(HttpStatusCode status) => new(null, null, null, status);
}
