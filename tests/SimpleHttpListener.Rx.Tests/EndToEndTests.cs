using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

public class EndToEndTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static IDisposable RespondWithHelloWorld(
        IObservable<HttpRequestResponse> listener,
        ConcurrentQueue<HttpRequestResponse> emissions)
    {
        return listener.Subscribe(request =>
        {
            emissions.Enqueue(request);
            _ = request.SendResponseAsync(new HttpResponse
            {
                Headers = { ["Content-Type"] = "text/plain" },
                Body = "Hello, World"u8.ToArray()
            });
        });
    }

    // 19
    [Fact]
    public async Task Tcp_end_to_end_round_trip()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        using var subscription = RespondWithHelloWorld(tcpListener.ToHttpListenerObservable(), emissions);
        using var httpClient = new HttpClient();

        var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/hello").WaitAsync(Timeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello, World", await response.Content.ReadAsStringAsync());

        var request = Assert.Single(emissions);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/hello", request.Path);
        Assert.NotNull(request.RemoteEndPoint);
        Assert.Equal(HttpTransport.Tcp, request.Transport);
    }

    // 20
    [Fact]
    public async Task Keep_alive_serves_two_requests_on_one_connection()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        using var subscription = RespondWithHelloWorld(tcpListener.ToHttpListenerObservable(), emissions);
        using var httpClient = new HttpClient();

        var first = await httpClient.GetAsync($"http://127.0.0.1:{port}/one").WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await httpClient.GetAsync($"http://127.0.0.1:{port}/two").WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(2, emissions.Count);
        var requests = emissions.ToArray();
        Assert.Equal("/one", requests[0].Path);
        Assert.Equal("/two", requests[1].Path);

        // Same client port on both emissions proves the connection was reused.
        Assert.Equal(requests[0].RemoteEndPoint!.Port, requests[1].RemoteEndPoint!.Port);
    }

    // 21
    [Fact]
    public async Task Idle_connection_does_not_starve_other_clients()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        using var subscription = RespondWithHelloWorld(tcpListener.ToHttpListenerObservable(), emissions);

        // Park a connection that never sends a byte.
        using var idleClient = new TcpClient();
        await idleClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);

        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/active").WaitAsync(Timeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(emissions);
        Assert.Equal("/active", request.Path);
    }

    // 22
    [Fact]
    public async Task Disposing_subscription_stops_listener_and_resubscribe_restarts_it()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var listenerObservable = tcpListener.ToHttpListenerObservable();
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        var subscription = RespondWithHelloWorld(listenerObservable, emissions);

        using (var httpClient = new HttpClient())
        {
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/first").WaitAsync(Timeout);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        subscription.Dispose();

        // Give the accept loop time to observe cancellation and stop the listener.
        await Task.Delay(250);

        using (var refusedClient = new TcpClient())
        {
            await Assert.ThrowsAnyAsync<SocketException>(
                () => refusedClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout));
        }

        using var resubscription = RespondWithHelloWorld(listenerObservable, emissions);
        using var secondClient = new HttpClient();

        var secondResponse = await secondClient.GetAsync($"http://127.0.0.1:{port}/second").WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, emissions.Count);
    }

    // 23
    [Fact]
    public async Task Udp_end_to_end_parses_datagram()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        var firstMessage = receiver.ToHttpListenerObservable()
            .FirstAsync()
            .ToTask();

        var datagram = "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: upnp:rootdevice\r\n\r\n"u8.ToArray();

        using var sender = new UdpClient();
        await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(Timeout);

        var message = await firstMessage.WaitAsync(Timeout);

        Assert.Equal("NOTIFY", message.Method);
        Assert.Equal(HttpTransport.Udp, message.Transport);
        Assert.Equal("upnp:rootdevice", message.Headers["NT"]);
        Assert.NotNull(message.RemoteEndPoint);
        Assert.Null(message.Connection);
    }

    // 24
    [Fact]
    public async Task Byte_stream_observable_emits_chunks_and_completes_on_eof()
    {
        var payload = Encoding.ASCII.GetBytes("this is twenty bytes");
        var stream = new DribbleStream(payload, 7);

        var chunks = await stream.ToByteStreamObservable()
            .ToList()
            .ToTask()
            .WaitAsync(Timeout);

        Assert.Equal([7, 7, 6], chunks.Select(c => c.Length));
        Assert.Equal(payload, chunks.SelectMany(c => c).ToArray());
    }
}
