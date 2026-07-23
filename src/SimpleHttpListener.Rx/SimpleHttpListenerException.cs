namespace SimpleHttpListener.Rx;

/// <summary>
/// Thrown for listener-specific failures, such as a hostname that cannot be resolved.
/// </summary>
public class SimpleHttpListenerException : Exception
{
    /// <summary>Creates the exception without a message.</summary>
    public SimpleHttpListenerException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SimpleHttpListenerException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public SimpleHttpListenerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
