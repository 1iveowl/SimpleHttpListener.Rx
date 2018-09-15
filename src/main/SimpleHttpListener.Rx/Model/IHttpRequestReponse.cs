using HttpMachine;
using ISimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Model
{
    public interface IHttpRequestResponse : IHttpResponse, IHttpRequest
    {
        MessageType MessageType { get; }

        bool HasParsingErrors { get; }
    }
}
