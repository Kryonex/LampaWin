using System.Net;
using System.Net.Sockets;

namespace LampaWin.Core;

public static class PublicNetworkPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 198 && bytes[1] is 18 or 19));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // Only globally-routable IPv6 unicast (2000::/3); reject unspecified,
        // loopback, ULA, link-local, multicast, mapped/private and NAT64 ranges.
        return (bytes[0] & 0xe0) == 0x20;
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        foreach (var address in addresses.Where(IsPublic))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the validated address, rather than resolve the hostname a second time.
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (token.IsCancellationRequested) throw;
            }
        }
        throw new HttpRequestException("No permitted public address could be reached.");
    }
}
