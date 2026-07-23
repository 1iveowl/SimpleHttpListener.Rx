using IHttpMachine;

namespace SimpleHttpListener.Rx.Internal;

/// <summary>
/// Subclasses the upstream delegate (which handles header merging, span bodies and
/// per-message reset) to track message boundaries for the read loop.
/// </summary>
internal sealed class ListenerParserDelegate : HttpMachine.HttpParserDelegate
{
    public Queue<IHttpMachine.Model.HttpRequestResponse> CompletedMessages { get; } = new();

    public bool HasIncompleteMessage { get; private set; }

    public bool AreHeadersComplete { get; private set; }

    public bool IsRequest { get; private set; } = true;

    /// <summary>
    /// Whether the current message carries a Content-Length or Transfer-Encoding header.
    /// A request without either has no body (RFC 9112 §6.3) and can be completed at end of
    /// headers even when the parser would wait for EOF (it treats non-keep-alive requests
    /// as close-delimited).
    /// </summary>
    public bool HasBodyFramingHeader { get; private set; }

    public bool HasParserError { get; private set; }

    public override void OnMessageBegin(IHttpCombinedParser combinedParser)
    {
        base.OnMessageBegin(combinedParser);
        HasIncompleteMessage = true;
        AreHeadersComplete = false;
        IsRequest = true;
        HasBodyFramingHeader = false;
    }

    public override void OnRequestType(IHttpCombinedParser combinedParser)
    {
        base.OnRequestType(combinedParser);
        IsRequest = true;
    }

    public override void OnResponseType(IHttpCombinedParser combinedParser)
    {
        base.OnResponseType(combinedParser);
        IsRequest = false;
    }

    public override void OnHeaderName(IHttpCombinedParser combinedParser, string headerName)
    {
        base.OnHeaderName(combinedParser, headerName);

        if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            HasBodyFramingHeader = true;
        }
    }

    public override void OnHeadersEnd(IHttpCombinedParser combinedParser)
    {
        base.OnHeadersEnd(combinedParser);
        AreHeadersComplete = true;
    }

    public override void OnMessageEnd(IHttpCombinedParser combinedParser)
    {
        base.OnMessageEnd(combinedParser);
        HasIncompleteMessage = false;
        CompletedMessages.Enqueue(HttpRequestResponse);
    }

    public override void OnParserError()
    {
        base.OnParserError();
        HasParserError = true;
    }
}
