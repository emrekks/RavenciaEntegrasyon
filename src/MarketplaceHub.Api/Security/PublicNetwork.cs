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
        // A proxy from the environment (HTTPS_PROXY) would make the connect-time address check meaningless.
        UseProxy = false,
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
            // Forms that carry an IPv4 address inside are judged by that address (an internal one is refused).
            if (EmbeddedIPv4(b) is { } embedded) return IsPublic(embedded);
            // Only global unicast (2000::/3) is public; inside it, the special-purpose blocks are refused.
            if ((b[0] & 0xE0) != 0x20) return false;
            return !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8 // documentation 2001:db8::/32
                || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00 // Teredo 2001::/32 (an IPv4 relay)
                || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) is 0x10 or 0x20 // ORCHID 2001:10::/28, 2001:20::/28
                || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x02 && b[4] == 0 && b[5] == 0 // benchmarking 2001:2::/48
                || b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00); // documentation 3fff::/20
        }
        return false;
    }

    // NAT64 (64:ff9b::/96 and the local-use 64:ff9b:1::/48), 6to4 (2002::/16), IPv4-compatible (::a.b.c.d)
    // and IPv4-translated (::ffff:0:a.b.c.d) addresses route to the IPv4 address they carry.
    internal static IPAddress? EmbeddedIPv4(byte[] b)
    {
        if (b.Length != 16) return null;
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0) return new IPAddress(b.AsSpan(12, 4));
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01) return new IPAddress(b.AsSpan(12, 4));
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b.AsSpan(2, 4));
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && b.AsSpan(12, 4).IndexOfAnyExcept((byte)0) >= 0) return new IPAddress(b.AsSpan(12, 4));
        if (b.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0 && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0) return new IPAddress(b.AsSpan(12, 4));
        return null;
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
