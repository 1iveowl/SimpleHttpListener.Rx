using System.IO;
using System.Threading;
using HttpMachine;
using SimpleHttpListener.Rx.Model.Base;

namespace SimpleHttpListener.Rx.Model
{
    public class HttpRequestResponse : HttpHeaderBase, IHttpRequestResponse
    {
        public MessageType MessageType { get; set; }
       
        public int StatusCode { get; set; }
        public string ResponseReason { get; set; }
        public int MajorVersion { get; set; }
        public int MinorVersion { get; set; }
        public bool ShouldKeepAlive { get; set; }
        public object UserContext { get; set; }
        public string Method { get; set; }
        public string RequestUri { get; set; }
        public string Path { get; set; }
        public string QueryString { get; set; }
        public string Fragment { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; internal set; }
        public bool IsChunked { get; set; }
        public MemoryStream Body { get; set; } = new MemoryStream();
    }
}
