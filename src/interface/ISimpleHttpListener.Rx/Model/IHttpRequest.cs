namespace ISimpleHttpListener.Rx.Model
{
    public interface IHttpRequest : IParseControl, IHttpCommon
    {
        bool ShouldKeepAlive { get; }
        object UserContext { get; }
        string Method { get;}
        string RequestUri { get; }
        string Path { get; }
        string QueryString { get; }
        string Fragment { get;}
    }
}
