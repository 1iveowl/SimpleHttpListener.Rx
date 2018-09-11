using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using HttpListener.Rx.Model;
using HttpListener.Rx.Parser;
using IHttpListener.Rx.Enum;
using IHttpListener.Rx.Model;

namespace HttpListener.Rx.Extension
{
    public static class HttpListenerEx
    {
        public static IObservable<IHttpRequestResponse> ToHttpTcpServerObservable(this TcpListener tcpListener, CancellationToken ct)
        {
            return tcpListener.ToObservable(ct)
                .Where(tcpClient => tcpClient != null)
                .Select(tcpClient =>
                {
                    var stream = tcpClient.GetStream();

                    var requestResponseObj = new HttpRequestResponse
                    {
                        ResponseStream = tcpClient.GetStream(),
                        RequestType = RequestType.TCP
                    };

                    using (var requestHandler = new HttpParserDelegate(requestResponseObj))
                    using (var httpStreamParser = new HttpStreamParser())
                    {
                        var result = httpStreamParser.Parse(requestHandler, stream);

                        return result;
                    }
                });
        }
    }
}
