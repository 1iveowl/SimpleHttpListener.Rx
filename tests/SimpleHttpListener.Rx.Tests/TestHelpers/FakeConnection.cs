using System.Net;

namespace SimpleHttpListener.Rx.Tests.TestHelpers;

/// <summary>
/// An <see cref="IHttpConnection"/> over an arbitrary stream that records disposal.
/// </summary>
internal sealed class FakeConnection(Stream stream) : IHttpConnection
{
    public FakeConnection() : this(new MemoryStream())
    {
    }

    public bool IsDisposed { get; private set; }

    public IPEndPoint? LocalEndPoint { get; init; } = new(IPAddress.Loopback, 80);

    public IPEndPoint? RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 54321);

    public Stream Stream => stream;

    public byte[] WrittenBytes => stream is MemoryStream memoryStream
        ? memoryStream.ToArray()
        : throw new InvalidOperationException("WrittenBytes requires a MemoryStream-backed connection.");

    public void Dispose() => IsDisposed = true;
}
