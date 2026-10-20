using System.Net;
using System.Net.Sockets;

namespace MarketplaceHub.Api.Security;

// The unit that rate limits and sign-in throttles count per sender. An IPv4 address is one sender; an
// IPv6 address is not, because one connection usually owns a whole /64 and can pick any address in it,
// so IPv6 senders are counted per /64.
public static class ClientNetwork
{
    public static string PartitionKey(HttpContext context) => PartitionKey(context.Connection.RemoteIpAddress);

    public static string PartitionKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    // A coarser unit for the sign-in throttle's "has this sender failed before" question: an IPv6 /48
    // (a site's usual allocation, from which a sender can pick thousands of /64s); anything else as is.
    public static string WideGroup(string partitionKey)
    {
        if (!partitionKey.EndsWith("/64", StringComparison.Ordinal) || !IPAddress.TryParse(partitionKey[..^3], out var address)) return partitionKey;
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 6, 10);
        return $"{new IPAddress(bytes)}/48";
    }
}
