# Simple Http Listener Rx

[![NuGet Badge](https://buildstats.info/nuget/SimpleHttpListener)](https://www.nuget.org/packages/SimpleHttpListener)

[![.NET Standard](http://img.shields.io/badge/.NET_Standard-v2.0-green.svg)](https://docs.microsoft.com/da-dk/dotnet/articles/standard/library)

*Please star this project if you find it useful. Thank you.*

## What is this?

This is a simple HTTP Listener that is created as a .NET Standard 2.0 Library for cross platform use. 

The HTTP listener is capable of listening for HTTP on TCP as well as UDP, including UDP multicast, which makes it suitable for use with for use with for instance [UPnP](https://openconnectivity.org/developer/specifications/upnp-resources/upnp).

The HTTP listner is created with [Reactive Extensions](http://reactivex.io/) making it easy to use and efficient for handling the asynchronous nature of HTTP.

## Why does this library start with version 6.0?

Simple Http Listener Rx is the next version of [Simple HTTP Listener PCL](https://github.com/1iveowl/Simple-Http-Listener-PCL). 

So much was changed between 5.1.1 and this library, that it made more sense to both rename and create an all new library. However, the goal of the two libraries is the same.

## Simple HTTP, Simple to Use
There is a reason this library is called simple. Turning a TCP or UDP connection into a observable HTTP listener is done by simply adding the `ToHttpListenerObservable` Extension Method to a `TcPListener` or a `UdpClient`. That's it. See the examples below or the test client included in the library. code

### Using

```cs
using ISimpleHttpListener.Rx.Enum;
using SimpleHttpListener.Rx.Extension;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Service;
```

### TCP HTTP Listener
For a simple TCP based HTTP Listner see this example.

Notice that it is [bad practice](https://stackoverflow.com/a/37131023/4140832) to create an async Suscriber - i.e. `.Subscribe(async x => ...)`. To avoid create a `.Select(r => Observable.FromAsync(() => ...).Concat` instead as in the example below.

```csharp

static void TcpListenerTest()
{

    var uri = new Uri("http://mylocalipaddress:8000");

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
            Console.WriteLine("--------------***-------------");
        })
        // Send reply to browser
        .Select(r => Observable.FromAsync(() => SendResponseAsync(r, httpSender)))
        .Concat()
        .Subscribe(r =>
        {
            Console.WriteLine("Reply sent.");
        },
        ex =>
        {
            Console.WriteLine($"Exception: {ex}");
        },
        () =>
        {
            Console.WriteLine("Completed.");
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


```

### UDP HTTP Listener

An UDP Listener is created like this. The following UDP listener will listen for UPnP [SSDP](https://en.wikipedia.org/wiki/Simple_Service_Discovery_Protocol) multicasts on the local network. 

```csharp
var localHost = IPAddress.Parse("192.168.0.59");
var localEndPoint = new IPEndPoint(localHost, 1900);

var multicastIpAddress = IPAddress.Parse("239.255.255.250");

udpClient = new UdpClient
{
    ExclusiveAddressUse = false,
    MulticastLoopback = true
};

udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

udpClient.JoinMulticastGroup(IPAddress.Parse(UdpSSDPMultiCastAddress));

udpClient.Client.Bind(_localEnpoint);

udpMulticastHttpListenerDisposable = udpClient.ToHttpListenerObservable(ct, ErrorCorrection.HeaderCompletionError)
    .Subscribe(r => 
    {
        Console.WriteLine($"Remote Address: {r.RemoteAddress}");
        Console.WriteLine($"Remote Port: {r.RemotePort}");
        Console.WriteLine("--------------***-------------");
    },
    ex => 
    {
        Console.WriteLine($"Exception: {ex}");
    }, 
    () => 
    {
        Console.WriteLine("Completed.");
    });

```

Notice the `ErrorCorrection.HeaderCompletionError`. It's optional. It's there to manage the the situation where an HTTP message header fail to end with `\r\n\r\n`. 