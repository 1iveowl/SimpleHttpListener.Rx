using System;
using System.IO;
using System.Threading;
using HttpListener.Rx.Model;
using HttpMachine;
using HttpListener.Rx.Extension;

namespace HttpListener.Rx.Parser
{
    internal class HttpStreamParser : IDisposable
    {
        private IDisposable _disposableParser;

        internal IHttpRequestResponse Parse(HttpParserDelegate requestHandler, Stream stream)
        {
            using (var parserHandler = new HttpCombinedParser(requestHandler))
            using (var cts = new CancellationTokenSource())
            {
              
                _disposableParser = stream.ToByteStreamObservable(cts.Token)
                    .Subscribe(
                    bArray =>
                    {

                        try
                        {
                            if (parserHandler.Execute(new ArraySegment<byte>(bArray, 0, bArray.Length)) <= 0)
                            {
                                requestHandler.RequestResponse.IsUnableToParseHttp = true;
                            }
                        }
                        catch (Exception)
                        {
                            requestHandler.RequestResponse.IsUnableToParseHttp = true;
                            requestHandler.StopParsing();
                        }

                    },
                    ex =>
                    {
                        if (ex is TimeoutException)
                        {
                            requestHandler.RequestResponse.IsRequestTimedOut = true;

                            requestHandler.StopParsing();

                        }
                        else
                        {
                            requestHandler.RequestResponse.IsUnableToParseHttp = true;

                            requestHandler.StopParsing();
                        }
                    },
                    () =>
                    {

                    });

                parserHandler.Execute(default);

                requestHandler.RequestResponse.MajorVersion = parserHandler.MajorVersion;
                requestHandler.RequestResponse.MinorVersion = parserHandler.MinorVersion;
                requestHandler.RequestResponse.ShouldKeepAlive = parserHandler.ShouldKeepAlive;
            }

            return requestHandler.RequestResponse;
        }

        public void Dispose()
        {
            _disposableParser?.Dispose();
        }
    }
}
