using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Tests.TestHelpers;

/// <summary>Loopback plumbing shared by the socket-level tests.</summary>
internal static class TestNetwork
{
    /// <summary>
    /// Upper bound on anything that waits for a socket. Generous: a passing test never comes
    /// near it, and a hanging one fails with a timeout rather than stalling the run.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A port nothing is listening on, for tests that must bind a known port.</summary>
    public static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>The port a client ended up bound to.</summary>
    public static int LocalPort(this UdpClient udpClient) =>
        ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;

    /// <summary>
    /// A representative SSDP announcement. A fresh array per call, because the tests that
    /// prove capture copies rather than aliases mutate the bytes they sent.
    /// </summary>
    public static byte[] SsdpNotify() =>
        Encoding.ASCII.GetBytes("NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: upnp:rootdevice\r\n\r\n");

    /// <summary>
    /// What the test listeners reply. Fire-and-forget, as a subscriber must be: awaiting
    /// inside OnNext would block the listener.
    /// </summary>
    public static void SendHelloWorld(HttpRequestResponse request) =>
        _ = request.SendResponseAsync(new HttpResponse
        {
            Headers = { ["Content-Type"] = "text/plain" },
            Body = "Hello, World"u8.ToArray()
        });
}
