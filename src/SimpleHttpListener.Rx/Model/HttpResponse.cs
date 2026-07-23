namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// An HTTP response to send with <see cref="HttpSender"/>.
/// </summary>
public sealed class HttpResponse
{
    /// <summary>Status code; defaults to <c>200</c>.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>
    /// Reason phrase; <see langword="null"/> uses the standard phrase for
    /// <see cref="StatusCode"/>.
    /// </summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>HTTP major version; defaults to <c>1</c>.</summary>
    public int MajorVersion { get; init; } = 1;

    /// <summary>HTTP minor version; defaults to <c>1</c>.</summary>
    public int MinorVersion { get; init; } = 1;

    /// <summary>Response headers (case-insensitive names).</summary>
    public IDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Response body; empty by default.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }
}
