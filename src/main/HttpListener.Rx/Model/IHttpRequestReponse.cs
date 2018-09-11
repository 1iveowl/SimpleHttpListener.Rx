using HttpMachine;
using IHttpListener.Rx.Model;

namespace HttpListener.Rx.Model
{
    public interface IHttpRequestResponse : IHttpResponse, IHttpRequest
    {
        MessageType MessageType { get; }
    }
}
