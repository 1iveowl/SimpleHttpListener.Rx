# SimpleHttpListener.Rx

[![NuGet](https://img.shields.io/nuget/v/SimpleHttpListener.Rx?logo=nuget&label=SimpleHttpListener.Rx)](https://www.nuget.org/packages/SimpleHttpListener.Rx)
[![Downloads](https://img.shields.io/nuget/dt/SimpleHttpListener.Rx?logo=nuget&color=blue)](https://www.nuget.org/packages/SimpleHttpListener.Rx)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE.md)

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![System.Reactive](https://img.shields.io/badge/Rx-7.0.0-ff69b4.svg)](https://reactivex.io/)

An Rx-based HTTP listener for TCP, UDP, and UDP multicast traffic.

*Please star this project if you find it useful. Thank you.*

## Overview

SimpleHttpListener.Rx is a .NET library for HTTP message handling over application-controlled transports. It can listen for HTTP over TCP and UDP, including UDP multicast, which makes it useful for protocols such as [UPnP](https://openconnectivity.org/developer/specifications/upnp-resources/upnp)/[SSDP](https://en.wikipedia.org/wiki/Simple_Service_Discovery_Protocol).

The library is built with [Reactive Extensions](https://reactivex.io/), exposing incoming HTTP messages as an `IObservable<HttpRequestResponse>` for asynchronous processing.

Version 7.0.0 is a modernization release: .NET 10, [HttpMachine.PCL](https://www.nuget.org/packages/HttpMachine.PCL) 6.0.x span-based parsing, HTTP keep-alive with concurrent connection handling, and a cleaned-up public API. See [Breaking changes in 7.0.0](#breaking-changes-in-700) if you are upgrading. Version 7.1.0 adds [WebSocket accept support](#websockets-710) — no ASP.NET/Kestrel required.

## Usage

Turn a `TcpListener` or `UdpClient` into an observable HTTP listener with the `ToHttpListenerObservable` extension method.

### Namespaces

```csharp
using SimpleHttpListener.Rx;
using SimpleHttpListener.Rx.Model;
```

### TCP HTTP listener

Avoid an `async` subscriber such as `.Subscribe(async x => ...)`; [it can make Rx error handling and scheduling difficult](https://stackoverflow.com/a/37131023/4140832). Instead, convert the asynchronous operation with `Observable.FromAsync`, then concatenate it as shown below.

```csharp
var tcpListener = new TcpListener(IPAddress.Loopback, 8088);

var cts = new CancellationTokenSource();

var disposable = tcpListener
    .ToHttpListenerObservable(cts.Token)
    .Do(r => Console.WriteLine($"{r.Method} {r.Path} from {r.RemoteEndPoint}"))
    // Send reply to the client
    .Select(r => Observable.FromAsync(() => r.SendResponseAsync(new HttpResponse
    {
        Headers =
        {
            ["Date"] = DateTime.UtcNow.ToString("r"),
            ["Content-Type"] = "text/html; charset=UTF-8"
        },
        Body = Encoding.UTF8.GetBytes($"<html><body><h1>Hello, World! {DateTime.Now}</h1></body></html>")
    })))
    .Concat()
    .Subscribe(
        _ => Console.WriteLine("Reply sent."),
        ex => Console.WriteLine($"Exception: {ex}"),
        () => Console.WriteLine("Completed."));
```

The listener starts when the observable is first subscribed and stops when the last subscription is disposed (or the token is cancelled). Re-subscribing restarts it.

### Keep-alive and connection ownership

New in 7.0.0: connections are handled **concurrently**, and an HTTP/1.1 keep-alive connection emits **one message per request** — a slow or idle client no longer blocks other clients.

Ownership of the underlying connection is simple:

- While the listener is reading a connection, the listener owns it. It disposes the connection when the client closes it or a fatal parse/IO error occurs.
- When a message with `ShouldKeepAlive == false` is emitted, the listener stops reading and the **consumer** owns the connection. `SendResponseAsync` in auto mode (`closeConnection: null`, the default) closes it after the response is written, so the simple flow above handles both cases correctly.
- Disposing a connection yourself while the listener is reading it is treated as a normal close, not an error.

`SendResponseAsync` always emits a correct `Content-Length` (including `0` for empty bodies) unless you set `Content-Length` or `Transfer-Encoding` yourself, and adds the appropriate `Connection: close` / `Connection: keep-alive` header automatically.

### UDP HTTP listener

The following UDP listener receives UPnP SSDP multicast traffic from the local network.

```csharp
var udpClient = new UdpClient
{
    ExclusiveAddressUse = false,
    MulticastLoopback = true
};

udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
udpClient.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));

var disposable = udpClient
    .ToHttpListenerObservable(cts.Token, ErrorCorrection.HeaderCompletionError)
    .Subscribe(r =>
    {
        Console.WriteLine($"{r.Method} from {r.RemoteEndPoint}");
    });
```

`ErrorCorrection.HeaderCompletionError` is optional. Some SSDP/UPnP devices send messages whose header section does not end with the required empty line (`\r\n\r\n`); with the correction enabled such messages still parse. Without it they are emitted with `HasParsingErrors == true` and `IsEndOfMessage == false`. UDP messages have `Connection == null`; replying is up to you (e.g. via `UdpClient.SendAsync`).

### WebSockets (7.1.0+)

The listener accepts incoming WebSocket connections without ASP.NET/Kestrel. A WebSocket
handshake arrives as a normal HTTP request with `IsUpgradeRequest == true`; the listener stops
reading that connection and hands ownership to you. Call `AcceptWebSocketAsync` to complete the
RFC 6455 handshake and get a standard `System.Net.WebSockets.WebSocket` (all framing, ping/pong
and close handling comes from the .NET runtime):

```csharp
var disposable = tcpListener
    .ToHttpListenerObservable(cts.Token)
    .Subscribe(request =>
    {
        if (request.IsUpgradeRequest)
        {
            _ = HandleWebSocketAsync(request);
        }
        else
        {
            _ = request.SendResponseAsync(new HttpResponse { Body = "Hello, World"u8.ToArray() });
        }
    });

static async Task HandleWebSocketAsync(HttpRequestResponse request)
{
    try
    {
        using var webSocket = await request.AcceptWebSocketAsync();
        // Standard WebSocket API from here: ReceiveAsync / SendAsync / CloseAsync…
    }
    finally
    {
        request.Connection?.Dispose(); // consumer owns the connection after the upgrade emission
    }
}
```

Notes and limitations:

- After an upgrade request is emitted the listener no longer reads that connection — complete
  the handshake or dispose `Connection`, even if you reject the upgrade.
- `ws://` only: the listener runs on a raw `TcpListener` with no TLS, so browsers can only
  connect from non-HTTPS pages. This targets LAN/local/native-app scenarios.
- `AcceptWebSocketAsync` validates the handshake (version 13, key present) and throws
  `InvalidOperationException` for invalid requests; sending an error response is up to you.
- Sub-protocols are pass-through (`subProtocol` parameter), no negotiation logic.
- For the client side of the story, see
  [WebsocketClientLite.PCL](https://github.com/1iveowl/WebsocketClientLite.PCL) — the
  `samples/SimpleHttpListener.Rx.Sample.WebSocketClient` project connects it to the server
  sample's echo endpoint.

### Parse errors

The listener observables never fail because of one bad client. Malformed input or a connection that closes mid-message produces an emission with `HasParsingErrors == true`, and the listener keeps serving other connections.

## Breaking changes in 7.0.0

7.0.0 targets .NET 10 and ships one assembly: the separate `ISimpleHttpListener.Rx` interface assembly/package is gone, as are the `IHttpCommon`/`IHttpHeaders`/`IParseControl`/`IHttpRequest`/`IHttpResponse`/`IHttpRequestReponse` interfaces. Everything lives in the `SimpleHttpListener.Rx` and `SimpleHttpListener.Rx.Model` namespaces.

| v6 | v7 |
|---|---|
| `IHttpRequestReponse` (misspelled) interface | `HttpRequestResponse` class |
| `RequestType` enum (`TCP`/`UDP`) | `HttpTransport` enum (`Tcp`/`Udp`) |
| `MessageType` (from HttpMachine) | `MessageType` (own enum, `Request`/`Response`) |
| `ResponseReason` | `ReasonPhrase` |
| `LocalIpEndPoint` / `RemoteIpEndPoint` | `LocalEndPoint` / `RemoteEndPoint` |
| `MemoryStream Body` | `ReadOnlyMemory<byte> Body` |
| `TcpClient` / `ResponseStream` properties | `IHttpConnection? Connection` (`Stream`, endpoints, `Dispose`) |
| `IsEndOfRequest` | `IsEndOfMessage` |
| `IsUnableToParseHttp` / `IsRequestTimedOut` | `HasParsingErrors` |
| `UserContext`, `CancellationTokenSource` | removed |
| `Headers` (uppercase keys, last duplicate wins) | `Headers` (uppercase keys, case-insensitive lookup, duplicates comma-joined per RFC 9110 §5.2) |
| `new HttpSender().SendTcpResponseAsync(request, response)` | `request.SendResponseAsync(response)` extension |
| `HttpResponse.Body` (`MemoryStream`) | `HttpResponse.Body` (`ReadOnlyMemory<byte>`) |
| `uri.Host.GetIPv4Address()` (sync) | `await uri.Host.GetIPv4AddressAsync()` |
| `TcpClientEx` / `UriEx` / `HttpListenerEx` classes | `TcpClientExtensions` / `UriExtensions` / `HttpListenerExtensions` |
| One message per connection, sequential handling | Multiple messages per keep-alive connection, concurrent connections (emissions interleave) |

Behavior note: v6 closed every connection after one message. v7 honors keep-alive, so a consumer must respond per message; `SendResponseAsync` auto mode preserves v6-style close semantics whenever the client asked for `Connection: close` or HTTP/1.0.

`HttpRequestResponse` and `HttpResponse` are `record` types, so `with` expressions work for derived copies. Their equality is deliberately reference-based (a received message carries a live connection and a body buffer, so member-wise comparison would be misleading).

## History

SimpleHttpListener.Rx is the successor to [Simple HTTP Listener PCL](https://github.com/1iveowl/Simple-Http-Listener-PCL). The legacy package remains available as [SimpleHttpListener](https://www.nuget.org/packages/SimpleHttpListener).
