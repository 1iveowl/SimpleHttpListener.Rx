using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using SimpleHttpListener.Rx.Internal;
using SimpleHttpListener.Rx.Model;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

/// <summary>
/// Opt-in wire capture: the bytes as received, kept verbatim alongside the parsed view.
/// </summary>
public class RawMessageCaptureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A real SSDP response, mixed-case header names and all — the detail the parsed view
    /// necessarily loses.
    /// </summary>
    private const string SsdpResponse =
        "HTTP/1.1 200 OK\r\n" +
        "Location: http://192.168.0.217:16422\r\n" +
        "Cache-Control: max-age=66\r\n" +
        "Server: UPnP/1.0, DLNADOC/1.50 Platinum/1.0.5.13\r\n" +
        "EXT: \r\n" +
        "OPT: \"http://schemas.upnp.org/upnp/1/0/\"; ns=01\r\n" +
        "01-NLS: 1785066224\r\n" +
        "USN: uuid:bf3f7ffd-777e-4f76-bfb8-b7ff6be2befe::upnp:rootdevice\r\n" +
        "ST: upnp:rootdevice\r\n" +
        "Date: Sun, 26 Jul 2026 14:40:08 GMT\r\n" +
        "\r\n";

    private static byte[] SsdpBytes() => Encoding.ASCII.GetBytes(SsdpResponse);

    private static HttpRequestResponse Parse(byte[] datagram, bool captureRawMessage) =>
        HttpMessageParser.ParseDatagram(
            datagram, false, null, new IPEndPoint(IPAddress.Loopback, 1900), captureRawMessage);

    [Fact]
    public void Captured_bytes_are_the_datagram_byte_for_byte()
    {
        var datagram = SsdpBytes();

        var message = Parse(datagram, captureRawMessage: true);

        Assert.Equal(datagram, message.RawMessage.ToArray());

        var captured = Encoding.ASCII.GetString(message.RawMessage.Span);

        // What the parsed view cannot tell you: original casing, field order, and the exact
        // spelling of values.
        Assert.Contains("Cache-Control: max-age=66", captured);
        Assert.Contains("OPT: \"http://schemas.upnp.org/upnp/1/0/\"; ns=01\r\n01-NLS: 1785066224", captured);
        Assert.StartsWith("HTTP/1.1 200 OK\r\nLocation: http://192.168.0.217:16422", captured);

        // ...while the parsed view has normalised the names it exposes.
        Assert.Equal("max-age=66", message.Headers["CACHE-CONTROL"]);
        Assert.Equal("UPnP/1.0, DLNADOC/1.50 Platinum/1.0.5.13", message.Headers["SERVER"]);
        Assert.Equal(200, message.StatusCode);
    }

    [Fact]
    public void Capture_is_off_by_default_and_changes_nothing_when_on()
    {
        var datagram = SsdpBytes();

        var withoutCapture = Parse(datagram, captureRawMessage: false);
        var withCapture = Parse(datagram, captureRawMessage: true);

        Assert.True(withoutCapture.RawMessage.IsEmpty);
        Assert.False(withCapture.RawMessage.IsEmpty);

        AssertSameParsedResult(withoutCapture, withCapture);
    }

    [Fact]
    public void Captured_bytes_are_a_copy_and_never_alias_the_source()
    {
        var datagram = SsdpBytes();
        var original = datagram.ToArray();

        var message = Parse(datagram, captureRawMessage: true);

        // The receive buffer is pooled and reused; a captured slice of it would rot.
        Array.Fill(datagram, (byte)'X');

        Assert.Equal(original, message.RawMessage.ToArray());
    }

    [Fact]
    public void Unparsable_datagram_still_carries_its_bytes()
    {
        var datagram = "NOT HTTP AT ALL\r\n\r\n"u8.ToArray();

        var message = Parse(datagram, captureRawMessage: true);

        Assert.True(message.HasParsingErrors);
        Assert.Equal(datagram, message.RawMessage.ToArray());
    }

    [Fact]
    public void Datagram_truncated_before_the_header_terminator_still_carries_its_bytes()
    {
        var datagram = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n"u8.ToArray();

        var message = Parse(datagram, captureRawMessage: true);

        Assert.True(message.HasParsingErrors);
        Assert.False(message.IsEndOfMessage);
        Assert.Equal(datagram, message.RawMessage.ToArray());
    }

    [Fact]
    public async Task Udp_listener_captures_the_datagram_when_enabled()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        var firstMessage = receiver
            .ToHttpListenerObservable(new HttpListenerOptions { CaptureRawMessage = true })
            .FirstAsync()
            .ToTask();

        var datagram = SsdpBytes();

        using var sender = new UdpClient();
        await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(Timeout);

        var message = await firstMessage.WaitAsync(Timeout);

        // Exactly the datagram: not a slice of the 64 KiB pooled receive buffer.
        Assert.Equal(datagram, message.RawMessage.ToArray());
        Assert.Equal(200, message.StatusCode);
    }

    [Fact]
    public async Task Udp_listener_leaves_raw_message_empty_by_default()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        var firstMessage = receiver.ToHttpListenerObservable().FirstAsync().ToTask();

        using var sender = new UdpClient();
        await sender.SendAsync(SsdpBytes(), new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(Timeout);

        var message = await firstMessage.WaitAsync(Timeout);

        Assert.True(message.RawMessage.IsEmpty);
        Assert.Equal(200, message.StatusCode);
    }

    [Fact]
    public async Task Tcp_messages_carry_no_raw_bytes_even_when_capture_is_enabled()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var tcpListener = new TcpListener(IPAddress.Loopback, port);

        var firstMessage = tcpListener
            .ToHttpListenerObservable(new HttpListenerOptions { CaptureRawMessage = true })
            .Do(request => _ = request.SendResponseAsync(new HttpResponse()))
            .FirstAsync()
            .ToTask();

        using var httpClient = new HttpClient();
        await httpClient.GetAsync($"http://127.0.0.1:{port}/raw").WaitAsync(Timeout);

        var message = await firstMessage.WaitAsync(Timeout);

        // Documented limitation: a TCP message is framed out of a stream.
        Assert.True(message.RawMessage.IsEmpty);
        Assert.Equal("/raw", message.Path);
    }

    /// <summary>
    /// Capture is observational: every parsed member except <see cref="HttpRequestResponse.RawMessage"/>
    /// must be identical with it on and off.
    /// </summary>
    private static void AssertSameParsedResult(HttpRequestResponse expected, HttpRequestResponse actual)
    {
        foreach (var property in typeof(HttpRequestResponse).GetProperties())
        {
            if (property.Name is nameof(HttpRequestResponse.RawMessage))
            {
                continue;
            }

            var expectedValue = property.GetValue(expected);
            var actualValue = property.GetValue(actual);

            switch (expectedValue)
            {
                case ReadOnlyMemory<byte> expectedBytes:
                    Assert.Equal(expectedBytes.ToArray(), ((ReadOnlyMemory<byte>)actualValue!).ToArray());
                    break;

                case IReadOnlyDictionary<string, string> expectedHeaders:
                    Assert.Equal(
                        expectedHeaders.OrderBy(header => header.Key),
                        ((IReadOnlyDictionary<string, string>)actualValue!).OrderBy(header => header.Key));
                    break;

                default:
                    Assert.Equal(expectedValue, actualValue);
                    break;
            }
        }
    }
}
