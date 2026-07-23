using System.Net;

namespace SimpleHttpListener.Rx;

/// <summary>
/// An open TCP connection a message was received on. Use <see cref="Stream"/> to write a
/// response (or hand the duplex stream to another protocol after an upgrade), and
/// <see cref="IDisposable.Dispose"/> to close the connection.
/// </summary>
/// <remarks>
/// Ownership: while the listener is reading, it owns the connection and disposes it on
/// client close or error. When a message with <c>ShouldKeepAlive == false</c> is emitted the
/// listener stops reading and ownership passes to the consumer;
/// <see cref="HttpSender.SendResponseAsync(Model.HttpRequestResponse, Model.HttpResponse, bool?, CancellationToken)"/>
/// in auto mode disposes the connection after the response is written.
/// </remarks>
public interface IHttpConnection : IDisposable
{
    /// <summary>Local endpoint of the connection, if available.</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>Remote (client) endpoint of the connection, if available.</summary>
    IPEndPoint? RemoteEndPoint { get; }

    /// <summary>The duplex network stream for the connection.</summary>
    Stream Stream { get; }
}
