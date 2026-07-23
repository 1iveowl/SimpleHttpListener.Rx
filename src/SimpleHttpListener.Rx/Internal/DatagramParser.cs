using System.Net;
using HttpMachine;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Internal;

/// <summary>
/// Parses datagrams one at a time, reusing the parser/delegate pair across well-formed
/// datagrams (the dominant case under SSDP multicast load) and recreating it whenever a
/// parse left the state machine unreliable.
/// </summary>
internal sealed class DatagramParser : IDisposable
{
    private ListenerParserDelegate _parserDelegate;
    private HttpCombinedParser _parser;

    public DatagramParser()
    {
        _parserDelegate = new ListenerParserDelegate();
        _parser = new HttpCombinedParser(_parserDelegate);
    }

    public HttpRequestResponse Parse(
        byte[] datagram,
        bool headerCompletionCorrection,
        IPEndPoint? localEndPoint,
        IPEndPoint? remoteEndPoint)
    {
        var (consumed, faulted) = HttpMessageParser.TryExecute(_parser, datagram);
        var hasError = faulted || consumed != datagram.Length;
        var neededEofSignal = false;

        if (!hasError && _parserDelegate.State.HasIncompleteMessage)
        {
            neededEofSignal = true;

            if (headerCompletionCorrection && !_parserDelegate.State.AreHeadersComplete)
            {
                HttpMessageParser.TryExecute(_parser, "\r\n\r\n"u8);
            }

            // A datagram is self-delimiting; feed the EOF signal to complete
            // close-delimited bodies.
            HttpMessageParser.TryExecute(_parser, ReadOnlySpan<byte>.Empty);
        }

        var message = !hasError && _parserDelegate.CompletedMessages.TryDequeue(out var snapshot)
            ? HttpMessageParser.Build(snapshot, HttpTransport.Udp, null, localEndPoint, remoteEndPoint)
            : HttpMessageParser.BuildIncomplete(_parserDelegate, _parser, HttpTransport.Udp, null,
                localEndPoint, remoteEndPoint);

        if (hasError || neededEofSignal || _parserDelegate.State.HasIncompleteMessage)
        {
            // Errors and mid-message EOF signals leave the state machine somewhere other
            // than "ready for a new message" — start fresh for the next datagram.
            RecreateParser();
        }
        else
        {
            // One message per datagram: drop any extras a pipelined datagram produced.
            _parserDelegate.CompletedMessages.Clear();
        }

        return message;
    }

    private void RecreateParser()
    {
        Dispose();
        _parserDelegate = new ListenerParserDelegate();
        _parser = new HttpCombinedParser(_parserDelegate);
    }

    public void Dispose()
    {
        _parser.Dispose();
        _parserDelegate.Dispose();
    }
}
