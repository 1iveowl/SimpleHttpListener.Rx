namespace ISimpleHttpListener.Rx.Model
{
    public interface IParseControl
    {
        bool IsEndOfRequest { get; }

        bool IsRequestTimedOut { get; }

        bool IsUnableToParseHttp { get; }

        bool HasParsingErrors { get; }
    }
}
