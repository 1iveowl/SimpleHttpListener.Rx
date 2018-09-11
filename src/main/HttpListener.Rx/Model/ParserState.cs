using System;
using System.Collections.Generic;
using System.Text;

namespace HttpListener.Rx.Model
{
    internal enum ParserState
    {
        Start,
        Parsing,
        Completed,
        Failed
    }
}
