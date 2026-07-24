using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx;

/// <summary>
/// Accepts incoming WebSocket connections on messages emitted by the listener observables.
/// </summary>
public static class WebSocketExtensions
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// Completes the WebSocket handshake for an upgrade request (RFC 6455 §4.2): sends
    /// <c>101 Switching Protocols</c> with the computed <c>Sec-WebSocket-Accept</c> and
    /// returns a server-side <see cref="WebSocket"/> over the connection's stream — all
    /// framing is handled by the runtime.
    /// </summary>
    /// <param name="request">
    /// A request with <see cref="HttpRequestResponse.IsUpgradeRequest"/> set. The listener
    /// has already stopped reading the connection; after this call the consumer owns both
    /// the returned socket and <see cref="HttpRequestResponse.Connection"/>, and should
    /// dispose the connection when finished.
    /// </param>
    /// <param name="subProtocol">
    /// Sub-protocol to confirm to the client (sent as <c>Sec-WebSocket-Protocol</c>);
    /// pass-through only, no negotiation is performed.
    /// </param>
    /// <param name="keepAliveInterval">
    /// Ping interval for the returned socket; defaults to
    /// <see cref="WebSocket.DefaultKeepAliveInterval"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels sending the handshake response.</param>
    /// <exception cref="InvalidOperationException">
    /// The message is not a valid WebSocket upgrade request (wrong transport, missing
    /// <c>Upgrade: websocket</c>, unsupported version, or missing key). The caller decides
    /// whether to answer with an error response and must dispose the connection.
    /// </exception>
    public static async Task<WebSocket> AcceptWebSocketAsync(
        this HttpRequestResponse request,
        string? subProtocol = null,
        TimeSpan? keepAliveInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Connection is not { } connection)
        {
            throw new InvalidOperationException(
                "The request has no connection to upgrade. WebSocket upgrades require a TCP message.");
        }

        if (!request.IsUpgradeRequest)
        {
            throw new InvalidOperationException("The request is not a protocol upgrade request.");
        }

        if (!request.Headers.TryGetValue("UPGRADE", out var upgrade)
            || !upgrade.Trim().Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The request does not ask to upgrade to the websocket protocol.");
        }

        if (!request.Headers.TryGetValue("SEC-WEBSOCKET-VERSION", out var version)
            || version.Trim() != "13")
        {
            throw new InvalidOperationException("Only WebSocket protocol version 13 is supported.");
        }

        if (!request.Headers.TryGetValue("SEC-WEBSOCKET-KEY", out var key)
            || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("The request has no Sec-WebSocket-Key header.");
        }

        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));

        var response = new HttpResponse
        {
            StatusCode = 101,
            Headers =
            {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Accept"] = acceptKey
            }
        };

        if (subProtocol is not null)
        {
            response.Headers["Sec-WebSocket-Protocol"] = subProtocol;
        }

        await connection.SendResponseAsync(response, closeConnection: false, cancellationToken)
            .ConfigureAwait(false);

        return WebSocket.CreateFromStream(connection.Stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = subProtocol,
            KeepAliveInterval = keepAliveInterval ?? WebSocket.DefaultKeepAliveInterval
        });
    }
}
