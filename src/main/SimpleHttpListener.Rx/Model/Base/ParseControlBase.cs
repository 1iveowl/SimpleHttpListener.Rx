using System.Net;
using ISimpleHttpListener.Rx.Enum;
using ISimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Model.Base
{
    public abstract class ParseControlBase : IParseControl
    {
        public RequestType RequestType { get; set; }

        public bool IsEndOfRequest { get; set; }

        public bool IsRequestTimedOut { get; set; }

        public bool IsUnableToParseHttp { get; set; }

        public bool HasParsingErrors { get; set; }

        public IPEndPoint LocalIpEndPoint { get; set; }

        public string RemoteAddress { get; set; }

        public int RemotePort { get; set; }

        protected ParseControlBase() { }

    }
}
