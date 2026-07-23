using System.Buffers;
using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using HttpMachine;
using SimpleHttpListener.Rx.Model;
using UpstreamMessage = IHttpMachine.Model.HttpRequestResponse;

namespace SimpleHttpListener.Rx.Internal;

internal static class HttpMessageParser
{
    private const int ReadBufferSize = 8192;

    /// <summary>
    /// Reads a TCP connection and emits one <see cref="HttpRequestResponse"/> per parsed
    /// message (0..n per connection under keep-alive). Never errors the observable: parse
    /// and I/O failures surface as a message with
    /// <see cref="HttpRequestResponse.HasParsingErrors"/> so one bad connection cannot tear
    /// down a shared listener.
    /// </summary>
    internal static IObservable<HttpRequestResponse> ParseConnection(
        IHttpConnection connection,
        bool headerCompletionCorrection,
        CancellationToken externalToken)
    {
        return Observable.Create<HttpRequestResponse>(async (observer, subscriptionToken) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(subscriptionToken, externalToken);

            using var parserDelegate = new ListenerParserDelegate();
            using var parser = new HttpCombinedParser(parserDelegate);
            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            var handedOff = false;

            HttpRequestResponse BuildIncompleteMessage() =>
                BuildIncomplete(parserDelegate, parser, HttpTransport.Tcp, connection);

            try
            {
                while (!linkedCts.IsCancellationRequested)
                {
                    int bytesRead;

                    try
                    {
                        bytesRead = await connection.Stream
                            .ReadAsync(buffer.AsMemory(0, ReadBufferSize), linkedCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        // ObjectDisposedException means the consumer closed the connection —
                        // a normal end, not a parse failure.
                        if (ex is not ObjectDisposedException && parserDelegate.HasIncompleteMessage)
                        {
                            observer.OnNext(BuildIncompleteMessage());
                        }

                        break;
                    }

                    if (bytesRead == 0)
                    {
                        if (parserDelegate.HasIncompleteMessage)
                        {
                            if (headerCompletionCorrection && !parserDelegate.AreHeadersComplete)
                            {
                                TryExecute(parser, "\r\n\r\n"u8);
                            }

                            TryExecute(parser, ReadOnlySpan<byte>.Empty);

                            DrainCompleted(parserDelegate, observer.OnNext, connection, out _);

                            if (parserDelegate.HasIncompleteMessage)
                            {
                                observer.OnNext(BuildIncompleteMessage());
                            }
                        }

                        break;
                    }

                    var consumed = TryExecute(parser, buffer.AsSpan(0, bytesRead));

                    // The parser treats a non-keep-alive request without Content-Length or
                    // Transfer-Encoding as close-delimited and waits for EOF; per RFC 9112
                    // such a request has no body, so complete it now.
                    if (consumed == bytesRead
                        && parserDelegate is { HasIncompleteMessage: true, AreHeadersComplete: true, IsRequest: true, HasBodyFramingHeader: false })
                    {
                        TryExecute(parser, ReadOnlySpan<byte>.Empty);
                    }

                    DrainCompleted(parserDelegate, observer.OnNext, connection, out var lastMessageClosed);

                    if (consumed != bytesRead)
                    {
                        observer.OnNext(BuildIncompleteMessage());
                        break;
                    }

                    if (lastMessageClosed)
                    {
                        // The consumer now owns the connection; SendResponseAsync in auto
                        // mode disposes it after replying.
                        handedOff = true;
                        break;
                    }
                }

                observer.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                observer.OnCompleted();
            }
            catch (Exception)
            {
                // A single connection must never tear down the shared listener stream.
                observer.OnCompleted();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                if (!handedOff)
                {
                    connection.Dispose();
                }
            }
        });
    }

