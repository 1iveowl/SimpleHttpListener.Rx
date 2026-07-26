using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using SimpleHttpListener.Rx.Internal;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

/// <summary>
/// How a response carrying neither <c>Content-Length</c> nor <c>Transfer-Encoding</c> is
/// framed. The default is pinned here deliberately: it is inherited from the parser package,
/// so a change upstream must fail a test rather than reach consumers silently.
/// </summary>
public class ResponseFramingTests
{
    private const string BodylessResponse =
        "HTTP/1.1 200 OK\r\nServer: UPnP/1.0\r\nST: upnp:rootdevice\r\n\r\n";

    private const string ResponseWithUnframedBody =
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nclose delimited body";

    private static IObservable<HttpRequestResponse> Parse(
        string payload,
        UnframedResponseMode unframedResponseMode = UnframedResponseMode.CompleteAtHeaders,
        bool holdOpen = false)
    {
        var stream = new DribbleStream(Encoding.ASCII.GetBytes(payload)) { HoldOpenAfterPayload = holdOpen };

        return HttpMessageParser.ParseConnection(
            new FakeConnection(stream), false, CancellationToken.None, unframedResponseMode);
    }

    [Fact]
    public void Default_option_is_complete_at_headers()
    {
        Assert.Equal(UnframedResponseMode.CompleteAtHeaders, new HttpListenerOptions().UnframedResponseMode);
    }

    [Fact]
    public async Task Bodyless_response_completes_at_the_blank_line_without_waiting_for_end_of_input()
    {
        // Holding the stream open proves the message does not wait for the connection to
        // close — this is what SSDP responses rely on.
        var message = await Parse(BodylessResponse, holdOpen: true)
            .FirstAsync()
            .ToTask()
            .WaitAsync(TestNetwork.Timeout);

        Assert.True(message.IsEndOfMessage);
        Assert.False(message.HasParsingErrors);
        Assert.Equal(200, message.StatusCode);
        Assert.True(message.Body.IsEmpty);
    }

    [Fact]
    public async Task Unframed_response_body_is_not_read_by_default()
    {
        var messages = await Parse(ResponseWithUnframedBody)
            .ToList()
            .ToTask()
            .WaitAsync(TestNetwork.Timeout);

        // The message ends at the blank line, so the body bytes are taken as the start of the
        // next message — which is why CloseDelimited exists.
        var response = messages[0];
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.Body.IsEmpty);
        Assert.Equal(2, messages.Count);
        Assert.True(messages[1].HasParsingErrors);
    }

    [Fact]
    public async Task Unframed_response_body_is_read_to_end_of_input_when_close_delimited()
    {
        var messages = await Parse(ResponseWithUnframedBody, UnframedResponseMode.CloseDelimited)
            .ToList()
            .ToTask()
            .WaitAsync(TestNetwork.Timeout);

        var response = Assert.Single(messages);
        Assert.False(response.HasParsingErrors);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("close delimited body", Encoding.ASCII.GetString(response.Body.Span));
    }

    [Fact]
    public async Task Requests_are_unaffected_by_close_delimited()
    {
        // A request without framing headers has no body (RFC 9112 §6), so it must still
        // complete without waiting for the connection to close.
        var message = await Parse(
                "GET /still/works HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n",
                UnframedResponseMode.CloseDelimited,
                holdOpen: true)
            .FirstAsync()
            .ToTask()
            .WaitAsync(TestNetwork.Timeout);

        Assert.True(message.IsEndOfMessage);
        Assert.False(message.HasParsingErrors);
        Assert.Equal("/still/works", message.Path);
    }

    [Fact]
    public void Ssdp_datagram_response_is_unaffected_by_close_delimited()
    {
        // A datagram is self-delimiting, so either mode must parse an SSDP response.
        foreach (var mode in Enum.GetValues<UnframedResponseMode>())
        {
            var message = HttpMessageParser.ParseDatagram(
                Encoding.ASCII.GetBytes(BodylessResponse), false, null,
                new IPEndPoint(IPAddress.Loopback, 1900), unframedResponseMode: mode);

            Assert.False(message.HasParsingErrors);
            Assert.True(message.IsEndOfMessage);
            Assert.Equal(200, message.StatusCode);
        }
    }
}
