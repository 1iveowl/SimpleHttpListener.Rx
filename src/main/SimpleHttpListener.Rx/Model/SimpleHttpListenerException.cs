using System;
using System.Collections.Generic;
using System.Text;

namespace SimpleHttpListener.Rx.Model
{
    public class SimpleHttpListenerException : Exception
    {
        public SimpleHttpListenerException() : base()
        {

        }

        public SimpleHttpListenerException(string message) : base(message)
        {

        }

        public SimpleHttpListenerException(string message, Exception inner) : base(message, inner)
        {

        }
    }
}
