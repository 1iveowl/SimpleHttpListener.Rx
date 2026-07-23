namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// The transport a message was received over.
/// </summary>
public enum HttpTransport
{
    /// <summary>Message was received over a TCP connection.</summary>
    Tcp,

    /// <summary>Message was received as a UDP datagram (e.g. SSDP multicast).</summary>
    Udp
}
