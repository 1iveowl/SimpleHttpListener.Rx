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

                try
                {
                    while (true)
                    {
                        var result = await udpClient.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);

                        observer.OnNext(HttpMessageParser.ParseDatagram(
                            result.Buffer,
                            headerCompletionCorrection,
                            udpClient.Client.LocalEndPoint as IPEndPoint,
                            result.RemoteEndPoint));
                    }
                }
                catch (OperationCanceledException)
                {
                    observer.OnCompleted();
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
