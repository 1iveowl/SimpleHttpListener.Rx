using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Linq;
using SimpleHttpListener.Rx.Model;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

/// <summary>
/// Stopping a listener must complete the stream rather than fault it, and disposing the last
/// subscription must not race an immediate resubscription over the same listener.
/// </summary>
public class SubscriptionLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] Datagram =
        "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: upnp:rootdevice\r\n\r\n"u8.ToArray();

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Records the terminal notification so a test can tell "stopped" from "failed".</summary>
    private sealed class Termination
    {
        private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Exception> Errors { get; } = new();

        public Task Terminated => _terminated.Task;

        public IDisposable Subscribe<T>(IObservable<T> source, Action<T>? onNext = null) =>
            source.Subscribe(
                value => onNext?.Invoke(value),
                error =>
                {
                    Errors.Enqueue(error);
                    _terminated.TrySetResult();
                },
                () => _terminated.TrySetResult());
    }

    private static void RespondWithHelloWorld(HttpRequestResponse request) =>
        _ = request.SendResponseAsync(new HttpResponse { Body = "Hello, World"u8.ToArray() });

    [Fact]
    public async Task Tcp_cancellation_while_accept_pending_completes_without_error()
    {
        using var cts = new CancellationTokenSource();
        var tcpListener = new TcpListener(IPAddress.Loopback, GetFreePort());
        var termination = new Termination();

        using var subscription = termination.Subscribe(tcpListener.ToHttpListenerObservable(cts.Token));

        // Let the accept loop reach its pending accept before pulling the rug out.
        await WaitUntilListeningAsync(tcpListener);

        await cts.CancelAsync();

        await termination.Terminated.WaitAsync(Timeout);
        Assert.Empty(termination.Errors);
    }

    [Fact]
    public async Task Tcp_listener_stopped_under_pending_accept_completes_without_error()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, GetFreePort());
        var termination = new Termination();

        using var subscription = termination.Subscribe(tcpListener.ToHttpListenerObservable());

        await WaitUntilListeningAsync(tcpListener);

        // Closing the socket under a pending accept surfaces as SocketException or
        // ObjectDisposedException rather than OperationCanceledException on Linux/macOS.
        tcpListener.Stop();

        await termination.Terminated.WaitAsync(Timeout);
        Assert.Empty(termination.Errors);
    }

    [Fact]
    public async Task Tcp_dispose_then_immediate_resubscribe_keeps_working()
    {
        var port = GetFreePort();
        var tcpListener = new TcpListener(IPAddress.Loopback, port);
        var listener = tcpListener.ToHttpListenerObservable();
        var errors = new ConcurrentQueue<Exception>();

        for (var generation = 0; generation < 5; generation++)
        {
            var requests = new ConcurrentQueue<HttpRequestResponse>();

            var subscription = listener.Subscribe(
                request =>
                {
                    requests.Enqueue(request);
                    RespondWithHelloWorld(request);
                },
                errors.Enqueue);

            using (var httpClient = new HttpClient())
            {
                var response = await httpClient
                    .GetAsync($"http://127.0.0.1:{port}/generation{generation}")
                    .WaitAsync(Timeout);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("Hello, World", await response.Content.ReadAsStringAsync());
            }

            Assert.Equal($"/generation{generation}", Assert.Single(requests).Path);

            // No delay before the next iteration subscribes: the restart must not race this
            // teardown.
            subscription.Dispose();
        }

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Udp_cancellation_while_receive_pending_completes_without_error()
    {
        using var cts = new CancellationTokenSource();
        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var termination = new Termination();

        using var subscription = termination.Subscribe(udpClient.ToHttpListenerObservable(cts.Token));

        await cts.CancelAsync();

        await termination.Terminated.WaitAsync(Timeout);
        Assert.Empty(termination.Errors);
    }

    [Fact]
    public async Task Udp_client_disposed_under_pending_receive_completes_without_error()
    {
        var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var termination = new Termination();

        using var subscription = termination.Subscribe(udpClient.ToHttpListenerObservable());

        // Give the receive loop a moment to reach its pending receive.
        await Task.Delay(100);

        udpClient.Dispose();

        await termination.Terminated.WaitAsync(Timeout);
        Assert.Empty(termination.Errors);
    }

    [Fact]
    public async Task Udp_dispose_then_immediate_resubscribe_keeps_working()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var listener = receiver.ToHttpListenerObservable();
        var errors = new ConcurrentQueue<Exception>();

        using var sender = new UdpClient();

        for (var generation = 0; generation < 5; generation++)
        {
            var received = new TaskCompletionSource<HttpRequestResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var subscription = listener.Subscribe(
                message => received.TrySetResult(message),
                errors.Enqueue);

            // The datagram is only sent once the new receive loop is up, so a lost datagram
            // (rather than a faulted stream) still fails this test via the timeout.
            await SendUntilReceivedAsync(sender, port, received.Task);

            var message = await received.Task.WaitAsync(Timeout);
            Assert.Equal("NOTIFY", message.Method);

            subscription.Dispose();
        }

        Assert.Empty(errors);
    }

    private static async Task WaitUntilListeningAsync(TcpListener tcpListener)
    {
        // The accept loop starts the listener asynchronously on subscription.
        for (var attempt = 0; attempt < 100 && !tcpListener.Server.IsBound; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(tcpListener.Server.IsBound);

        // Bound is not yet listening; give the loop a moment to reach its pending accept.
        await Task.Delay(100);
    }

    private static async Task SendUntilReceivedAsync(UdpClient sender, int port, Task received)
    {
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        // UDP is lossy by contract and the receive loop starts asynchronously, so resend
        // until the subscriber sees one rather than assuming the first datagram lands.
        for (var attempt = 0; attempt < 100 && !received.IsCompleted; attempt++)
        {
            await sender.SendAsync(Datagram, destination).AsTask().WaitAsync(Timeout);
            await Task.WhenAny(received, Task.Delay(50));
        }
    }
}
