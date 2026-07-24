using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimpleHttpListener.Rx.Internal;

/// <summary>
/// Derives the local endpoint a datagram was actually delivered to from the per-packet
/// information the socket reports.
/// </summary>
/// <remarks>
/// A multicast socket must bind the wildcard address on macOS and Linux, so the socket's
/// bound endpoint says <c>0.0.0.0</c> rather than the address the datagram arrived on —
/// useless to a consumer that needs an address to advertise back to the sender (e.g. an
/// SSDP callback URL). For a unicast datagram the packet's destination address is that
/// address; for a multicast or broadcast datagram it is the group address, so the receiving
/// interface has to be resolved from the interface index instead. Resolution failures fall
/// back to the bound endpoint: degraded information, never a dropped message.
/// </remarks>
internal sealed class UdpLocalEndPointResolver : IDisposable
{
    private readonly Lock _gate = new();

    private InterfaceAddresses? _cache;
    private bool _listensForAddressChanges;

    internal UdpLocalEndPointResolver()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _listensForAddressChanges = true;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Without change notifications the interface cache is built once and kept. A
            // stale entry only costs accuracy on a machine that re-addresses mid-run.
        }
    }

    /// <summary>
    /// Turns on per-packet information for the socket. .NET does this itself on the first
    /// <c>ReceiveMessageFrom</c> call, so this is belt and braces: it keeps the behaviour
    /// independent of that implementation detail across Windows, macOS and Linux. Options
    /// the platform rejects for the socket's family are ignored.
    /// </summary>
    internal static void TryEnablePacketInformation(Socket socket)
    {
        if (socket.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        {
            // Dual-mode sockets need the IPv4 level as well, hence no else.
            TrySetOption(socket, SocketOptionLevel.IP);
        }

        if (socket.AddressFamily is AddressFamily.InterNetworkV6)
        {
            TrySetOption(socket, SocketOptionLevel.IPv6);
        }

        static void TrySetOption(Socket socket, SocketOptionLevel level)
        {
            try
            {
                socket.SetSocketOption(level, SocketOptionName.PacketInformation, true);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
            {
                // The socket family does not take this option; the receive call still works,
                // and a missing packet information falls back to the bound endpoint.
            }
        }
    }

    internal IPEndPoint? Resolve(in IPPacketInformation packetInformation, IPEndPoint? boundEndPoint) =>
        Resolve(packetInformation.Address, packetInformation.Interface, boundEndPoint);

    /// <param name="destination">Destination address of the received packet, if reported.</param>
    /// <param name="interfaceIndex">Index of the interface the packet was received on.</param>
    /// <param name="boundEndPoint">The socket's bound endpoint; supplies the port, and the
    /// fallback address when the receiving interface cannot be determined.</param>
    internal IPEndPoint? Resolve(IPAddress? destination, int interfaceIndex, IPEndPoint? boundEndPoint)
    {
        if (destination is null
            || destination.Equals(IPAddress.Any)
            || destination.Equals(IPAddress.IPv6Any))
        {
            return boundEndPoint;
        }

        if (destination.IsIPv4MappedToIPv6)
        {
            // A dual-mode socket reports IPv4 traffic mapped; unmap so that the group check
            // below sees a plain IPv4 address and consumers get a usable IPv4 address.
            destination = destination.MapToIPv4();
        }

        if (!IsGroupAddress(destination))
        {
            // Unicast: the packet's destination is the local address it was delivered to.
            return WithPortOf(destination, boundEndPoint);
        }

        if (destination.AddressFamily is not AddressFamily.InterNetwork)
        {
            // IPv6 multicast: mapping an interface index to the "right" IPv6 address (link
            // local vs global) is its own decision — keep the pre-existing behaviour.
            return boundEndPoint;
        }

        var interfaceAddress = ResolveInterfaceAddress(interfaceIndex);

        return interfaceAddress is null
            ? boundEndPoint
            : WithPortOf(interfaceAddress, boundEndPoint);
    }

    public void Dispose()
    {
        if (!_listensForAddressChanges)
        {
            return;
        }

        _listensForAddressChanges = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    private static IPEndPoint WithPortOf(IPAddress address, IPEndPoint? boundEndPoint) =>
        new(address, boundEndPoint?.Port ?? 0);

    private bool IsGroupAddress(IPAddress address)
    {
        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }

        Span<byte> octets = stackalloc byte[4];

        if (!address.TryWriteBytes(octets, out _))
        {
            return false;
        }

        return octets[0] is >= 224 and <= 239                  // 224.0.0.0/4 multicast
               || address.Equals(IPAddress.Broadcast)          // 255.255.255.255
               || IsDirectedBroadcast(address);                // e.g. 192.168.1.255
    }

    private bool IsDirectedBroadcast(IPAddress address)
    {
        lock (_gate)
        {
            return GetCache().DirectedBroadcasts.Contains(address);
        }
    }

    private IPAddress? ResolveInterfaceAddress(int interfaceIndex)
    {
        lock (_gate)
        {
            return GetCache().AddressByInterfaceIndex.GetValueOrDefault(interfaceIndex);
        }
    }

    private InterfaceAddresses GetCache() => _cache ??= BuildCache();

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    /// <summary>
    /// Enumerates the machine's interfaces once; datagram handling then costs a dictionary
    /// lookup. Rebuilt on the next datagram after an address change.
    /// </summary>
    private static InterfaceAddresses BuildCache()
    {
        var addressByInterfaceIndex = new Dictionary<int, IPAddress>();
        var directedBroadcasts = new HashSet<IPAddress>();
        var cache = new InterfaceAddresses(addressByInterfaceIndex, directedBroadcasts);

        NetworkInterface[] networkInterfaces;

        try
        {
            networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return cache;
        }

        foreach (var networkInterface in networkInterfaces)
        {
            if (networkInterface.OperationalStatus is not OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                var properties = networkInterface.GetIPProperties();
                var index = properties.GetIPv4Properties()?.Index;

                if (index is null)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is not AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    // An APIPA address is a last resort: prefer any real address on the NIC.
                    if (!addressByInterfaceIndex.TryGetValue(index.Value, out var known)
                        || (IsLinkLocal(known) && !IsLinkLocal(unicast.Address)))
                    {
                        addressByInterfaceIndex[index.Value] = unicast.Address;
                    }

                    if (TryGetDirectedBroadcast(unicast, out var broadcast))
                    {
                        directedBroadcasts.Add(broadcast);
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // The interface has no IPv4 configuration, or the platform will not report
                // it — skip it rather than lose the interfaces that do work.
            }
        }

        return cache;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        Span<byte> octets = stackalloc byte[4];

        return address.TryWriteBytes(octets, out _) && octets is [169, 254, _, _];
    }

    private static bool TryGetDirectedBroadcast(
        UnicastIPAddressInformation unicast,
        [NotNullWhen(true)] out IPAddress? broadcast)
    {
        broadcast = null;

        IPAddress mask;

        try
        {
            mask = unicast.IPv4Mask;
        }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException)
        {
            return false;
        }

        if (mask is null || mask.Equals(IPAddress.Any) || mask.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        Span<byte> maskOctets = stackalloc byte[4];

        if (!unicast.Address.TryWriteBytes(octets, out _) || !mask.TryWriteBytes(maskOctets, out _))
        {
            return false;
        }

        for (var i = 0; i < octets.Length; i++)
        {
            octets[i] |= (byte)~maskOctets[i];
        }

        broadcast = new IPAddress(octets);

        return true;
    }

    private sealed record InterfaceAddresses(
        Dictionary<int, IPAddress> AddressByInterfaceIndex,
        HashSet<IPAddress> DirectedBroadcasts);
}
