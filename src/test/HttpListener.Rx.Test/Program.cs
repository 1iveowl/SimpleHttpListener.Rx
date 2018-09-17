using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISimpleHttpListener.Rx.Enum;
using ISimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Extension;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Service;

namespace SimpleHttpListener.Rx.Test
{
    class Program
    {
        static async Task Main(string[] args)
        {
            TcpListenerTest();
            //await UdpMulticastListenerTest();
            Console.WriteLine("Press any key to stop.");
            Console.ReadLine();
        }

        static async Task UdpUnicastListenerTest()
        {
            var udpClient = new UdpClient();

        }

        static async Task UdpMulticastListenerTest()
        {

            var localHost = IPAddress.Parse("192.168.0.59");
            var multicastIpAddress = IPAddress.Parse("239.255.255.250");

            var ipEndpoint = new IPEndPoint(localHost, 8000);

            var udpClient = new UdpClient
            {
                ExclusiveAddressUse = false
            };
            udpClient.JoinMulticastGroup(multicastIpAddress);

            udpClient.Client.Bind(ipEndpoint);

            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var cts = new CancellationTokenSource();

            var disposable = udpClient
                .ToHttpListenerObservable(cts.Token)
                .Subscribe(msg =>
                    {
                        Console.WriteLine($"Remote Address: {msg.RemoteAddress}");
                        Console.WriteLine($"Remote Port: {msg.RemotePort}");
                        Console.WriteLine($"Local Address: {msg.LocalAddress}");
                        Console.WriteLine($"Local Port: {msg.LocalPort}");
                        msg.Body.WriteToConsole();
                        Console.WriteLine("--------------***-------------");
                    },
                    ex =>
                    {

                    },
                    () =>
                    {

                    });

        }

static void TcpListenerTest()
{

    var uri = new Uri("http://192.168.0.59:8000");

    var tcpListener = new TcpListener(uri.Host.GetIPv4Address(), uri.Port)
    {
        ExclusiveAddressUse = false
    };
    
    var httpSender = new HttpSender();

    var cts = new CancellationTokenSource();

    var disposable = tcpListener
        .ToHttpListenerObservable(cts.Token)
        .Do(r =>
        {
            Console.WriteLine($"Remote Address: {r.RemoteAddress}");
            Console.WriteLine($"Remote Port: {r.RemotePort}");
            Console.WriteLine($"Local Address: {r.LocalAddress}");
            Console.WriteLine($"Local Port: {r.LocalPort}");
            Console.WriteLine("--------------***-------------");
        })
        .Select(r => Observable.FromAsync(() => SendResponseAsync(r, httpSender)))
        .Concat()
        .Subscribe(r =>
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

        await httpSender.SendTcpResponseAsync(request, response).ConfigureAwait(false);
    }
}
    }
}
