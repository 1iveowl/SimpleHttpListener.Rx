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
// using var ssdpSubscription = udpClient
//     .ToHttpListenerObservable(cts.Token, ErrorCorrection.HeaderCompletionError)
//     .Subscribe(message =>
//         Console.WriteLine($"SSDP {message.Method} from {message.RemoteEndPoint}"));

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
