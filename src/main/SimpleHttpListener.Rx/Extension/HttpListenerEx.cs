using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISimpleHttpListener.Rx.Enum;
using ISimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Parser;

namespace SimpleHttpListener.Rx.Extension
{
    public static class HttpListenerEx
    {
        public static IObservable<IHttpRequestResponse> ToHttpListenerObservable(
            this TcpListener tcpListener, 
            CancellationToken outerCancellationToken,
            params ErrorCorrection[] errorCorrections)
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
                        RemoteIpEndPoint = remoteEndPoint,
                        LocalIpEndPoint = tcpClient.Client.LocalEndPoint as IPEndPoint

                    };
                })
                .Select(httpObj => Observable.FromAsync(ct => ParseAsync(httpObj, ct, errorCorrections)))
                .Concat();
        }

        public static IObservable<IHttpRequestResponse> ToHttpListenerObservable(
            this UdpClient udpClient,
            CancellationToken outerCancellationToken,
            params ErrorCorrection[] errorCorrections)
        {
            return udpClient.ToObservable(outerCancellationToken)
                .Select(udpReceiveResult => new HttpRequestResponse
                {
                    ResponseStream = new MemoryStream(udpReceiveResult.Buffer),
                    RequestType = RequestType.UDP,
                    RemoteIpEndPoint = udpReceiveResult.RemoteEndPoint,
                    LocalIpEndPoint = udpClient.Client.LocalEndPoint as IPEndPoint
                })
                .Select(httpObj => Observable.FromAsync(ct => ParseAsync(httpObj, ct, errorCorrections)))
                .Concat();
        }

        private static int StringToInt(string number)
        {
            return int.TryParse(number, out var x) ? x : 0;
        }

        private static async Task<IHttpRequestResponse> ParseAsync(
            HttpRequestResponse requestResponseObj, 
            CancellationToken ct, 
            params ErrorCorrection[] errorCorrections)
        {
            using (var requestHandler = new HttpParserDelegate(requestResponseObj))
            using (var httpStreamParser = new HttpStreamParser(requestHandler, errorCorrections))
            {
                var result = await httpStreamParser.ParseAsync(requestResponseObj.ResponseStream, ct);

                if (httpStreamParser.HasParsingError)
                {
                    ((HttpRequestResponse)result).HasParsingErrors = httpStreamParser.HasParsingError;
                }

                return result;
            }
        }
    }
}
