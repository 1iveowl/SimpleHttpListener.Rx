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
    private static IDisposable RespondWithHelloWorld(
        IObservable<HttpRequestResponse> listener,
        ConcurrentQueue<HttpRequestResponse> emissions)
    {
        return listener.Subscribe(request =>
        {
            emissions.Enqueue(request);
            TestNetwork.SendHelloWorld(request);
        });
    }

    // 19
    [Fact]
    public async Task Tcp_end_to_end_round_trip()
    {
        var port = TestNetwork.GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        using var subscription = RespondWithHelloWorld(tcpListener.ToHttpListenerObservable(), emissions);
        using var httpClient = new HttpClient();

        var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/hello").WaitAsync(TestNetwork.Timeout);

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
        var port = TestNetwork.GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        using var subscription = RespondWithHelloWorld(tcpListener.ToHttpListenerObservable(), emissions);
        using var httpClient = new HttpClient();

        var first = await httpClient.GetAsync($"http://127.0.0.1:{port}/one").WaitAsync(TestNetwork.Timeout);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await httpClient.GetAsync($"http://127.0.0.1:{port}/two").WaitAsync(TestNetwork.Timeout);
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
        var port = TestNetwork.GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        using var subscription = RespondWithHelloWorld(tcpListener.ToHttpListenerObservable(), emissions);

        // Park a connection that never sends a byte.
        using var idleClient = new TcpClient();
        await idleClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TestNetwork.Timeout);

        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/active").WaitAsync(TestNetwork.Timeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(emissions);
        Assert.Equal("/active", request.Path);
    }

    // 22
    [Fact]
    public async Task Disposing_subscription_stops_listener_and_resubscribe_restarts_it()
    {
        var port = TestNetwork.GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var listenerObservable = tcpListener.ToHttpListenerObservable();
        var emissions = new ConcurrentQueue<HttpRequestResponse>();

        var subscription = RespondWithHelloWorld(listenerObservable, emissions);

        using (var httpClient = new HttpClient())
        {
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/first").WaitAsync(TestNetwork.Timeout);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Teardown is synchronous: the listener is stopped by the time Dispose returns.
        subscription.Dispose();

        using (var refusedClient = new TcpClient())
        {
            await Assert.ThrowsAnyAsync<SocketException>(
                () => refusedClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TestNetwork.Timeout));
        }

        using var resubscription = RespondWithHelloWorld(listenerObservable, emissions);
        using var secondClient = new HttpClient();

        var secondResponse = await secondClient.GetAsync($"http://127.0.0.1:{port}/second").WaitAsync(TestNetwork.Timeout);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, emissions.Count);
    }

    // 23
    [Fact]
    public async Task Udp_end_to_end_parses_datagram()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = receiver.LocalPort();

        var firstMessage = receiver.ToHttpListenerObservable()
            .FirstAsync()
            .ToTask();

        var datagram = TestNetwork.SsdpNotify();

        using var sender = new UdpClient();
        await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TestNetwork.Timeout);

        var message = await firstMessage.WaitAsync(TestNetwork.Timeout);

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
            .WaitAsync(TestNetwork.Timeout);

        Assert.Equal([7, 7, 6], chunks.Select(c => c.Length));
        Assert.Equal(payload, chunks.SelectMany(c => c).ToArray());
    }
}
