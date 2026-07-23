using IHttpMachine;

namespace SimpleHttpListener.Rx.Internal;

/// <summary>
/// Immutable snapshot of where the parser is inside the current message.
/// </summary>
/// <param name="HasIncompleteMessage">A message has begun but not ended.</param>
/// <param name="AreHeadersComplete">The current message's header section has ended.</param>
/// <param name="IsRequest">The current message is a request (default until known).</param>
/// <param name="HasBodyFramingHeader">
/// The current message carries a Content-Length or Transfer-Encoding header. A request
/// without either has no body (RFC 9112 §6.3) and can be completed at end of headers even
/// when the parser would wait for EOF (it treats non-keep-alive requests as close-delimited).
/// </param>
/// <param name="HasParserError">The parser reported an unrecoverable error.</param>
internal readonly record struct ParseState(
    bool HasIncompleteMessage,
    bool AreHeadersComplete,
    bool IsRequest,
    bool HasBodyFramingHeader,
    bool HasParserError);

/// <summary>
/// Subclasses the upstream delegate (which handles header merging, span bodies and
/// per-message reset) to track message boundaries for the read loop.
/// </summary>
internal sealed class ListenerParserDelegate : HttpMachine.HttpParserDelegate
{
    public Queue<IHttpMachine.Model.HttpRequestResponse> CompletedMessages { get; } = new();

    public ParseState State { get; private set; } = new(IsRequest: true,
        HasIncompleteMessage: false, AreHeadersComplete: false, HasBodyFramingHeader: false,
        HasParserError: false);

    public override void OnMessageBegin(IHttpCombinedParser combinedParser)
    {
        base.OnMessageBegin(combinedParser);
        State = State with
        {
            HasIncompleteMessage = true,
            AreHeadersComplete = false,
            IsRequest = true,
            HasBodyFramingHeader = false
        };
    }

    public override void OnRequestType(IHttpCombinedParser combinedParser)
    {
        base.OnRequestType(combinedParser);
        State = State with { IsRequest = true };
    }

    public override void OnResponseType(IHttpCombinedParser combinedParser)
    {
        base.OnResponseType(combinedParser);
        State = State with { IsRequest = false };
    }

    public override void OnHeaderName(IHttpCombinedParser combinedParser, string headerName)
    {
        base.OnHeaderName(combinedParser, headerName);

        if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            State = State with { HasBodyFramingHeader = true };
        }
    }

    public override void OnHeadersEnd(IHttpCombinedParser combinedParser)
    {
        base.OnHeadersEnd(combinedParser);
        State = State with { AreHeadersComplete = true };
    }

    public override void OnMessageEnd(IHttpCombinedParser combinedParser)
    {
        base.OnMessageEnd(combinedParser);
        State = State with { HasIncompleteMessage = false };
        CompletedMessages.Enqueue(HttpRequestResponse);
    }

    public override void OnParserError()
    {
        base.OnParserError();
        State = State with { HasParserError = true };
    }
}
