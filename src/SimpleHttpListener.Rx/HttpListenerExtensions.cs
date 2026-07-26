using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
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
    /// One run gate per listener or socket instance, so subscriptions hand the listener over
    /// even when a caller wraps the same listener in more than one observable. Weak keys: a
    /// collected listener takes its gate with it.
    /// </summary>
    private static readonly ConditionalWeakTable<object, ListenerRunGate> RunGates = new();

    /// <summary>Shared, so the overloads without options allocate nothing extra.</summary>
    private static readonly HttpListenerOptions DefaultOptions = new();

    /// <summary>
    /// Listens for TCP connections and emits every HTTP message received on them.
    /// Connections are handled concurrently, and keep-alive connections emit one message
    /// per request. The listener is started on first subscription and stopped when the last
    /// subscription is disposed (or <paramref name="cancellationToken"/> is cancelled);
    /// re-subscribing restarts it.
    /// </summary>
    /// <remarks>
    /// Stopping is never an error: cancelling, disposing the last subscription, or closing
    /// the listener completes the stream rather than faulting it. Resubscribing immediately
    /// after disposing the last subscription is safe — the new subscription waits for the
    /// previous one to release the listener, so it always gets a working one.
    /// </remarks>
    /// <param name="tcpListener">The listener to accept connections on.</param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="errorCorrections">Opt-in corrections for malformed messages.</param>
    public static IObservable<HttpRequestResponse> ToHttpListenerObservable(
        this TcpListener tcpListener,
        CancellationToken cancellationToken = default,
        params ErrorCorrection[] errorCorrections) =>
        tcpListener.ToHttpListenerObservable(DefaultOptions, cancellationToken, errorCorrections);

    /// <inheritdoc cref="ToHttpListenerObservable(TcpListener, CancellationToken, ErrorCorrection[])"/>
    /// <param name="tcpListener">The listener to accept connections on.</param>
    /// <param name="options">
    /// Listener options. Note that <see cref="HttpListenerOptions.CaptureRawMessage"/> has
    /// no effect here: a TCP message is framed out of a stream, so
    /// <see cref="HttpRequestResponse.RawMessage"/> stays empty for TCP messages.
    /// </param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="errorCorrections">Opt-in corrections for malformed messages.</param>
    public static IObservable<HttpRequestResponse> ToHttpListenerObservable(
        this TcpListener tcpListener,
        HttpListenerOptions options,
        CancellationToken cancellationToken = default,
        params ErrorCorrection[] errorCorrections)
    {
        ArgumentNullException.ThrowIfNull(options);

        var headerCompletionCorrection = errorCorrections.Contains(ErrorCorrection.HeaderCompletionError);

        return AcceptConnections(tcpListener, cancellationToken)
            .SelectMany(connection => HttpMessageParser.ParseConnection(
                connection, headerCompletionCorrection, cancellationToken, options.UnframedResponseMode))
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
    /// Stopping is never an error: cancelling, disposing the last subscription, or disposing
    /// the client completes the stream rather than faulting it, and resubscribing
    /// immediately after disposing the last subscription is safe — the new subscription
    /// waits for the previous one to release the socket.
    /// <para>
    /// <see cref="HttpRequestResponse.LocalEndPoint"/> reports the address the datagram was
    /// actually delivered to rather than the socket's bound address, which matters for the
    /// wildcard bind a multicast socket needs on macOS and Linux. For a multicast or
    /// broadcast datagram the receiving interface is resolved from the packet's interface
    /// index; if that cannot be determined, the bound endpoint is reported instead.
    /// </para>
    /// </remarks>
    /// <param name="udpClient">The client to receive datagrams on.</param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="errorCorrections">Opt-in corrections for malformed messages.</param>
    public static IObservable<HttpRequestResponse> ToHttpListenerObservable(
        this UdpClient udpClient,
        CancellationToken cancellationToken = default,
        params ErrorCorrection[] errorCorrections) =>
        udpClient.ToHttpListenerObservable(DefaultOptions, cancellationToken, errorCorrections);

    /// <inheritdoc cref="ToHttpListenerObservable(UdpClient, CancellationToken, ErrorCorrection[])"/>
    /// <param name="udpClient">The client to receive datagrams on.</param>
    /// <param name="options">
    /// Listener options — set <see cref="HttpListenerOptions.CaptureRawMessage"/> to have
    /// each datagram's bytes carried on <see cref="HttpRequestResponse.RawMessage"/>.
    /// </param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="errorCorrections">Opt-in corrections for malformed messages.</param>
    public static IObservable<HttpRequestResponse> ToHttpListenerObservable(
        this UdpClient udpClient,
        HttpListenerOptions options,
        CancellationToken cancellationToken = default,
        params ErrorCorrection[] errorCorrections)
    {
        ArgumentNullException.ThrowIfNull(options);

        var headerCompletionCorrection = errorCorrections.Contains(ErrorCorrection.HeaderCompletionError);
        var captureRawMessage = options.CaptureRawMessage;

        return Observable.Create<HttpRequestResponse>(async (observer, subscriptionToken) =>
            {
                // Unlike the TCP listener there is nothing to start or stop here, so this
                // run simply waits its turn: two receive loops on one socket would compete
                // for datagrams. Nothing is lost by starting a moment later — the socket
                // buffers what arrives meanwhile.
                using var run = RunGateFor(udpClient).Claim();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(subscriptionToken, cancellationToken);

                if (!await TryTakeOverAsync(run, linkedCts.Token).ConfigureAwait(false))
                {
                    observer.OnCompleted();
                    return;
                }

                using var datagramParser = new DatagramParser(options.UnframedResponseMode);
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
                            result.RemoteEndPoint as IPEndPoint,
                            captureRawMessage));
                    }
                }
                catch (Exception ex) when (IsListenerStopped(ex, linkedCts.Token))
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
        return Observable.Create<TcpConnection>(observer =>
        {
            var run = RunGateFor(tcpListener).Claim();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            try
            {
                // Started synchronously, so the listener is accepting by the time Subscribe
                // returns and a caller may connect straight away.
                tcpListener.Start();
            }
            catch (Exception ex)
            {
                linkedCts.Dispose();
                run.Dispose();
                observer.OnError(ex);

                return Disposable.Empty;
            }

            _ = AcceptLoopAsync(tcpListener, observer, run, linkedCts.Token);

            return Disposable.Create(() =>
            {
                linkedCts.Cancel();

                // Stopping here rather than in the loop makes teardown synchronous: a
                // subscription taken immediately afterwards finds the listener really free,
                // and the loop — still unwinding — can no longer stop the listener that the
                // next subscription has since started.
                run.Release(tcpListener.Stop);
                linkedCts.Dispose();
            });
        });
    }

    private static async Task AcceptLoopAsync(
        TcpListener tcpListener,
        IObserver<TcpConnection> observer,
        ListenerRunGate.Run run,
        CancellationToken token)
    {
        try
        {
            while (true)
            {
                var client = await tcpListener.AcceptTcpClientAsync(token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    // Accepted as the listener was being torn down: nobody is left to own it.
                    client.Dispose();

                    break;
                }

                observer.OnNext(new TcpConnection(client));
            }

            observer.OnCompleted();
        }
        catch (Exception ex) when (IsListenerStopped(ex, token))
        {
            observer.OnCompleted();
        }
        catch (Exception ex)
        {
            observer.OnError(ex);
        }
        finally
        {
            // The loop also ends when the caller's token is cancelled, without a Dispose to
            // stop the listener — so release it here too, if it is still ours.
            run.Release(tcpListener.Stop);
            run.Dispose();
        }
    }

    private static ListenerRunGate RunGateFor(object listener) => RunGates.GetOrCreateValue(listener);

    /// <summary>
    /// Waits for the previous subscription over the same socket to finish, so two receive
    /// loops never compete for datagrams. Returns <see langword="false"/> if this
    /// subscription was disposed while waiting.
    /// </summary>
    private static async Task<bool> TryTakeOverAsync(ListenerRunGate.Run run, CancellationToken token)
    {
        try
        {
            await run.Previous.WaitAsync(token).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an exception out of a pending accept or receive means the listener stopped
    /// rather than failed. Cancelling a socket operation, or closing the socket under it,
    /// surfaces as an <see cref="OperationCanceledException"/> on Windows but as a
    /// <see cref="SocketException"/> ("Operation canceled") or an
    /// <see cref="ObjectDisposedException"/> on Linux and macOS — and a shared, ref-counted
    /// stream must not turn a stop into an error for every subscriber. A socket failure
    /// while the listener is still meant to be running is a genuine error and still
    /// propagates.
    /// </summary>
    private static bool IsListenerStopped(Exception exception, CancellationToken token) =>
        exception switch
        {
            OperationCanceledException or ObjectDisposedException => true,
            SocketException socketException =>
                token.IsCancellationRequested
                || socketException.SocketErrorCode is SocketError.OperationAborted
                    or SocketError.Interrupted
                    or SocketError.Shutdown,
            _ => false
        };
}
