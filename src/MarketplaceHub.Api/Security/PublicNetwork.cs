using System.Net;
using System.Net.Sockets;

namespace MarketplaceHub.Api.Security;

// Outbound requests to addresses a user typed (product image links) may only reach the public
// internet. The host is resolved at connect time and every address it resolves to must be public,
// so a name that points at the server's own network (or is re-pointed there) is refused.
public static class PublicNetwork
{
    public const string ClientName = "PublicMediaProxy";

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
                throw new HttpRequestException($"{context.DnsEndPoint.Host} genel internette olmayan bir adrese çözülüyor.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                || b[0] == 100 && b[1] is >= 64 and <= 127 // carrier-grade NAT
                || b[0] == 169 && b[1] == 254 // link-local, cloud metadata
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
                || b[0] == 198 && b[1] is 18 or 19);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC // unique local fc00::/7
                || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8); // documentation
        }
        return false;
    }

    // GET that follows at most three https redirects itself and reads at most maxBytes; null when
    // the address is refused, the answer is not an image, or it is too large.
    public static async Task<(byte[] Bytes, string ContentType)?> GetImageAsync(HttpClient client, Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= 3; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) return null;
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            if (response.Content.Headers.ContentLength > maxBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length == 0 ? null : (buffer.ToArray(), contentType);
        }
        return null;
    }
}
