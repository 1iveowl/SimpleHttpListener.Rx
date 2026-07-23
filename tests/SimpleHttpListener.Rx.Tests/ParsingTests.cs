using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using SimpleHttpListener.Rx.Internal;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Tests;

public class ParsingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<IList<HttpRequestResponse>> ParseAsync(
        string payload,
        int[]? chunkSizes = null,
        bool headerCompletionCorrection = false)
    {
        var stream = new DribbleStream(Encoding.ASCII.GetBytes(payload), chunkSizes ?? []);
        var connection = new FakeConnection(stream);

        return await HttpMessageParser
            .ParseConnection(connection, headerCompletionCorrection, CancellationToken.None)
            .ToList()
            .ToTask()
            .WaitAsync(Timeout);
    }

    // 1
    [Fact]
    public async Task Get_request_without_body_parses_request_line_and_completes()
    {
        var messages = await ParseAsync("GET /a/b?q=1#f HTTP/1.1\r\nHost: x\r\n\r\n");

        var message = Assert.Single(messages);
        Assert.Equal(MessageType.Request, message.MessageType);
        Assert.Equal("GET", message.Method);
        Assert.Equal("/a/b?q=1#f", message.RequestUri);
        Assert.Equal("/a/b", message.Path);
        Assert.Equal("q=1", message.QueryString);
        Assert.Equal("f", message.Fragment);
        Assert.True(message.Body.IsEmpty);
        Assert.True(message.IsEndOfMessage);
        Assert.False(message.HasParsingErrors);
    }

    // 2
    [Fact]
    public async Task Post_with_content_length_delivers_exact_body_bytes()
    {
        var messages = await ParseAsync("POST /data HTTP/1.1\r\nHost: x\r\nContent-Length: 11\r\n\r\nhello world");

        var message = Assert.Single(messages);
        Assert.Equal("hello world", Encoding.ASCII.GetString(message.Body.Span));
        Assert.True(message.IsEndOfMessage);
    }

    // 3
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Headers_split_across_reads_parse_identically_to_single_buffer(int chunkSize)
    {
        const string payload = "GET /split HTTP/1.1\r\nHost: example.com\r\nX-Long-Header-Name: some long value\r\n\r\n";

        var whole = Assert.Single(await ParseAsync(payload));
        var split = Assert.Single(await ParseAsync(payload, [chunkSize]));

        Assert.Equal(whole.Method, split.Method);
        Assert.Equal(whole.Path, split.Path);
        Assert.Equal(whole.Headers, split.Headers);
        Assert.Equal(whole.IsEndOfMessage, split.IsEndOfMessage);
        Assert.False(split.HasParsingErrors);
    }

    // 4
    [Fact]
    public async Task Body_split_at_every_boundary_is_reassembled()
    {
        const string payload = "POST /b HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello";
        var payloadLength = payload.Length;

        for (var splitAt = 1; splitAt < payloadLength; splitAt++)
        {
            var messages = await ParseAsync(payload, [splitAt, int.MaxValue]);

            var message = Assert.Single(messages);
            Assert.False(message.HasParsingErrors);
            Assert.Equal("hello", Encoding.ASCII.GetString(message.Body.Span));
        }
    }

    // 5
    [Fact]
    public async Task Chunked_transfer_body_is_reassembled()
    {
        var messages = await ParseAsync(
            "POST /c HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");

        var message = Assert.Single(messages);
        Assert.True(message.IsChunked);
        Assert.Equal("hello world", Encoding.ASCII.GetString(message.Body.Span));
        Assert.True(message.IsEndOfMessage);
    }

    // 6
    [Fact]
    public async Task Duplicate_headers_are_comma_joined()
    {
        var messages = await ParseAsync("GET / HTTP/1.1\r\nHost: x\r\nX-Custom: a\r\nx-custom: b\r\n\r\n");

        var message = Assert.Single(messages);
        Assert.Equal("a, b", message.Headers["X-CUSTOM"]);
    }

    // 7
    [Fact]
    public async Task Header_lookup_is_case_insensitive()
    {
        var messages = await ParseAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\n\r\nhi");

        var message = Assert.Single(messages);
        Assert.Equal("2", message.Headers["content-length"]);
        Assert.Equal("2", message.Headers["CONTENT-LENGTH"]);
    }

    // 8
    [Fact]
    public async Task Http_response_message_parses_status_line()
    {
        var messages = await ParseAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi");

        var message = Assert.Single(messages);
        Assert.Equal(MessageType.Response, message.MessageType);
        Assert.Equal(200, message.StatusCode);
        Assert.Equal("OK", message.ReasonPhrase);
        Assert.Null(message.Method);
    }

    // 9
    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n\r\n", true, 1, 1)]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n", false, 1, 1)]
    [InlineData("GET / HTTP/1.0\r\nHost: a\r\n\r\n", false, 1, 0)]
    [InlineData("GET / HTTP/1.0\r\nHost: a\r\nConnection: keep-alive\r\n\r\n", true, 1, 0)]
    public async Task Keep_alive_flag_follows_version_and_connection_header(
        string payload, bool shouldKeepAlive, int majorVersion, int minorVersion)
    {
        var messages = await ParseAsync(payload);

        var message = Assert.Single(messages);
        Assert.False(message.HasParsingErrors);
        Assert.True(message.IsEndOfMessage);
        Assert.Equal(shouldKeepAlive, message.ShouldKeepAlive);
        Assert.Equal(majorVersion, message.MajorVersion);
        Assert.Equal(minorVersion, message.MinorVersion);
    }

    // 10
    [Fact]
    public async Task Message_end_is_emitted_while_connection_stays_open()
    {
        var stream = new DribbleStream("GET /fast HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray())
        {
            HoldOpenAfterPayload = true
        };
        var connection = new FakeConnection(stream);

        var message = await HttpMessageParser
            .ParseConnection(connection, false, CancellationToken.None)
            .FirstAsync()
            .ToTask()
            .WaitAsync(Timeout);

        Assert.Equal("/fast", message.Path);
        Assert.True(message.IsEndOfMessage);
    }

    // 11
    [Fact]
    public async Task Two_pipelined_requests_in_one_buffer_emit_two_messages()
    {
        var messages = await ParseAsync("GET /1 HTTP/1.1\r\nHost: a\r\n\r\nGET /2 HTTP/1.1\r\nHost: a\r\n\r\n");

        Assert.Equal(2, messages.Count);
        Assert.Equal("/1", messages[0].Path);
        Assert.Equal("/2", messages[1].Path);
        Assert.All(messages, m => Assert.False(m.HasParsingErrors));
    }

    // 12
    [Fact]
    public async Task Garbage_input_reports_parsing_error()
    {
        var messages = await ParseAsync("NOT HTTP\r\n");

        var message = Assert.Single(messages);
        Assert.True(message.HasParsingErrors);
    }

    // 13
    [Fact]
    public async Task Eof_truncated_message_reports_parsing_error()
    {
        var messages = await ParseAsync("GET / HTTP/1.1\r\nHost: x\r\n");

        var message = Assert.Single(messages);
        Assert.True(message.HasParsingErrors);
        Assert.False(message.IsEndOfMessage);
    }

    // 14
    [Fact]
    public void Ssdp_datagram_without_header_terminator_parses_with_correction_enabled()
    {
        var datagram = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n"u8.ToArray();

        var message = HttpMessageParser.ParseDatagram(
            datagram, true, null, new IPEndPoint(IPAddress.Loopback, 1900));

        Assert.False(message.HasParsingErrors);
        Assert.True(message.IsEndOfMessage);
        Assert.Equal("M-SEARCH", message.Method);
        Assert.Equal("239.255.255.250:1900", message.Headers["HOST"]);
        Assert.Equal("\"ssdp:discover\"", message.Headers["MAN"]);
        Assert.Null(message.Connection);
        Assert.Equal(HttpTransport.Udp, message.Transport);
    }

    [Fact]
    public void Datagram_parser_reuse_recovers_after_garbage_and_correction()
    {
        using var datagramParser = new DatagramParser();
        var valid = "NOTIFY * HTTP/1.1\r\nHOST: x\r\n\r\n"u8.ToArray();
        var garbage = "NOT HTTP\r\n"u8.ToArray();
        var truncated = "M-SEARCH * HTTP/1.1\r\nHOST: x\r\n"u8.ToArray();

        // valid → reused parser; garbage → recreated; valid again must still parse
        Assert.False(datagramParser.Parse(valid, false, null, null).HasParsingErrors);
        Assert.True(datagramParser.Parse(garbage, false, null, null).HasParsingErrors);

        var recovered = datagramParser.Parse(valid, false, null, null);
        Assert.False(recovered.HasParsingErrors);
        Assert.Equal("NOTIFY", recovered.Method);

        // correction path needs the EOF signal → recreated; valid again must still parse
        var corrected = datagramParser.Parse(truncated, true, null, null);
        Assert.False(corrected.HasParsingErrors);
        Assert.Equal("M-SEARCH", corrected.Method);

        Assert.False(datagramParser.Parse(valid, false, null, null).HasParsingErrors);
    }

    // 15
    [Fact]
    public void Ssdp_datagram_without_header_terminator_stays_incomplete_without_correction()
    {
        var datagram = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n"u8.ToArray();

        var message = HttpMessageParser.ParseDatagram(
            datagram, false, null, new IPEndPoint(IPAddress.Loopback, 1900));

        Assert.False(message.IsEndOfMessage);
        Assert.True(message.HasParsingErrors);
    }
}
