using System.Text;
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
        var datagram = ComposeResponse(response, closeConnection);

        await connection.Stream.WriteAsync(datagram, cancellationToken).ConfigureAwait(false);
        await connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (closeConnection)
        {
            connection.Dispose();
        }
    }

    private static ReadOnlyMemory<byte> ComposeResponse(HttpResponse response, bool closeConnection)
    {
        var reasonPhrase = response.ReasonPhrase ?? ReasonPhrase.For(response.StatusCode);

        var stringBuilder = new StringBuilder();
        stringBuilder.Append(
            $"HTTP/{response.MajorVersion}.{response.MinorVersion} {response.StatusCode} {reasonPhrase}\r\n");

        var hasContentLength = false;
        var hasTransferEncoding = false;
        var hasConnection = false;

        foreach (var (name, value) in response.Headers)
        {
            hasContentLength |= name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
            hasTransferEncoding |= name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
            hasConnection |= name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

            stringBuilder.Append($"{name}: {value}\r\n");
        }

        // Keep-alive framing requires an explicit length on every response, including empty ones.
        if (!hasContentLength && !hasTransferEncoding)
        {
            stringBuilder.Append($"Content-Length: {response.Body.Length}\r\n");
        }

        if (!hasConnection)
        {
            var isHttp11OrLater = response.MajorVersion > 1
                || (response.MajorVersion == 1 && response.MinorVersion >= 1);

            if (closeConnection && isHttp11OrLater)
            {
                stringBuilder.Append("Connection: close\r\n");
            }
            else if (!closeConnection && !isHttp11OrLater)
            {
                stringBuilder.Append("Connection: keep-alive\r\n");
            }
        }

        stringBuilder.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());

        if (response.Body.IsEmpty)
        {
            return headerBytes;
        }

        var datagram = new byte[headerBytes.Length + response.Body.Length];
        headerBytes.CopyTo(datagram, 0);
        response.Body.CopyTo(datagram.AsMemory(headerBytes.Length));
        return datagram;
    }
}
