namespace ProcBridge.Relay.Api.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// Client address for limits and logs: after <c>UseForwardedHeaders</c> this is the address the trusted proxy saw.
    /// </summary>
    public static string ClientAddress(this HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// The network a client address belongs to, for limits a single owner could dodge by switching addresses: IPv4 /24,
    /// IPv6 /64 (IPv4-mapped IPv6 counts as IPv4). Anything that does not parse is returned as is.
    /// </summary>
    public static string AddressPrefix(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip))
            return address;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        Array.Clear(bytes, 8, 8);
        return new System.Net.IPAddress(bytes) + "/64";
    }
}
