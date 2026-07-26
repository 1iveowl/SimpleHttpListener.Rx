namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// Options for the listener observables. Defaults match the overloads that do not take
/// options, so enabling nothing changes nothing.
/// </summary>
public sealed record HttpListenerOptions
{
    /// <summary>
    /// Capture each message's bytes exactly as received into
    /// <see cref="HttpRequestResponse.RawMessage"/>. Off by default.
    /// </summary>
    /// <remarks>
    /// Intended for diagnostics — a wire log, or an accurate bug report against a parser —
    /// and it copies every message, so it is not free on a busy SSDP network. Capture is
    /// observational: it changes no parsed value, no framing and no emission.
    /// <para>
    /// Applies to UDP listeners. A TCP message is framed out of a stream and may span
    /// reads, so attributing bytes to it would only be approximate;
    /// <see cref="HttpRequestResponse.RawMessage"/> stays empty for TCP messages.
    /// </para>
    /// <para>
    /// The captured bytes live as long as the consumer keeps the message: retaining every
    /// message on a chatty network accumulates memory, so long-running capture belongs in a
    /// file log rather than in memory.
    /// </para>
    /// </remarks>
    public bool CaptureRawMessage { get; init; }

    /// <summary>
    /// How to frame a response that carries neither <c>Content-Length</c> nor
    /// <c>Transfer-Encoding</c>. Defaults to
    /// <see cref="Model.UnframedResponseMode.CompleteAtHeaders"/>, which is what SSDP needs
    /// and what every earlier version did.
    /// </summary>
    /// <remarks>
    /// Only worth changing for a stream that carries HTTP/1.0 style responses whose body
    /// ends when the connection closes: under the default those bodies are not read, and the
    /// bytes are taken as the start of the next message. Requests are unaffected either way
    /// — a request without framing headers has no body (RFC 9112 §6).
    /// </remarks>
    public UnframedResponseMode UnframedResponseMode { get; init; }
}
