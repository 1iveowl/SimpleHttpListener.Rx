namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// The kind of HTTP message that was parsed.
/// </summary>
public enum MessageType
{
    /// <summary>An HTTP request message.</summary>
    Request,

    /// <summary>An HTTP response message.</summary>
    Response
}
