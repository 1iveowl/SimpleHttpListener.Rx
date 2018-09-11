namespace IHttpListener.Rx.Model
{
    public interface IParseControl
    {
        bool IsEndOfRequest { get; }

        bool IsRequestTimedOut { get; }

        bool IsUnableToParseHttp { get; }
    }
}
