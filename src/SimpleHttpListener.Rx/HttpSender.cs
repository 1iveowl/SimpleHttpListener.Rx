using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Unicode;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx;

/// <summary>
/// Sends <see cref="HttpResponse"/> replies over the connection a request arrived on.
/// </summary>
public static class HttpSender
{
    /// <summary>
    /// Sends <paramref name="response"/> on the connection <paramref name="request"/> arrived on.
    /// </summary>
    /// <param name="request">The received request; must have arrived over TCP.</param>
    /// <param name="response">The response to send.</param>
    /// <param name="closeConnection">
    /// Whether to close the connection after sending. <see langword="null"/> (auto) closes
    /// if and only if <see cref="HttpRequestResponse.ShouldKeepAlive"/> is <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="InvalidOperationException">The request has no connection (UDP).</exception>
    public static Task SendResponseAsync(
        this HttpRequestResponse request,
        HttpResponse response,
        bool? closeConnection = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Connection is not { } connection)
        {
            throw new InvalidOperationException(
                "The request has no connection to respond on. Responses can only be sent to messages received over TCP.");
        }

        return connection.SendResponseAsync(response, closeConnection ?? !request.ShouldKeepAlive, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="response"/> on <paramref name="connection"/>.
    /// </summary>
    /// <param name="connection">The connection to write to.</param>
    /// <param name="response">The response to send.</param>
    /// <param name="closeConnection">Whether to dispose the connection after sending.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public static async Task SendResponseAsync(
        this IHttpConnection connection,
        HttpResponse response,
        bool closeConnection = true,
        CancellationToken cancellationToken = default)
    {
        var (buffer, length) = ComposeResponse(response, closeConnection);

        try
        {
            await connection.Stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            await connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (closeConnection)
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Composes the response UTF-8 encoded into a rented buffer — no intermediate string or
    /// byte[] allocations. The caller must return the buffer to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    private static (byte[] Buffer, int Length) ComposeResponse(HttpResponse response, bool closeConnection)
    {
        var reasonPhrase = response.ReasonPhrase ?? ReasonPhrase.For(response.StatusCode);

        // Worst-case UTF-8 sizing: 3 bytes per char of caller-supplied text, 128 bytes for
        // the fixed parts (status line ints, auto Content-Length and Connection headers).
        var capacity = 128 + reasonPhrase.Length * 3 + response.Body.Length;

        foreach (var (name, value) in response.Headers)
        {
            capacity += (name.Length + value.Length) * 3 + 4;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        var written = 0;

        written += WriteUtf8(buffer.AsSpan(written),
            $"HTTP/{response.MajorVersion}.{response.MinorVersion} {response.StatusCode} {reasonPhrase}\r\n");

        var hasContentLength = false;
        var hasTransferEncoding = false;
        var hasConnection = false;

        foreach (var (name, value) in response.Headers)
        {
            hasContentLength |= name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
            hasTransferEncoding |= name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
            hasConnection |= name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

            written += WriteUtf8(buffer.AsSpan(written), $"{name}: {value}\r\n");
        }

        // Keep-alive framing requires an explicit length on every response, including empty
        // ones — except 1xx/204/304, which must not carry Content-Length (RFC 9110 §8.6).
        var statusForbidsContentLength = response.StatusCode < 200 || response.StatusCode is 204 or 304;

        if (!hasContentLength && !hasTransferEncoding && !statusForbidsContentLength)
        {
            written += WriteUtf8(buffer.AsSpan(written), $"Content-Length: {response.Body.Length}\r\n");
        }

        if (!hasConnection)
        {
            var isHttp11OrLater = response.MajorVersion > 1
                || (response.MajorVersion == 1 && response.MinorVersion >= 1);

            if (closeConnection && isHttp11OrLater)
            {
                written += WriteUtf8(buffer.AsSpan(written), $"Connection: close\r\n");
            }
            else if (!closeConnection && !isHttp11OrLater)
            {
                written += WriteUtf8(buffer.AsSpan(written), $"Connection: keep-alive\r\n");
            }
        }

        written += WriteUtf8(buffer.AsSpan(written), $"\r\n");

        response.Body.Span.CopyTo(buffer.AsSpan(written));
        written += response.Body.Length;

        return (buffer, written);
    }

    private static int WriteUtf8(
        Span<byte> destination,
        [InterpolatedStringHandlerArgument(nameof(destination))] ref Utf8.TryWriteInterpolatedStringHandler handler)
    {
        return Utf8.TryWrite(destination, ref handler, out var bytesWritten)
            ? bytesWritten
            : throw new InvalidOperationException("Response buffer was sized too small.");
    }
}
