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
}
