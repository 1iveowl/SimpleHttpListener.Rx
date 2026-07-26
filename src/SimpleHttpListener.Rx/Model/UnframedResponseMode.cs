namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// How to frame a response that carries neither <c>Content-Length</c> nor
/// <c>Transfer-Encoding</c> — the one HTTP message shape whose end is ambiguous.
/// </summary>
public enum UnframedResponseMode
{
    /// <summary>
    /// The message ends at the blank line after the headers, and any bytes that follow start
    /// the next message. Correct for SSDP/HTTPU, whose responses are bodyless, and the
    /// default.
    /// </summary>
    CompleteAtHeaders,

    /// <summary>
    /// The body runs to the end of the input, per RFC 9112 §6.3 — the framing an HTTP/1.0
    /// style response that ends by closing the connection needs. Only enable it for a stream
    /// you know carries such responses: it makes every unframed response wait for the end of
    /// input before completing.
    /// </summary>
    CloseDelimited
}
