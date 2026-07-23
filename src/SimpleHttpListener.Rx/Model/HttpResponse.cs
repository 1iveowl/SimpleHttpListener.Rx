using System.Runtime.CompilerServices;

namespace SimpleHttpListener.Rx.Model;

/// <summary>
/// An HTTP response to send with <see cref="HttpSender"/>.
/// </summary>
/// <remarks>
/// A record, so <c>with</c> expressions work for derived copies. Equality is
/// reference-based (not member-based): <see cref="Headers"/> and <see cref="Body"/> would
/// compare by reference anyway, so member comparison would be misleading.
/// </remarks>
public sealed record HttpResponse
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

    /// <summary>Reference equality; see the class remarks.</summary>
    public bool Equals(HttpResponse? other) => ReferenceEquals(this, other);

    /// <summary>Identity-based hash, consistent with reference equality.</summary>
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
