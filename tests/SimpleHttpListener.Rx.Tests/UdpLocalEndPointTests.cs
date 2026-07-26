using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using SimpleHttpListener.Rx.Internal;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

/// <summary>
/// The local endpoint reported for a UDP message must be the address the datagram was
/// delivered to, not the socket's bound address — a multicast socket has to bind the
/// wildcard address on macOS and Linux.
/// </summary>
public class UdpLocalEndPointTests
{
    [Fact]
    public async Task Udp_reports_receiving_interface_not_wildcard_bind()
    {
        // Bound to the wildcard address, exactly as a multicast listener must be.
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = receiver.LocalPort();

        var firstMessage = receiver.ToHttpListenerObservable()
            .FirstAsync()
            .ToTask();

        using var sender = new UdpClient();
        await sender.SendAsync(TestNetwork.SsdpNotify(), new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TestNetwork.Timeout);

        var message = await firstMessage.WaitAsync(TestNetwork.Timeout);

        Assert.NotNull(message.LocalEndPoint);
        Assert.Equal(IPAddress.Loopback, message.LocalEndPoint.Address);
        Assert.Equal(port, message.LocalEndPoint.Port);
        Assert.Equal("NOTIFY", message.Method);
    }

    [Fact]
    public async Task Udp_keeps_receiving_after_the_first_datagram()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = receiver.LocalPort();

        var twoMessages = receiver.ToHttpListenerObservable()
            .Take(2)
            .ToList()
            .ToTask();

        using var sender = new UdpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        await sender.SendAsync(TestNetwork.SsdpNotify(), destination).AsTask().WaitAsync(TestNetwork.Timeout);
        await sender.SendAsync(TestNetwork.SsdpNotify(), destination).AsTask().WaitAsync(TestNetwork.Timeout);

        var messages = await twoMessages.WaitAsync(TestNetwork.Timeout);

        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Equal(IPAddress.Loopback, message.LocalEndPoint!.Address));
    }

    [Fact]
    public void Unicast_destination_is_used_as_local_address_with_the_bound_port()
    {
        using var resolver = new UdpLocalEndPointResolver();
        var bound = new IPEndPoint(IPAddress.Any, 1900);

        var resolved = resolver.Resolve(IPAddress.Parse("192.168.0.10"), interfaceIndex: 7, bound);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.10"), 1900), resolved);
    }

    [Fact]
    public void Multicast_destination_resolves_to_the_receiving_interface_address()
    {
        var loopback = LoopbackInterfaceIndex();

        Assert.SkipWhen(loopback is null, "No IPv4 loopback interface to resolve.");

        using var resolver = new UdpLocalEndPointResolver();
        var bound = new IPEndPoint(IPAddress.Any, 1900);

        var resolved = resolver.Resolve(IPAddress.Parse("239.255.255.250"), loopback!.Value, bound);

        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 1900), resolved);
    }

    [Fact]
    public void Unresolvable_interface_index_falls_back_to_the_bound_endpoint()
    {
        using var resolver = new UdpLocalEndPointResolver();
        var bound = new IPEndPoint(IPAddress.Any, 1900);

        var resolved = resolver.Resolve(IPAddress.Parse("239.255.255.250"), int.MaxValue, bound);

        Assert.Same(bound, resolved);
    }

    [Fact]
    public void Missing_packet_information_falls_back_to_the_bound_endpoint()
    {
        using var resolver = new UdpLocalEndPointResolver();
        var bound = new IPEndPoint(IPAddress.Any, 1900);

        // Default IPPacketInformation (option unsupported): no address, interface index 0.
        Assert.Same(bound, resolver.Resolve(destination: null, interfaceIndex: 0, bound));
        Assert.Same(bound, resolver.Resolve(IPAddress.Any, interfaceIndex: 0, bound));
    }

    [Fact]
    public void Ipv6_multicast_destination_falls_back_to_the_bound_endpoint()
    {
        using var resolver = new UdpLocalEndPointResolver();
        var bound = new IPEndPoint(IPAddress.IPv6Any, 1900);

        var resolved = resolver.Resolve(IPAddress.Parse("ff02::c"), interfaceIndex: 1, bound);

        Assert.Same(bound, resolved);
    }

    [Fact]
    public void Ipv4_mapped_unicast_destination_is_unmapped()
    {
        using var resolver = new UdpLocalEndPointResolver();
        var bound = new IPEndPoint(IPAddress.IPv6Any, 1900);

        var resolved = resolver.Resolve(IPAddress.Parse("192.168.0.10").MapToIPv6(), interfaceIndex: 7, bound);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.10"), 1900), resolved);
    }

    [Fact]
    public void Degraded_local_endpoint_still_emits_the_message()
    {
        var bound = new IPEndPoint(IPAddress.Any, 1900);

        var message = HttpMessageParser.ParseDatagram(
            TestNetwork.SsdpNotify(), false, bound, new IPEndPoint(IPAddress.Loopback, 55555));

        Assert.False(message.HasParsingErrors);
        Assert.Equal("NOTIFY", message.Method);
        Assert.Equal(bound, message.LocalEndPoint);
        Assert.Equal(HttpTransport.Udp, message.Transport);
    }

    private static int? LoopbackInterfaceIndex()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                || networkInterface.OperationalStatus is not OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                var properties = networkInterface.GetIPProperties();

                if (properties.UnicastAddresses.Any(unicast =>
                        unicast.Address.Equals(IPAddress.Loopback)))
                {
                    return properties.GetIPv4Properties()?.Index;
                }
            }
            catch (NetworkInformationException)
            {
                // No IPv4 on this interface.
            }
        }

        return null;
    }
}
