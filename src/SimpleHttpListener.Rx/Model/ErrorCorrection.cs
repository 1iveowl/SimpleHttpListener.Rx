namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// Opt-in corrections applied to malformed messages before parsing is finalized.
/// </summary>
public enum ErrorCorrection
{
    /// <summary>
    /// If a message ends (datagram fully read, or TCP connection closed) before its header
    /// section was terminated with an empty line, feed a trailing <c>CRLF CRLF</c> so the
    /// headers received so far still parse. Some SSDP/UPnP devices omit the terminating
    /// empty line; enable this to accept their notifications.
    /// </summary>
    HeaderCompletionError
}
