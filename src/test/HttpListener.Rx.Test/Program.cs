using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HttpListener.Rx.Extension;
using HttpListener.Rx.Model;
using HttpListener.Rx.Service;
using IHttpListener.Rx.Enum;

namespace HttpListener.Rx.Test
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await TcpListenerTest();
            Console.ReadLine();
        }

        static async Task TcpListenerTest()
        {

            var httpSender = new HttpSender();
            var uri = new Uri("http://192.168.0.59:8000");

            var tcpListener = new TcpListener(uri.Host.GetIPv4Address(), uri.Port);

            tcpListener.Start();

            var cts = new CancellationTokenSource();

            var disposable = tcpListener.ToHttpTcpServerObservable(cts.Token)
                .Do(r =>
                {
                    Console.WriteLine($"Remote Address: {r.RemoteAddress}");
                    Console.WriteLine($"Remote Port: {r.RemotePort}");
                    Console.WriteLine("--------------***-------------");
                })
                .Select(r => Observable.FromAsync(() => SendResponseAsync(r, httpSender)))
                .Concat()
                .Subscribe(
                r =>
                {

                },
                ex =>
                {

                },
                () =>
                {

                });
            
        }

        static async Task SendResponseAsync(IHttpRequestResponse request, HttpSender httpSender)
        {
            if (request.RequestType == RequestType.TCP)
            {
                var response = new HttpResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    ResponseReason = HttpStatusCode.OK.ToString(),
                    Headers = new Dictionary<string, string>
                    {
                        {"Date", DateTime.UtcNow.ToString("r")},
                        {"Content-Type", "text/html; charset=UTF-8" },
                    },
                    Body = new MemoryStream(Encoding.UTF8.GetBytes($"<html>\r\n<body>\r\n<h1>Hello, World! {DateTime.Now}</h1>\r\n</body>\r\n</html>"))
                };

                await httpSender.SendResponseAsync(request, response);
            }
        }
    }
}
