using System.Collections.Generic;

namespace ISimpleHttpListener.Rx.Model
{
    public interface IHttpHeaders
    {
        IDictionary<string, string> Headers { get; }
    }
}
