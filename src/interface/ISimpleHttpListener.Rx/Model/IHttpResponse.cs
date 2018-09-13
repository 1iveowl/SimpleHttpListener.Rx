namespace ISimpleHttpListener.Rx.Model
{
    public interface IHttpResponse : IHttpCommon, IParseControl
    {
        int StatusCode { get; }
        string ResponseReason { get; }
    }
}