    /// <summary>
    /// Parses one UDP datagram as a complete HTTP message.
    /// </summary>
    internal static HttpRequestResponse ParseDatagram(
        byte[] datagram,
        bool headerCompletionCorrection,
        IPEndPoint? localEndPoint,
        IPEndPoint? remoteEndPoint)
    {
        using var parserDelegate = new ListenerParserDelegate();
        using var parser = new HttpCombinedParser(parserDelegate);

        var consumed = TryExecute(parser, datagram);
        var hasError = consumed != datagram.Length;

        if (!hasError && parserDelegate.HasIncompleteMessage)
        {
            if (headerCompletionCorrection && !parserDelegate.AreHeadersComplete)
            {
                TryExecute(parser, "\r\n\r\n"u8);
            }

            // A datagram is self-delimiting; feed the EOF signal to complete
            // close-delimited bodies.
            TryExecute(parser, ReadOnlySpan<byte>.Empty);
        }

        if (!hasError && parserDelegate.CompletedMessages.TryDequeue(out var snapshot))
        {
            return Build(snapshot, HttpTransport.Udp, null, localEndPoint, remoteEndPoint);
        }

        return BuildIncomplete(parserDelegate, parser, HttpTransport.Udp, null, localEndPoint, remoteEndPoint);
    }

    private static int TryExecute(HttpCombinedParser parser, ReadOnlySpan<byte> data)
    {
        try
        {
            return parser.Execute(data);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void DrainCompleted(
        ListenerParserDelegate parserDelegate,
        Action<HttpRequestResponse> emit,
        IHttpConnection connection,
        out bool lastMessageClosed)
    {
        lastMessageClosed = false;

        while (parserDelegate.CompletedMessages.TryDequeue(out var snapshot))
        {
            var message = Build(snapshot, HttpTransport.Tcp, connection,
                connection.LocalEndPoint, connection.RemoteEndPoint);
            lastMessageClosed = !message.ShouldKeepAlive;
            emit(message);
        }
    }

    private static HttpRequestResponse Build(
        UpstreamMessage snapshot,
        HttpTransport transport,
        IHttpConnection? connection,
        IPEndPoint? localEndPoint,
        IPEndPoint? remoteEndPoint)
    {
        var body = ReadOnlyMemory<byte>.Empty;

        if (snapshot.Body is { Length: > 0 } bodyStream)
        {
            body = bodyStream.TryGetBuffer(out var segment)
                ? segment.AsMemory()
                : bodyStream.ToArray();
        }

        var isRequest = snapshot.MessageType == MessageTypeKind.Request;

        return new HttpRequestResponse
        {
            MessageType = isRequest ? MessageType.Request : MessageType.Response,
            Transport = transport,
            Method = isRequest ? snapshot.Method : null,
            RequestUri = isRequest ? snapshot.RequestUri : null,
            Path = isRequest ? snapshot.Path : null,
            QueryString = isRequest ? snapshot.QueryString : null,
            Fragment = isRequest ? snapshot.Fragment : null,
            StatusCode = isRequest ? 0 : snapshot.StatusCode,
            ReasonPhrase = isRequest ? null : snapshot.ResponseReason,
            MajorVersion = snapshot.MajorVersion,
            MinorVersion = snapshot.MinorVersion,
            ShouldKeepAlive = snapshot.ShouldKeepAlive,
            IsChunked = snapshot.IsTransferEncodingChunked,
            Headers = snapshot.Headers is null
                ? FrozenDictionary<string, string>.Empty
                : snapshot.Headers.ToFrozenDictionary(
                    static kv => kv.Key,
                    static kv => string.Join(", ", kv.Value),
                    StringComparer.OrdinalIgnoreCase),
            Body = body,
            IsEndOfMessage = snapshot.IsEndOfMessage,
            HasParsingErrors = false,
            LocalEndPoint = localEndPoint,
            RemoteEndPoint = remoteEndPoint,
            Connection = connection
        };
    }

    private static HttpRequestResponse BuildIncomplete(
        ListenerParserDelegate parserDelegate,
        HttpCombinedParser parser,
        HttpTransport transport,
        IHttpConnection? connection,
        IPEndPoint? localEndPoint = null,
        IPEndPoint? remoteEndPoint = null)
    {
        return new HttpRequestResponse
        {
            MessageType = parserDelegate.IsRequest ? MessageType.Request : MessageType.Response,
            Transport = transport,
            MajorVersion = parser.MajorVersion,
            MinorVersion = parser.MinorVersion,
            Headers = FrozenDictionary<string, string>.Empty,
            IsEndOfMessage = false,
            HasParsingErrors = true,
            LocalEndPoint = localEndPoint ?? connection?.LocalEndPoint,
            RemoteEndPoint = remoteEndPoint ?? connection?.RemoteEndPoint,
            Connection = connection
        };
    }
}
