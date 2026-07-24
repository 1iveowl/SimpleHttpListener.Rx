using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using IWebsocketClientLite;
using SimpleHttpListener.Rx.Internal;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Tests.TestHelpers;
using WebsocketClientLite;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

public class WebSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string SampleKey = "dGhlIHNhbXBsZSBub25jZQ==";
    private const string SampleAccept = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";

    private static async Task<IList<HttpRequestResponse>> ParseAsync(string payload, bool holdOpen = false)
    {
        var stream = new DribbleStream(Encoding.ASCII.GetBytes(payload)) { HoldOpenAfterPayload = holdOpen };
        var connection = new FakeConnection(stream);

        return await HttpMessageParser
            .ParseConnection(connection, false, CancellationToken.None)
            .ToList()
            .ToTask()
            .WaitAsync(Timeout);
    }

    private static HttpRequestResponse UpgradeRequest(
        FakeConnection? connection,
        Action<Dictionary<string, string>>? mutateHeaders = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOST"] = "x",
            ["UPGRADE"] = "websocket",
            ["CONNECTION"] = "Upgrade",
            ["SEC-WEBSOCKET-KEY"] = SampleKey,
            ["SEC-WEBSOCKET-VERSION"] = "13"
        };

        mutateHeaders?.Invoke(headers);

        return new HttpRequestResponse
        {
            MessageType = MessageType.Request,
            Transport = connection is null ? HttpTransport.Udp : HttpTransport.Tcp,
            Method = "GET",
            Path = "/ws",
            MajorVersion = 1,
            MinorVersion = 1,
            ShouldKeepAlive = true,
            Headers = headers,
            IsEndOfMessage = true,
            IsUpgradeRequest = true,
            Connection = connection
        };
    }

    private static async void EchoWebSocket(HttpRequestResponse request)
    {
        if (!request.IsUpgradeRequest)
        {
            await request.SendResponseAsync(new HttpResponse { Body = "plain"u8.ToArray() });
            return;
        }

        try
        {
            using var webSocket = await request.AcceptWebSocketAsync();
            var buffer = new byte[4096];

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                await webSocket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType,
                    result.EndOfMessage, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Test client disconnects can race the echo loop; the connection is disposed below.
        }
        finally
        {
            request.Connection?.Dispose();
        }
    }

    [Theory]
    [InlineData("Connection: Upgrade\r\nUpgrade: websocket\r\n", true)]
    [InlineData("Connection: keep-alive, Upgrade\r\nUpgrade: websocket\r\n", true)]
    [InlineData("Connection: keep-alive\r\n", false)]
    [InlineData("Upgrade: websocket\r\n", false)]
    public async Task Upgrade_requests_are_detected(string headerLines, bool expected)
    {
        var messages = await ParseAsync(
            $"GET /ws HTTP/1.1\r\nHost: x\r\n{headerLines}Sec-WebSocket-Key: {SampleKey}\r\nSec-WebSocket-Version: 13\r\n\r\n");

        var message = Assert.Single(messages);
        Assert.False(message.HasParsingErrors);
        Assert.Equal(expected, message.IsUpgradeRequest);
    }

    [Fact]
    public async Task Read_loop_hands_off_connection_on_upgrade()
    {
        var stream = new DribbleStream(
            "GET /ws HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"u8.ToArray())
        {
            HoldOpenAfterPayload = true
        };
        var connection = new FakeConnection(stream);

        // Completes only because the loop breaks on the upgrade instead of reading on.
        var messages = await HttpMessageParser
            .ParseConnection(connection, false, CancellationToken.None)
            .ToList()
            .ToTask()
            .WaitAsync(Timeout);

        var message = Assert.Single(messages);
        Assert.True(message.IsUpgradeRequest);
        Assert.False(connection.IsDisposed);
    }

    [Fact]
    public async Task Handshake_produces_rfc6455_sample_response()
    {
        var connection = new FakeConnection();

        using var webSocket = await UpgradeRequest(connection).AcceptWebSocketAsync();

        Assert.Equal(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {SampleAccept}\r\n\r\n",
            Encoding.ASCII.GetString(connection.WrittenBytes));
        Assert.False(connection.IsDisposed);
    }

    [Fact]
    public async Task Handshake_echoes_requested_subprotocol()
    {
        var connection = new FakeConnection();

        using var webSocket = await UpgradeRequest(connection).AcceptWebSocketAsync(subProtocol: "chat");

        Assert.Contains("Sec-WebSocket-Protocol: chat\r\n", Encoding.ASCII.GetString(connection.WrittenBytes));
        Assert.Equal("chat", webSocket.SubProtocol);
    }

    [Fact]
    public async Task Invalid_upgrade_requests_throw()
    {
        // UDP message (no connection)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UpgradeRequest(null).AcceptWebSocketAsync());

        // missing key
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UpgradeRequest(new FakeConnection(), h => h.Remove("SEC-WEBSOCKET-KEY")).AcceptWebSocketAsync());

        // unsupported version
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UpgradeRequest(new FakeConnection(), h => h["SEC-WEBSOCKET-VERSION"] = "8").AcceptWebSocketAsync());

        // not a websocket upgrade
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UpgradeRequest(new FakeConnection(), h => h["UPGRADE"] = "h2c").AcceptWebSocketAsync());
    }

    [Fact]
    public async Task End_to_end_echo_with_ClientWebSocket()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);

        using var subscription = tcpListener.ToHttpListenerObservable().Subscribe(EchoWebSocket);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None).WaitAsync(Timeout);

        await client.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None)
            .WaitAsync(Timeout);

        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask().WaitAsync(Timeout);
        Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, result.Count));

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(Timeout);

        // The same listener still serves plain HTTP afterwards.
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/plain").WaitAsync(Timeout);
        Assert.Equal("plain", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task End_to_end_echo_with_WebsocketClientLite()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);

        using var subscription = tcpListener.ToHttpListenerObservable().Subscribe(EchoWebSocket);

        var echoed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var client = new ClientWebSocketRx();
        using var clientSubscription = client
            .WebsocketConnectWithStatusObservable(new Uri($"ws://127.0.0.1:{port}/ws"))
            .Subscribe(
                tuple =>
                {
                    if (tuple.state == ConnectionStatus.WebsocketConnected)
                    {
                        _ = client.Sender?.SendText("interop", CancellationToken.None);
                    }

                    if (tuple.state == ConnectionStatus.DataframeReceived && tuple.dataframe?.Message is { } message)
                    {
                        echoed.TrySetResult(message);
                    }
                },
                ex => echoed.TrySetException(ex));

        Assert.Equal("interop", await echoed.Task.WaitAsync(Timeout));
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
