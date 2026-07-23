using System.Buffers;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;

namespace SimpleHttpListener.Rx;

/// <summary>
/// Client-side helpers for connecting and streaming over TCP.
/// </summary>
public static class TcpClientExtensions
{
    private const int ReadBufferSize = 8192;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves the host of <paramref name="uri"/> to an IPv4 address and connects.
    /// </summary>
    /// <param name="tcpClient">The client to connect.</param>
    /// <param name="uri">Target host and port.</param>
    /// <param name="timeout">Connect timeout; defaults to 5 seconds.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <exception cref="SimpleHttpListenerException">The host does not resolve to an IPv4 address.</exception>
    /// <exception cref="TimeoutException">The connect did not complete within the timeout.</exception>
    public static async Task ConnectTcpIPv4Async(
        this TcpClient tcpClient,
        Uri uri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var ip = await uri.Host.GetIPv4AddressAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SimpleHttpListenerException($"Unable to resolve '{uri.Host}' to an IPv4 address.");

        await tcpClient.ConnectAsync(ip, uri.Port, cancellationToken).AsTask()
            .WaitAsync(timeout ?? DefaultConnectTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the host of <paramref name="uri"/> to an IPv6 address and connects.
    /// </summary>
    /// <param name="tcpClient">The client to connect.</param>
    /// <param name="uri">Target host and port.</param>
    /// <param name="timeout">Connect timeout; defaults to 5 seconds.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <exception cref="SimpleHttpListenerException">The host does not resolve to an IPv6 address.</exception>
    /// <exception cref="TimeoutException">The connect did not complete within the timeout.</exception>
    public static async Task ConnectTcpIPv6Async(
        this TcpClient tcpClient,
        Uri uri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var ip = await uri.Host.GetIPv6AddressAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SimpleHttpListenerException($"Unable to resolve '{uri.Host}' to an IPv6 address.");

        await tcpClient.ConnectAsync(ip, uri.Port, cancellationToken).AsTask()
            .WaitAsync(timeout ?? DefaultConnectTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> in buffered chunks and emits each chunk as its own
    /// byte array. Completes when the stream reaches end-of-file or the token is cancelled.
    /// </summary>
    public static IObservable<byte[]> ToByteStreamObservable(
        this Stream stream,
        CancellationToken cancellationToken = default)
    {
        return Observable.Create<byte[]>(async (observer, ct) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

            try
            {
                while (true)
                {
                    int bytesRead;

                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, ReadBufferSize), linkedCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    observer.OnNext(buffer.AsSpan(0, bytesRead).ToArray());
                }

                observer.OnCompleted();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>Writes <paramref name="data"/> to the stream.</summary>
    public static async Task SendDatagramAsync(this Stream stream, byte[] data, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="text"/> UTF-8 encoded to the stream.</summary>
    public static async Task SendStringAsync(this Stream stream, string text, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="text"/> followed by <c>CRLF</c>, UTF-8 encoded, to the stream.</summary>
    public static async Task SendStringLineAsync(this Stream stream, string text, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"{text}\r\n"), cancellationToken).ConfigureAwait(false);
    }
}
