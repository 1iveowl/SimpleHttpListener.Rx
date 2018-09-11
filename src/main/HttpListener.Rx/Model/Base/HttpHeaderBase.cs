using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using IHttpListener.Rx.Model;

namespace HttpListener.Rx.Model.Base
{
    public abstract class HttpHeaderBase : ParseControlBase, IHttpHeaders
    {
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        public Stream ResponseStream { get; set; }

        public TcpClient TcpClient { get; set; }
    }
}
