using System.Net;

namespace SimpleHttpListener.Rx;

/// <summary>
/// Host name / IP address resolution helpers.
/// </summary>
public static class UriExtensions
{
    /// <summary>
    /// Resolves <paramref name="host"/> (an IP literal or a DNS name) to an IPv4-mapped
    /// address, or <see langword="null"/> if it does not resolve.
    /// </summary>
    public static async Task<IPAddress?> GetIPv4AddressAsync(this string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address.MapToIPv4();
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault()?.MapToIPv4();
    }

    /// <summary>
    /// Resolves <paramref name="host"/> (an IP literal or a DNS name) to an IPv6-mapped
    /// address, or <see langword="null"/> if it does not resolve.
    /// </summary>
    public static async Task<IPAddress?> GetIPv6AddressAsync(this string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address.MapToIPv6();
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault()?.MapToIPv6();
    }
}
