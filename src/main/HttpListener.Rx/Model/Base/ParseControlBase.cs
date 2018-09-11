using System.Net.Sockets;
using IHttpListener.Rx.Enum;
using IHttpListener.Rx.Model;

namespace HttpListener.Rx.Model.Base
{
    public abstract class ParseControlBase : IParseControl
    {
        public RequestType RequestType { get; set; }

        public bool IsEndOfRequest { get; set; }

        public bool IsRequestTimedOut { get; set; }

        public bool IsUnableToParseHttp { get; set; }

        public string RemoteAddress { get; set; }

        public int RemotePort { get; set; }

        protected ParseControlBase() { }

    }
}
