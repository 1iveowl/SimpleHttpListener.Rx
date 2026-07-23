using System.Net;
using System.Net.Sockets;

namespace SimpleHttpListener.Rx.Internal;

internal sealed class TcpConnection : IHttpConnection
{
    private readonly TcpClient _client;

    internal TcpConnection(TcpClient client)
    {
        _client = client;
        LocalEndPoint = client.Client.LocalEndPoint as IPEndPoint;
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        Stream = client.GetStream();
    }

    public IPEndPoint? LocalEndPoint { get; }

    public IPEndPoint? RemoteEndPoint { get; }

    public Stream Stream { get; }

    public void Dispose()
    {
        Stream.Dispose();
        _client.Dispose();
    }
}
