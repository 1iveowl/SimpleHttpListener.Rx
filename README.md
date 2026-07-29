# SimpleHttpListener.Rx

[![NuGet](https://img.shields.io/nuget/v/SimpleHttpListener.Rx?logo=nuget&label=SimpleHttpListener.Rx)](https://www.nuget.org/packages/SimpleHttpListener.Rx)
[![Downloads](https://img.shields.io/nuget/dt/SimpleHttpListener.Rx?logo=nuget&color=blue)](https://www.nuget.org/packages/SimpleHttpListener.Rx)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE.md)

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![System.Reactive](https://img.shields.io/badge/Rx-7.0.0-ff69b4.svg)](https://reactivex.io/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-compatible-success.svg)](#native-aot-and-trimming-760)
[![Analyzers](https://img.shields.io/badge/analyzers-included-8A2BE2.svg)](#analyzers)

An Rx-based HTTP listener for TCP, UDP, and UDP multicast traffic.

*Please star this project if you find it useful. Thank you.*

## Overview

SimpleHttpListener.Rx is a .NET library for HTTP message handling over application-controlled transports. It can listen for HTTP over TCP and UDP, including UDP multicast, which makes it useful for protocols such as [UPnP](https://openconnectivity.org/developer/specifications/upnp-resources/upnp)/[SSDP](https://en.wikipedia.org/wiki/Simple_Service_Discovery_Protocol).

The library is built with [Reactive Extensions](https://reactivex.io/), exposing incoming HTTP messages as an `IObservable<HttpRequestResponse>` for asynchronous processing.

### Version highlights

| Version | Highlights |
| --- | --- |
| **7.6.0** | [Native AOT and trimming](#native-aot-and-trimming-760): the library is annotated trim- and AOT-compatible, and publishing it into a native binary is verified rather than assumed. |
| **7.5.0** | [Declining an upgrade no longer leaks the connection](#websockets-710), and [analyzers](#analyzers-750) ship with the package to catch two easy-to-miss misuses at build time. |
| **7.4.0** | [Listener options](#listener-options-740): opt-in [raw wire capture](#raw-message-capture-740) of each UDP message's bytes as received, and opt-in RFC 9112 §6.3 framing for unframed response bodies. |
| **7.3.0** | [Reliable stop and restart](#subscription-lifecycle-730): stopping never surfaces as an error, and dispose-then-resubscribe never races the previous teardown. |
| **7.2.0** | `LocalEndPoint` on UDP messages reports [the interface the datagram arrived on](#local-endpoint-of-a-received-datagram-720) rather than the socket's bound address. |
| **7.1.0** | [WebSocket accept support](#websockets-710) — no ASP.NET/Kestrel required. |
| **7.0.0** | Modernization release: .NET 10, [HttpMachine.PCL](https://www.nuget.org/packages/HttpMachine.PCL) 6.0.x span-based parsing, HTTP keep-alive with concurrent connection handling, and a cleaned-up public API. See [Breaking changes in 7.0.0](#breaking-changes-in-700) if you are upgrading. |

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

### Subscription lifecycle (7.3.0+)

Two guarantees make dispose/resubscribe cycles safe on every platform, for both the TCP and UDP listeners:

- **Stopping is never an error.** Cancelling the token, disposing the last subscription, or closing the listener/socket out from under the loop completes the stream — it never reaches `OnError`. Cancelling a pending accept or receive raises `OperationCanceledException` on Windows but a `SocketException` ("Operation canceled") or `ObjectDisposedException` on Linux and macOS; all of them are a stop. A genuine socket failure while the listener is meant to be running still errors as before.
- **Restart never races teardown.** Teardown is synchronous — the listener is stopped by the time `Dispose()` on the subscription returns — and a subscription that starts while an earlier loop is still unwinding cannot have its listener stopped underneath it. Disposing the last subscriber of a shared (`RefCount`ed) stream and resubscribing in the same breath yields a working listener, with no delay needed in between.

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
        Console.WriteLine($"{r.Method} from {r.RemoteEndPoint} on {r.LocalEndPoint}");
    });
```

`ErrorCorrection.HeaderCompletionError` is optional. Some SSDP/UPnP devices send messages whose header section does not end with the required empty line (`\r\n\r\n`); with the correction enabled such messages still parse. Without it they are emitted with `HasParsingErrors == true` and `IsEndOfMessage == false`. UDP messages have `Connection == null`; replying is up to you (e.g. via `UdpClient.SendAsync`).

#### Local endpoint of a received datagram (7.2.0+)

`LocalEndPoint` reports the address the datagram was **actually delivered to**, not the socket's bound address, so it stays useful for the wildcard bind that multicast requires on macOS and Linux (`0.0.0.0:1900` above). It is derived per datagram from the packet information the socket reports:

- **Unicast** — the packet's destination address is the receiving interface address, and is used directly.
- **Multicast/broadcast** — the packet's destination is the group (e.g. `239.255.255.250`), so the address of the interface the datagram arrived on is resolved from its interface index instead. The interface lookup is cached and refreshed when the machine's addresses change.
- **If resolution fails** (unknown interface index, or a NIC with no IPv4 address) the socket's bound endpoint is reported, as before. The message is always emitted; only the accuracy of `LocalEndPoint` degrades.

The port is always the socket's bound port. IPv6 unicast is resolved the same way; IPv6 multicast still reports the bound endpoint.

This matters when you have to hand a peer an address to call you back on — for example a UPnP GENA `CALLBACK` URL. Before 7.2.0 that would be built from `0.0.0.0` on macOS and Linux, which strict devices reject.

#### Raw message capture (7.4.0+)

`HttpRequestResponse` gives you a parsed, normalised view — header names are upper-cased, repeated fields are comma-joined, field order is gone. For SSDP that loses the message itself: a datagram has no body, so the header block *is* the message. Enable capture to keep the bytes as they arrived:

```csharp
var options = new HttpListenerOptions { CaptureRawMessage = true };

var disposable = udpClient
    .ToHttpListenerObservable(options, cts.Token, ErrorCorrection.HeaderCompletionError)
    .Subscribe(message =>
    {
        // Decode leniently: real devices emit bytes no encoding decodes faithfully.
        var wire = Encoding.ASCII.GetString(message.RawMessage.Span);
        Console.WriteLine(wire);
    });
```

`RawMessage` is the message before parsing, normalisation or de-chunking, so original casing (`Cache-Control:`), field order, and repeated fields all survive — which is what makes a bug report against a parser reproducible. It is `ReadOnlyMemory<byte>` rather than a string on purpose: decoding is the consumer's call.

- **Off by default**, and free when off — no copy, no allocation on the receive path.
- **Always a copy**, never a slice of the pooled receive buffer, so it stays valid for as long as you hold the message.
- **Populated for messages that failed to parse too** (`HasParsingErrors == true`) — usually when you want it most. Every datagram is emitted, parsable or not.
- **Observational only**: enabling it changes no parsed value, no framing, and no emission.
- **UDP only.** A TCP message is framed out of a stream and may span reads, so attributing bytes to it would only be approximate; `RawMessage` stays empty for TCP messages, and the `HttpListenerOptions` overload for `TcpListener` exists only for future options.
- **Retention is yours.** A chatty SSDP network emits a lot of datagrams; holding every message keeps every captured buffer alive. Long-running capture belongs in a file log, not in memory.

### Listener options (7.4.0+)

Both `ToHttpListenerObservable` overloads accept an optional `HttpListenerOptions`. Every default matches the behaviour of the overloads without it, so options only ever add something you asked for:

| Option | Default | Effect |
| --- | --- | --- |
| `CaptureRawMessage` | `false` | Carry each message's bytes as received on [`RawMessage`](#raw-message-capture-740). UDP only. |
| `UnframedResponseMode` | `CompleteAtHeaders` | How to frame a response with neither `Content-Length` nor `Transfer-Encoding` (see below). |

```csharp
var options = new HttpListenerOptions
{
    CaptureRawMessage = true,
    UnframedResponseMode = UnframedResponseMode.CloseDelimited
};

var disposable = tcpListener.ToHttpListenerObservable(options, cts.Token).Subscribe(/* ... */);
```

#### Unframed response bodies

A response that carries neither `Content-Length` nor `Transfer-Encoding` has no self-evident end, and the two legal readings serve different protocols:

- **`CompleteAtHeaders`** (default) — the message ends at the blank line. This is what SSDP/HTTPU needs, since those responses are bodyless, and it is what every version before 7.4.0 did. If such a response *does* carry a body, those bytes are read as the start of the next message.
- **`CloseDelimited`** — the body runs to the end of the input, per RFC 9112 §6.3: the framing an HTTP/1.0 style response that ends by closing the connection needs. Enable it only for a stream you know carries such responses, since every unframed response then waits for the input to end before completing.

Requests are unaffected either way: a request with no framing headers has no body (RFC 9112 §6), and completes immediately under both modes.

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
- **Rejecting by responding closes the connection (7.5.0+).** `SendResponseAsync` in auto
  mode (`closeConnection: null`, the default) treats any response other than
  `101 Switching Protocols` to an upgrade request as declining it, and closes afterwards.
  Before 7.5.0 it kept the connection open — an upgrade request has `ShouldKeepAlive == true`
  — and since the listener had already stopped reading it, that socket leaked. Answering a
  `400` and considering the matter closed is now correct. Pass `closeConnection: false`
  explicitly if you intend to keep the connection and take over the stream yourself.
- `ws://` only: the listener runs on a raw `TcpListener` with no TLS, so browsers can only
  connect from non-HTTPS pages. This targets LAN/local/native-app scenarios.
- `AcceptWebSocketAsync` validates the handshake (version 13, key present) and throws
  `InvalidOperationException` for invalid requests; sending an error response is up to you.
- Sub-protocols are pass-through (`subProtocol` parameter), no negotiation logic.
- For the client side of the story, see
  [WebsocketClientLite.PCL](https://github.com/1iveowl/WebsocketClientLite.PCL) — the
  `samples/SimpleHttpListener.Rx.Sample.WebSocketClient` project connects it to the server
  sample's echo endpoint.

## Native AOT and trimming (7.6.0+)

The library is annotated `IsTrimmable` and `IsAotCompatible`, so a consuming app can publish
with `PublishAot` or `PublishTrimmed` and get no `IL` warnings from this package.

```xml
<PublishAot>true</PublishAot>
```

The annotation is backed by a test rather than a hope. A console app referencing the package
was published with native AOT and run, exercising the paths most likely to break under
trimming:

- **TCP** — accept, parse a request, send a response.
- **WebSockets** — the RFC 6455 handshake through `AcceptWebSocketAsync`, then an echo.
- **UDP** — datagram parse plus [`LocalEndPoint` resolution](#local-endpoint-of-a-received-datagram-720),
  which is the interesting one: it looks up network interfaces at runtime, and that is exactly
  the kind of code trimming tends to break.

All three work in the native binary, and the publish produces no trim or AOT warnings.

Two caveats worth stating plainly. This says nothing about *your* code or your other
dependencies — trimming is a whole-app property, and the compatible ones here are this
library, [System.Reactive](https://www.nuget.org/packages/System.Reactive) and
[HttpMachine.PCL](https://www.nuget.org/packages/HttpMachine.PCL), all of which carry the
trim annotation. And the [analyzers](#analyzers-750) are irrelevant to it either way: they run
in the compiler and never reach your output.

## Analyzers (7.5.0+)

Two mistakes this library makes easy are hard to see in a code review and invisible at
runtime until production: subscribing with an `async` handler, and letting an upgraded
connection go unowned. From 7.5.0 both are build warnings.

The analyzers ship inside the package — no install step, nothing extra to reference, and
they reach you through intermediate libraries too, so referencing something built on
SimpleHttpListener.Rx is enough.

They are build-time only: nothing is added to your output, and there is no runtime
dependency. Both rules are **warnings**, so a build never breaks because of them unless you
have opted into `TreatWarningsAsErrors` — where a failure is exactly what that flag is for.
The rules need the .NET 8.0.100 SDK or later; on anything older they simply do not load
(the compiler says so with `CS8032`) and your build is otherwise unaffected.

<a id="shlrx001"></a>

### SHLRX001 — async delegate passed to `Subscribe`

`Subscribe` takes an `Action<T>`, so an `async` handler passed to it is an `async void`
method. Three things follow, none of them visible at the call site:

- an exception it throws never reaches your `onError` handler — it is raised on the thread
  pool, where the usual outcome is a lost error or a crashed process;
- a second message can start being handled before the first has finished, so ordering is
  gone;
- nothing slows the source to the rate the handler can keep up with.

```csharp
// Warns — and the method-group form warns too, where 'async' is not even visible here:
xs.Subscribe(async x => await HandleAsync(x));
xs.Subscribe(HandleAsync);            // async void HandleAsync(int x)
```

Keep the asynchronous work inside the pipeline instead:

```csharp
xs.Select(x => Observable.FromAsync(() => HandleAsync(x)))
  .Concat()     // one at a time, in order — or .Merge() to allow overlap
  .Subscribe(_ => { }, ex => Log(ex));
```

Two code fixes are offered for the lambda form, and the choice is yours to make because it
changes behaviour: **Concat** preserves order and applies backpressure, **Merge** lets
handlers run concurrently. The method-group form gets the warning but no automatic fix —
the repair is to change that method's signature, which is not a safe local edit.

The rule fires on `Subscribe` over any `IObservable<T>`, not only on streams from this
library. If you already run VSTHRD101, MA0147, EPC17 or AsyncFixer03, expect two warnings on
the same lambda; both are right, and this one additionally catches the method-group form
that all of those miss.

<a id="shlrx002"></a>

### SHLRX002 — upgrade request leaves its connection open

When a message arrives with `IsUpgradeRequest` set, the listener stops reading that
connection and hands it to you. A path that walks away without completing the handshake,
answering the request, or disposing `Connection` leaves a socket that nothing will ever read
or close. Nothing fails in development; in production it surfaces as socket exhaustion under
a client that probes for WebSocket support.

```csharp
requests.Subscribe(request =>
{
    if (request.IsUpgradeRequest)     // Warns: this path abandons the connection
    {
        return;
    }

    _ = request.SendResponseAsync(new HttpResponse());
});
```

Any of these resolves it — including simply declining, which since 7.5.0 closes the
connection for you:

```csharp
if (request.IsUpgradeRequest)
{
    _ = request.SendResponseAsync(new HttpResponse { StatusCode = 400 });  // declines and closes
    // or: await request.AcceptWebSocketAsync();   — completes the handshake
    // or: request.Connection?.Dispose();          — closes it yourself
    return;
}
```

The rule is deliberately quiet. Proving that every path out of a branch either completed or
disposed is dataflow analysis across lambdas and methods, and a rule that guesses at it
produces noise people learn to suppress. So it reports one shape — an upgrade path that
never touches the message again — and says nothing as soon as the message or its connection
is passed somewhere, stored, or otherwise leaves its sight. Handing the request to another
method, the pattern in [WebSockets](#websockets-710) above, is silent by design. It will
miss real leaks; every warning it does give you is one.

### Turning a rule off

Both rules are meant to be right every time, so the first question when one fires is whether
it has a point. When it genuinely does not, silence it as narrowly as the situation needs:

```csharp
#pragma warning disable SHLRX001 // deliberate fire-and-forget; exceptions handled inside
xs.Subscribe(async x => await HandleAsync(x));
#pragma warning restore SHLRX001
```

```ini
# .editorconfig — per project or per folder, and the same knob tunes severity:
# 'suggestion' demotes it to an IDE hint, 'error' promotes it to a build failure.
[*.cs]
dotnet_diagnostic.SHLRX001.severity = none
```

```xml
<!-- Or bluntly, for a whole project -->
<NoWarn>$(NoWarn);SHLRX001;SHLRX002</NoWarn>
```

`.editorconfig` and `NoWarn` are the supported off-switches, and they silence a rule
completely. `ExcludeAssets="analyzers"` on the package reference does **not** remove them —
that has been measured, not assumed. The assembly still loads when a rule is set to `none`;
it just has nothing to say.

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
