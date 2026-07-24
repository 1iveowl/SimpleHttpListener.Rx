using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using SimpleHttpListener.Rx.Internal;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx;

/// <summary>
/// Turns a <see cref="TcpListener"/> or <see cref="UdpClient"/> into an observable stream
/// of parsed HTTP messages.
/// </summary>
public static class HttpListenerExtensions
{
    /// <summary>Largest payload a UDP datagram can carry, so nothing is ever truncated.</summary>
    private const int MaxDatagramSize = 65507;

    /// <summary>
    /// Listens for TCP connections and emits every HTTP message received on them.
    /// Connections are handled concurrently, and keep-alive connections emit one message
    /// per request. The listener is started on first subscription and stopped when the last
    /// subscription is disposed (or <paramref name="cancellationToken"/> is cancelled);
    /// re-subscribing restarts it.
    /// </summary>
    /// <param name="tcpListener">The listener to accept connections on.</param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="errorCorrections">Opt-in corrections for malformed messages.</param>
    public static IObservable<HttpRequestResponse> ToHttpListenerObservable(
        this TcpListener tcpListener,
        CancellationToken cancellationToken = default,
        params ErrorCorrection[] errorCorrections)
    {
        var headerCompletionCorrection = errorCorrections.Contains(ErrorCorrection.HeaderCompletionError);

        return AcceptConnections(tcpListener, cancellationToken)
            .SelectMany(connection =>
                HttpMessageParser.ParseConnection(connection, headerCompletionCorrection, cancellationToken))
            .Publish()
            .RefCount();
    }

    /// <summary>
    /// Receives UDP datagrams (e.g. SSDP multicast) and emits each one parsed as a complete
    /// HTTP message, with <see cref="HttpRequestResponse.Connection"/> set to
    /// <see langword="null"/>. Receiving starts on first subscription and stops when the
    /// last subscription is disposed (or <paramref name="cancellationToken"/> is cancelled).
    /// </summary>
    /// <remarks>
    /// <see cref="HttpRequestResponse.LocalEndPoint"/> reports the address the datagram was
    /// actually delivered to rather than the socket's bound address, which matters for the
    /// wildcard bind a multicast socket needs on macOS and Linux. For a multicast or
    /// broadcast datagram the receiving interface is resolved from the packet's interface
    /// index; if that cannot be determined, the bound endpoint is reported instead.
    /// </remarks>
    /// <param name="udpClient">The client to receive datagrams on.</param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="errorCorrections">Opt-in corrections for malformed messages.</param>
    public static IObservable<HttpRequestResponse> ToHttpListenerObservable(
        this UdpClient udpClient,
        CancellationToken cancellationToken = default,
        params ErrorCorrection[] errorCorrections)
    {
        var headerCompletionCorrection = errorCorrections.Contains(ErrorCorrection.HeaderCompletionError);

        return Observable.Create<HttpRequestResponse>(async (observer, subscriptionToken) =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(subscriptionToken, cancellationToken);
                using var datagramParser = new DatagramParser();
                using var localEndPointResolver = new UdpLocalEndPointResolver();

                var socket = udpClient.Client;

                UdpLocalEndPointResolver.TryEnablePacketInformation(socket);

                // ReceiveMessageFromAsync, unlike ReceiveAsync, reports which address and
                // interface the datagram arrived on.
                var senderTemplate = new IPEndPoint(
                    socket.AddressFamily is AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

                var buffer = ArrayPool<byte>.Shared.Rent(MaxDatagramSize);
                IPEndPoint? boundEndPoint = null;

                try
                {
                    while (true)
                    {
                        var result = await socket
                            .ReceiveMessageFromAsync(buffer, SocketFlags.None, senderTemplate, linkedCts.Token)
                            .ConfigureAwait(false);

                        // Reading the socket's bound endpoint costs a syscall, and it cannot
                        // change once bound — but the socket may only get bound by the first
                        // receive, so take it here rather than before the loop.
                        boundEndPoint ??= socket.LocalEndPoint as IPEndPoint;

                        observer.OnNext(datagramParser.Parse(
                            buffer.AsSpan(0, result.ReceivedBytes),
                            headerCompletionCorrection,
                            localEndPointResolver.Resolve(result.PacketInformation, boundEndPoint),
                            result.RemoteEndPoint as IPEndPoint));
                    }
                }
                catch (OperationCanceledException)
                {
                    observer.OnCompleted();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            })
            .Publish()
            .RefCount();
    }

    private static IObservable<TcpConnection> AcceptConnections(
        TcpListener tcpListener,
        CancellationToken externalToken)
    {
        return Observable.Create<TcpConnection>(async (observer, subscriptionToken) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(subscriptionToken, externalToken);

            tcpListener.Start();

            try
            {
                while (true)
                {
                    var client = await tcpListener.AcceptTcpClientAsync(linkedCts.Token).ConfigureAwait(false);
                    observer.OnNext(new TcpConnection(client));
                }
            }
            catch (OperationCanceledException)
            {
                observer.OnCompleted();
            }
            finally
            {
                tcpListener.Stop();
            }
        });
    }
}
