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

    private static readonly List<HttpRequestResponse> NoMessages = [];

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
                        if (ex is not ObjectDisposedException && parserDelegate.State.HasIncompleteMessage)
                        {
                            observer.OnNext(BuildIncompleteMessage());
                        }

                        break;
                    }

                    if (bytesRead == 0)
                    {
                        if (parserDelegate.State.HasIncompleteMessage)
                        {
                            if (headerCompletionCorrection && !parserDelegate.State.AreHeadersComplete)
                            {
                                TryExecute(parser, "\r\n\r\n"u8);
                            }

                            TryExecute(parser, ReadOnlySpan<byte>.Empty);

                            foreach (var message in DrainCompleted(parserDelegate, connection))
                            {
                                observer.OnNext(message);
                            }

                            if (parserDelegate.State.HasIncompleteMessage)
                            {
                                observer.OnNext(BuildIncompleteMessage());
                            }
                        }

                        break;
                    }

                    var (consumed, faulted) = TryExecute(parser, buffer.AsSpan(0, bytesRead));
                    var hasError = faulted || consumed != bytesRead;

                    // The parser treats a non-keep-alive request without Content-Length or
                    // Transfer-Encoding as close-delimited and waits for EOF; per RFC 9112
                    // such a request has no body, so complete it now.
                    if (!hasError && parserDelegate.State is
                        { HasIncompleteMessage: true, AreHeadersComplete: true, IsRequest: true, HasBodyFramingHeader: false })
                    {
                        TryExecute(parser, ReadOnlySpan<byte>.Empty);
                    }

                    var messages = DrainCompleted(parserDelegate, connection);

                    foreach (var message in messages)
                    {
                        observer.OnNext(message);
                    }

                    if (hasError)
                    {
                        observer.OnNext(BuildIncompleteMessage());
                        break;
                    }

                    if (messages.Count > 0
                        && (!messages[^1].ShouldKeepAlive || messages[^1].IsUpgradeRequest))
                    {
                        // The consumer now owns the connection: for Connection: close,
                        // SendResponseAsync in auto mode disposes it after replying; for an
                        // upgrade request, the consumer completes the handshake and the
                        // stream stops being HTTP.
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
    /// Parses one UDP datagram as a complete HTTP message. For datagram streams prefer a
    /// reused <see cref="DatagramParser"/>.
    /// </summary>
    internal static HttpRequestResponse ParseDatagram(
        byte[] datagram,
        bool headerCompletionCorrection,
        IPEndPoint? localEndPoint,
        IPEndPoint? remoteEndPoint)
    {
        using var datagramParser = new DatagramParser();
        return datagramParser.Parse(datagram, headerCompletionCorrection, localEndPoint, remoteEndPoint);
    }

    internal static (int Consumed, bool Faulted) TryExecute(HttpCombinedParser parser, ReadOnlySpan<byte> data)
    {
        try
        {
            return (parser.Execute(data), false);
        }
        catch (Exception)
        {
            return (0, true);
        }
    }

    private static List<HttpRequestResponse> DrainCompleted(
        ListenerParserDelegate parserDelegate,
        IHttpConnection connection)
    {
        if (parserDelegate.CompletedMessages.Count == 0)
        {
            return NoMessages;
        }

        var messages = new List<HttpRequestResponse>(parserDelegate.CompletedMessages.Count);

        while (parserDelegate.CompletedMessages.TryDequeue(out var snapshot))
        {
            messages.Add(Build(snapshot, HttpTransport.Tcp, connection,
                connection.LocalEndPoint, connection.RemoteEndPoint));
        }

        return messages;
    }

    internal static HttpRequestResponse Build(
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

        IReadOnlyDictionary<string, string> headers;

        if (snapshot.Headers is not { Count: > 0 } rawHeaders)
        {
            headers = FrozenDictionary<string, string>.Empty;
        }
        else
        {
            // Built once and read a handful of times per message, so a plain Dictionary
            // beats FrozenDictionary's construction cost.
            var headerDictionary = new Dictionary<string, string>(rawHeaders.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, values) in rawHeaders)
            {
                headerDictionary[name] = string.Join(", ", values);
            }

            headers = headerDictionary;
        }

        var isRequest = snapshot.MessageType == MessageTypeKind.Request;

        var isUpgradeRequest = isRequest
            && headers.ContainsKey("UPGRADE")
            && headers.TryGetValue("CONNECTION", out var connectionHeader)
            && HasToken(connectionHeader, "upgrade");

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
            Headers = headers,
            Body = body,
            IsEndOfMessage = snapshot.IsEndOfMessage,
            HasParsingErrors = false,
            LocalEndPoint = localEndPoint,
            RemoteEndPoint = remoteEndPoint,
            Connection = connection,
            IsUpgradeRequest = isUpgradeRequest
        };
    }

    private static bool HasToken(string headerValue, string token)
    {
        foreach (var part in headerValue.AsSpan().Split(','))
        {
            if (headerValue.AsSpan()[part].Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static HttpRequestResponse BuildIncomplete(
        ListenerParserDelegate parserDelegate,
        HttpCombinedParser parser,
        HttpTransport transport,
        IHttpConnection? connection,
        IPEndPoint? localEndPoint = null,
        IPEndPoint? remoteEndPoint = null)
    {
        return new HttpRequestResponse
        {
            MessageType = parserDelegate.State.IsRequest ? MessageType.Request : MessageType.Response,
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
