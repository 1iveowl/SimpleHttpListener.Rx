using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HttpListener.Rx.Model;
using HttpMachine;
using IHttpMachine;

namespace HttpListener.Rx.Parser
{
    internal class HttpParserDelegate : IHttpParserCombinedDelegate
    {
        private readonly IObserver<ParserState> _observerParserState;

        internal IObservable<ParserState> ParserCompletionObservable { get; }

        public HttpRequestResponse RequestResponse { get; }

        public MessageType MessageType { get; private set; }

        internal HttpParserDelegate(HttpRequestResponse requestResponse)
        {
            RequestResponse = requestResponse;

            var parserStateSubject = new BehaviorSubject<ParserState>(ParserState.Start);

            _observerParserState = parserStateSubject.AsObserver();
            ParserCompletionObservable = parserStateSubject.AsObservable();
        }

        public void OnMessageBegin(IHttpCombinedParser parser)
        {
            _observerParserState.OnNext(ParserState.Parsing);
        }

        public void OnRequestType(IHttpCombinedParser combinedParser)
        {
            RequestResponse.MessageType = MessageType.Request;
            MessageType = MessageType.Request;
        }

        public void OnResponseType(IHttpCombinedParser combinedParser)
        {
            RequestResponse.MessageType = MessageType.Response;
            MessageType = MessageType.Response;
        }

        public void OnMethod(IHttpCombinedParser parser, string method)
        {
            RequestResponse.Method = method;
        }

        public void OnRequestUri(IHttpCombinedParser parser, string requestUri)
        {
            RequestResponse.RequestUri = requestUri;
        }

        public void OnPath(IHttpCombinedParser parser, string path)
        {
            RequestResponse.Path = path;
        }

        public void OnFragment(IHttpCombinedParser parser, string fragment)
        {
            RequestResponse.Fragment = fragment;
        }

        public void OnQueryString(IHttpCombinedParser parser, string queryString)
        {
            RequestResponse.QueryString = queryString;
        }

        private string _headerName;
        private bool _headerAlreadyExist;
        //protected IHttpHeaders HeaderDictionary;

        //http://www.w3.org/Protocols/rfc2616/rfc2616-sec4.html#sec4.2
        public void OnHeaderName(IHttpCombinedParser parser, string name)
        {

            if (RequestResponse.Headers.ContainsKey(name.ToUpper()))
            {
                // Header Field Names are case-insensitive http://www.w3.org/Protocols/rfc2616/rfc2616-sec4.html#sec4.2
                _headerAlreadyExist = false;
            }
            _headerName = name.ToUpper();
        }

        public void OnHeaderValue(IHttpCombinedParser parser, string value)
        {
            if (_headerAlreadyExist)
            {
                // Join multiple message-header fields into one comma seperated list http://www.w3.org/Protocols/rfc2616/rfc2616-sec4.html#sec4.2
                RequestResponse.Headers[_headerName] = $"{RequestResponse.Headers[_headerName]}, {value}";
                _headerAlreadyExist = false;
            }
            else
            {
                RequestResponse.Headers[_headerName] = value;
            }
        }

        public void OnTransferEncodingChunked(IHttpCombinedParser combinedParser, bool isChunked)
        {

            RequestResponse.IsChunked = isChunked;
        }

        public void OnChunkedLength(IHttpCombinedParser combinedParser, int length)
        {
            
        }

        public void OnChunkReceived(IHttpCombinedParser combinedParser)
        {
            
        }

        public void OnHeadersEnd(IHttpCombinedParser parser)
        {
            //throw new NotImplementedException();
        }

        public void OnBody(IHttpCombinedParser parser, ArraySegment<byte> data)
        {
            RequestResponse.Body.Write(data.Array, 0, data.Array.Length);
        }

        public void OnMessageEnd(IHttpCombinedParser parser)
        {
            RequestResponse.IsEndOfRequest = true;
            _observerParserState.OnNext(ParserState.Completed);
            _observerParserState.OnCompleted();
        }

        public void OnResponseCode(IHttpCombinedParser parser, int statusCode, string statusReason)
        {
            RequestResponse.StatusCode = statusCode;
            RequestResponse.ResponseReason = statusReason;
        }

        public void OnParserError()
        {
            _observerParserState.OnNext(ParserState.Failed);
            _observerParserState.OnError(new Exception("Http parser failed."));
        }

        public void Dispose()
        {
            _observerParserState.OnCompleted();
        }
    }
}
