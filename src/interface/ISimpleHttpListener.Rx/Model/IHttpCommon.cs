using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using ISimpleHttpListener.Rx.Enum;

namespace ISimpleHttpListener.Rx.Model
{
    public interface IHttpCommon
    {
        RequestType RequestType { get; }
        int MajorVersion { get; }
        int MinorVersion { get; }

        IDictionary<string, string> Headers { get; }

        MemoryStream Body { get; }

        int RemotePort { get; }

        string RemoteAddress { get; }

        Stream ResponseStream { get; }

        TcpClient TcpClient { get; }
    }
}
