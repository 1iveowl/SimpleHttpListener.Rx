using System.Collections.Frozen;
using System.Text;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

public class HttpSenderTests
{
    private static HttpRequestResponse Request(FakeConnection connection, bool shouldKeepAlive) => new()
    {
        MessageType = MessageType.Request,
        Transport = HttpTransport.Tcp,
        Method = "GET",
        MajorVersion = 1,
        MinorVersion = 1,
        ShouldKeepAlive = shouldKeepAlive,
        Headers = FrozenDictionary<string, string>.Empty,
        IsEndOfMessage = true,
        Connection = connection
    };

    // 16
    [Fact]
    public async Task Response_with_body_composes_exact_bytes()
    {
        var connection = new FakeConnection();

        await connection.SendResponseAsync(
            new HttpResponse
            {
                Headers = { ["Content-Type"] = "text/plain" },
                Body = "Hello"u8.ToArray()
            },
            closeConnection: false);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nHello",
            Encoding.ASCII.GetString(connection.WrittenBytes));
    }

    // 17
    [Fact]
    public async Task Response_without_body_emits_content_length_zero_and_no_extra_blank_line()
    {
        var connection = new FakeConnection();

        await connection.SendResponseAsync(new HttpResponse(), closeConnection: false);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            Encoding.ASCII.GetString(connection.WrittenBytes));
    }

    [Fact]
    public async Task Non_ascii_header_values_are_utf8_encoded()
    {
        var connection = new FakeConnection();

        await connection.SendResponseAsync(
            new HttpResponse { Headers = { ["X-Note"] = "café ☕" } },
            closeConnection: false);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nX-Note: café ☕\r\nContent-Length: 0\r\n\r\n"u8.ToArray(),
            connection.WrittenBytes);
    }

    [Fact]
    public async Task Caller_supplied_content_length_is_not_duplicated()
    {
        var connection = new FakeConnection();

        await connection.SendResponseAsync(
            new HttpResponse { Headers = { ["Content-Length"] = "5" }, Body = "Hello"u8.ToArray() },
            closeConnection: false);

        var text = Encoding.ASCII.GetString(connection.WrittenBytes);
        Assert.Equal(1, CountOccurrences(text, "Content-Length"));
    }

    // 18
    [Fact]
    public async Task Auto_mode_closes_connection_iff_request_is_not_keep_alive()
    {
        var keepAliveConnection = new FakeConnection();
        await Request(keepAliveConnection, shouldKeepAlive: true)
            .SendResponseAsync(new HttpResponse());
        Assert.False(keepAliveConnection.IsDisposed);

        var closingConnection = new FakeConnection();
        await Request(closingConnection, shouldKeepAlive: false)
            .SendResponseAsync(new HttpResponse());
        Assert.True(closingConnection.IsDisposed);
        Assert.Contains("Connection: close\r\n", Encoding.ASCII.GetString(closingConnection.WrittenBytes));
    }

    [Fact]
    public async Task Explicit_close_flag_overrides_keep_alive_semantics()
    {
        var forcedClose = new FakeConnection();
        await Request(forcedClose, shouldKeepAlive: true)
            .SendResponseAsync(new HttpResponse(), closeConnection: true);
        Assert.True(forcedClose.IsDisposed);

        var forcedOpen = new FakeConnection();
        await Request(forcedOpen, shouldKeepAlive: false)
            .SendResponseAsync(new HttpResponse(), closeConnection: false);
        Assert.False(forcedOpen.IsDisposed);
    }

    [Fact]
    public async Task Udp_request_without_connection_throws()
    {
        var request = new HttpRequestResponse
        {
            MessageType = MessageType.Request,
            Transport = HttpTransport.Udp,
            Headers = FrozenDictionary<string, string>.Empty
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => request.SendResponseAsync(new HttpResponse()));
    }

    [Fact]
    public async Task Http10_keep_alive_response_gets_keep_alive_header()
    {
        var connection = new FakeConnection();

        await connection.SendResponseAsync(
            new HttpResponse { MinorVersion = 0 },
            closeConnection: false);

        Assert.Contains("Connection: keep-alive\r\n", Encoding.ASCII.GetString(connection.WrittenBytes));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;

        for (var index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }
}
