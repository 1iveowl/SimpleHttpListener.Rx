using HttpMachine;

namespace ISimpleHttpListener.Rx.Model
{
    public interface IHttpRequestResponse : IHttpResponse, IHttpRequest
    {
        MessageType MessageType { get; }
    }
}
