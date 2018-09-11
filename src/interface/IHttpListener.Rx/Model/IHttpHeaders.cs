using System.Collections.Generic;

namespace IHttpListener.Rx.Model
{
    public interface IHttpHeaders
    {
        IDictionary<string, string> Headers { get; }
    }
}
