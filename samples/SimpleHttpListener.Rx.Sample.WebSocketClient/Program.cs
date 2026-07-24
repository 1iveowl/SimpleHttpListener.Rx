// WebSocket client demo using WebsocketClientLite.PCL — the client-side companion to the
// SimpleHttpListener.Rx.Sample server. Start the server sample first, then run this.
using IWebsocketClientLite;
using WebsocketClientLite;

var uri = new Uri(args.Length > 0 ? args[0] : "ws://localhost:8088/ws");
Console.WriteLine($"Connecting to {uri} (start SimpleHttpListener.Rx.Sample first)…");

using var client = new ClientWebSocketRx();

using var subscription = client
    .WebsocketConnectWithStatusObservable(uri)
    .Subscribe(
        tuple =>
        {
            switch (tuple.state)
            {
                case ConnectionStatus.WebsocketConnected:
                    Console.WriteLine("Connected — sending messages…");
                    _ = SendGreetingsAsync(client);
                    break;

                case ConnectionStatus.DataframeReceived when tuple.dataframe?.Message is { } message:
                    Console.WriteLine($"Echo: {message}");
                    break;

                case ConnectionStatus.Disconnected:
                    Console.WriteLine("Disconnected.");
                    break;
            }
        },
        ex => Console.WriteLine($"Error: {ex.Message}"),
        () => Console.WriteLine("Completed."));

await Task.Delay(TimeSpan.FromSeconds(5));

static async Task SendGreetingsAsync(ClientWebSocketRx client)
{
    foreach (var message in new[] { "Hello", "from", "WebsocketClientLite!" })
    {
        if (client.Sender is { } sender)
        {
            await sender.SendText(message, CancellationToken.None);
        }

        await Task.Delay(200);
    }
}
