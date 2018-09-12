using System;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using HttpListener.Rx.Model;
using HttpListener.Rx.Parser;
using IHttpListener.Rx.Enum;

namespace HttpListener.Rx.Extension
{
    public static class HttpListenerEx
    {
        public static IObservable<IHttpRequestResponse> ToHttpTcpServerObservable(this TcpListener tcpListener, CancellationToken outerCancellationToken)
        {
            return tcpListener.ToObservable(outerCancellationToken)
                .Where(tcpClient => tcpClient != null)
                .Select(tcpClient =>
                {
                    var stream = tcpClient.GetStream();

                    var remoteEndPoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;

                    return new HttpRequestResponse
                    {
                        ResponseStream = stream,
                        RequestType = RequestType.TCP,
                        TcpClient = tcpClient,
                        RemoteAddress = remoteEndPoint?.Address?.ToString(),
                        RemotePort = remoteEndPoint?.Port ?? 0
                    };
                })
                .Select(httpObj => Observable.FromAsync(ct => ParseAsync(httpObj, ct)))
                .Concat();
        }

        private static async Task<IHttpRequestResponse> ParseAsync(HttpRequestResponse requestResponseObj, CancellationToken ct)
        {
            using (var requestHandler = new HttpParserDelegate(requestResponseObj))
            using (var httpStreamParser = new HttpStreamParser(requestHandler))
            {
                return await httpStreamParser.ParseAsync(requestResponseObj.ResponseStream, ct);
            }
        }
    }
}
