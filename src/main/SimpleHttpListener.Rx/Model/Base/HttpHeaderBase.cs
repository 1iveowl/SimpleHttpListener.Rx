using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using ISimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Model.Base
{
    public abstract class HttpHeaderBase : ParseControlBase, IHttpHeaders
    {
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Stream ResponseStream { get; set; }

        public TcpClient TcpClient { get; set; }
    }
}
