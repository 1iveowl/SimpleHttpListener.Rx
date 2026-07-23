# SimpleHttpListener.Rx

[![NuGet](https://img.shields.io/nuget/v/SimpleHttpListener.Rx?logo=nuget&label=SimpleHttpListener.Rx)](https://www.nuget.org/packages/SimpleHttpListener.Rx)
[![Downloads](https://img.shields.io/nuget/dt/SimpleHttpListener.Rx?logo=nuget&color=blue)](https://www.nuget.org/packages/SimpleHttpListener.Rx)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE.md)

[![.NET Standard](https://img.shields.io/badge/.NET%20Standard-2.0-5C2D91?logo=dotnet&logoColor=white)](https://learn.microsoft.com/dotnet/standard/net-standard)
[![System.Reactive](https://img.shields.io/badge/Rx-4.0.0-ff69b4.svg)](https://reactivex.io/)

An Rx-based HTTP listener for TCP, UDP, and UDP multicast traffic.

*Please star this project if you find it useful. Thank you.*

## Overview

SimpleHttpListener.Rx is a .NET Standard 2.0 library for cross-platform HTTP message handling. It can listen for HTTP over TCP and UDP, including UDP multicast, which makes it useful for protocols such as [UPnP](https://openconnectivity.org/developer/specifications/upnp-resources/upnp).

The library is built with [Reactive Extensions](https://reactivex.io/), exposing incoming HTTP messages as observables for asynchronous processing.

## Why version 6.0?

SimpleHttpListener.Rx is the successor to [Simple HTTP Listener PCL](https://github.com/1iveowl/Simple-Http-Listener-PCL). The changes from version 5.1.1 were substantial enough to warrant a new name and major version, while retaining the same overall goal: a lightweight HTTP listener for application-controlled transports.

The legacy package remains available as [SimpleHttpListener](https://www.nuget.org/packages/SimpleHttpListener).

## Usage

Turn a `TcpListener` or `UdpClient` into an observable HTTP listener with the `ToHttpListenerObservable` extension method. The examples below show both transports.

### Namespaces

```cs
using ISimpleHttpListener.Rx.Enum;
using SimpleHttpListener.Rx.Extension;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Service;
```

### TCP HTTP listener

Avoid an `async` subscriber such as `.Subscribe(async x => ...)`; [it can make Rx error handling and scheduling difficult](https://stackoverflow.com/a/37131023/4140832). Instead, convert the asynchronous operation with `Observable.FromAsync`, then concatenate it as shown below.

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
            Console.WriteLine($"Remote Address: {r.RemoteIpEndPoint.Address}");
            Console.WriteLine($"Remote Port: {r.RemoteIpEndPoint.Port}");
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

### UDP HTTP listener

The following UDP listener receives UPnP [SSDP](https://en.wikipedia.org/wiki/Simple_Service_Discovery_Protocol) multicast traffic from the local network.

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
		Console.WriteLine($"Remote Address: {r.RemoteIpEndPoint.Address}");
		Console.WriteLine($"Remote Port: {r.RemoteIpEndPoint.Port}");
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

`ErrorCorrection.HeaderCompletionError` is optional. It handles HTTP message headers that do not end with `\r\n\r\n`.
