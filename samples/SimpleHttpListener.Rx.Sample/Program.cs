using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using SimpleHttpListener.Rx;
using SimpleHttpListener.Rx.Model;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var tcpListener = new TcpListener(IPAddress.Loopback, 8088);

using var subscription = tcpListener
    .ToHttpListenerObservable(cts.Token)
    .Subscribe(
        request =>
        {
            Console.WriteLine($"{request.Method} {request.Path} from {request.RemoteEndPoint} " +
                              $"(keep-alive: {request.ShouldKeepAlive}, upgrade: {request.IsUpgradeRequest})");

            if (request.IsUpgradeRequest)
            {
                _ = EchoWebSocketAsync(request);
            }
            else
            {
                // Auto mode: the connection stays open for keep-alive requests and is closed otherwise.
                _ = request.SendResponseAsync(new HttpResponse
                {
                    Headers = { ["Content-Type"] = "text/plain" },
                    Body = "Hello, World"u8.ToArray()
                });
            }
        },
        ex => Console.WriteLine($"Listener error: {ex}"),
        () => Console.WriteLine("Listener completed."));

Console.WriteLine("Listening on http://localhost:8088 (WebSocket echo on ws://localhost:8088) — Ctrl+C to stop.");

// --- SSDP multicast listening (uncomment to try) ---
// var udpClient = new UdpClient();
// udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
// udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
// udpClient.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
//
// // CaptureRawMessage keeps each datagram verbatim — drop it for anything but diagnostics.
// var options = new HttpListenerOptions { CaptureRawMessage = true };
//
// using var ssdpSubscription = udpClient
//     .ToHttpListenerObservable(options, cts.Token, ErrorCorrection.HeaderCompletionError)
//     .Subscribe(message =>
//     {
//         // LocalEndPoint is the interface the datagram arrived on, not the 0.0.0.0 bind.
//         Console.WriteLine($"SSDP {message.Method} from {message.RemoteEndPoint} on {message.LocalEndPoint}");
//         Console.WriteLine(Encoding.ASCII.GetString(message.RawMessage.Span));
//     });

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

static async Task EchoWebSocketAsync(HttpRequestResponse request)
{
    try
    {
        using var webSocket = await request.AcceptWebSocketAsync();
        Console.WriteLine("WebSocket connected — echoing.");

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

        Console.WriteLine("WebSocket closed.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WebSocket error: {ex.Message}");
    }
    finally
    {
        request.Connection?.Dispose();
    }
}

// --- Analyzer demo - uncomment to see the analyzer in action ---
//static class AnalyzerDemo
//{
//    // SHLRX001: Ctrl+. offers two fixes — Concat (one at a time, in order) and Merge.
//    public static void AsyncLambda(IObservable<HttpRequestResponse> requests) =>
//        requests.Subscribe(async request => await request.SendResponseAsync(new HttpResponse()));

//    // SHLRX001 again: the word 'async' appears nowhere at this call site.
//    public static void AsyncMethodGroup(IObservable<HttpRequestResponse> requests) =>
//        requests.Subscribe(HandleAsync);

//    static async void HandleAsync(HttpRequestResponse request) =>
//        await request.SendResponseAsync(new HttpResponse());

//    // SHLRX002: this path abandons the connection.
//    public static void LeakedUpgrade(HttpRequestResponse request)
//    {
//        if (request.IsUpgradeRequest)
//        {
//            return;
//        }

//        _ = request.SendResponseAsync(new HttpResponse());
//    }
//}

