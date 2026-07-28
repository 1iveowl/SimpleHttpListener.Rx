namespace SimpleHttpListener.Rx.Analyzers.Tests.TestHelpers;

/// <summary>
/// A stand-in for the listener's public surface, compiled into the test compilations that
/// exercise SHLRX002.
/// </summary>
/// <remarks>
/// The real assembly targets net10.0 and the analyzer test harness tops out at net9.0
/// reference assemblies, so it cannot be referenced from a test compilation directly.
/// <see cref="ListenerApiGuardTests"/> checks this stub against the real type by reflection,
/// so a rename in the library cannot leave the analyzer quietly matching nothing.
/// </remarks>
internal static class ListenerStub
{
    internal const string RequestTypeMetadataName = "SimpleHttpListener.Rx.Model.HttpRequestResponse";
    internal const string UpgradePropertyName = "IsUpgradeRequest";
    internal const string ConnectionPropertyName = "Connection";
    internal const string AcceptWebSocketMethodName = "AcceptWebSocketAsync";
    internal const string SendResponseMethodName = "SendResponseAsync";

    internal const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace SimpleHttpListener.Rx
        {
            public interface IHttpConnection : IDisposable
            {
                System.IO.Stream Stream { get; }
            }

            public static class HttpSender
            {
                public static Task SendResponseAsync(
                    this Model.HttpRequestResponse request,
                    Model.HttpResponse response,
                    bool? closeConnection = null,
                    CancellationToken cancellationToken = default) => Task.CompletedTask;
            }

            public static class WebSocketExtensions
            {
                public static Task<object> AcceptWebSocketAsync(
                    this Model.HttpRequestResponse request,
                    string? subProtocol = null,
                    CancellationToken cancellationToken = default) => Task.FromResult(new object());
            }
        }

        namespace SimpleHttpListener.Rx.Model
        {
            public sealed record HttpResponse
            {
                public int StatusCode { get; init; }
            }

            public sealed record HttpRequestResponse
            {
                public string? Path { get; init; }

                public string? Method { get; init; }

                public bool ShouldKeepAlive { get; init; }

                public bool IsUpgradeRequest { get; init; }

                public IHttpConnection? Connection { get; init; }
            }
        }
        """;
}
