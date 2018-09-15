using System.IO;
using ISimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Model.Base;

namespace SimpleHttpListener.Rx.Model
{
    public class HttpResponse : HttpHeaderBase, IHttpResponse
    {

        public int MajorVersion { get;  set; }
        public int MinorVersion { get; set; }
        public int StatusCode { get; set; }
        public string ResponseReason { get; set; }
        
        public MemoryStream Body { get; set; } = new MemoryStream();

    }
}
