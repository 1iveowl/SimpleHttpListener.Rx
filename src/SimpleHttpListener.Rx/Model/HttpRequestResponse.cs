using System.Net;
using System.Runtime.CompilerServices;

namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// One parsed HTTP message (request or response) emitted by the listener observables.
/// Immutable snapshot; a keep-alive TCP connection produces one instance per message.
/// </summary>
/// <remarks>
/// A record, so <c>with</c> expressions work for derived copies. Equality is
/// reference-based (not member-based): a message carries a live <see cref="Connection"/>
/// and a <see cref="Body"/> buffer, so value comparison would be misleading.
/// </remarks>
public sealed record HttpRequestResponse
{
    /// <summary>Whether this message is a request or a response.</summary>
    public MessageType MessageType { get; init; }

    /// <summary>The transport the message arrived over.</summary>
    public HttpTransport Transport { get; init; }

    /// <summary>Request method (e.g. <c>GET</c>). <see langword="null"/> for responses.</summary>
    public string? Method { get; init; }

    /// <summary>Full request target as received. <see langword="null"/> for responses.</summary>
    public string? RequestUri { get; init; }

    /// <summary>Path component of the request target. <see langword="null"/> for responses.</summary>
    public string? Path { get; init; }

    /// <summary>Query string of the request target (without <c>?</c>). <see langword="null"/> if absent.</summary>
    public string? QueryString { get; init; }

    /// <summary>Fragment of the request target (without <c>#</c>). <see langword="null"/> if absent.</summary>
    public string? Fragment { get; init; }

    /// <summary>Response status code. <c>0</c> for requests.</summary>
    public int StatusCode { get; init; }

    /// <summary>Response reason phrase. <see langword="null"/> for requests.</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>HTTP major version.</summary>
    public int MajorVersion { get; init; }

    /// <summary>HTTP minor version.</summary>
    public int MinorVersion { get; init; }

    /// <summary>
    /// Whether HTTP semantics (version and <c>Connection</c> header) call for keeping the
    /// connection open after this message. When <see langword="false"/> for a TCP message,
    /// the listener stops reading the connection and hands ownership to the consumer.
    /// </summary>
    public bool ShouldKeepAlive { get; init; }

    /// <summary>Whether the body used chunked transfer encoding.</summary>
    public bool IsChunked { get; init; }

    /// <summary>
    /// Message headers. Keys are UPPERCASE and compared case-insensitively; repeated
    /// headers are comma-joined per RFC 9110 §5.2.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Message body; empty if the message has none.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Whether the message was fully parsed to its end.</summary>
    public bool IsEndOfMessage { get; init; }

    /// <summary>
    /// Whether parsing failed (malformed input, or the peer closed mid-message). When set,
    /// the other members describe whatever partial state was available.
    /// </summary>
    public bool HasParsingErrors { get; init; }

    /// <summary>Local endpoint the message was received on, if available.</summary>
    public IPEndPoint? LocalEndPoint { get; init; }

    /// <summary>Remote endpoint the message was sent from, if available.</summary>
    public IPEndPoint? RemoteEndPoint { get; init; }

    /// <summary>
    /// The TCP connection the message arrived on; <see langword="null"/> for UDP messages.
    /// See <see cref="IHttpConnection"/> for the ownership contract.
    /// </summary>
    public IHttpConnection? Connection { get; init; }

    /// <summary>
    /// Whether this is a request asking for a protocol upgrade (<c>Connection: Upgrade</c>
    /// plus an <c>Upgrade</c> header, e.g. a WebSocket handshake). When set for a TCP
    /// message, the listener has stopped reading the connection and ownership has passed to
    /// the consumer — complete the upgrade (e.g. via
    /// <see cref="WebSocketExtensions.AcceptWebSocketAsync"/>) or dispose the connection.
    /// </summary>
    public bool IsUpgradeRequest { get; init; }

    /// <summary>Reference equality; see the class remarks.</summary>
    public bool Equals(HttpRequestResponse? other) => ReferenceEquals(this, other);

    /// <summary>Identity-based hash, consistent with reference equality.</summary>
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
