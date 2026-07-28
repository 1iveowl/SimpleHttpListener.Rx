namespace SimpleHttpListener.Rx.Analyzers;

/// <summary>
/// The names SHLRX002 matches the listener's own API by.
/// </summary>
/// <remarks>
/// Matching is by name, so a rename in the library would leave the rule quietly finding
/// nothing. These constants are the ones the analyzer actually uses, and the analyzer tests
/// assert them against the real types by reflection — keep that chain intact rather than
/// repeating a literal at either end of it.
/// </remarks>
internal static class ListenerApi
{
    internal const string RequestTypeMetadataName = "SimpleHttpListener.Rx.Model.HttpRequestResponse";

    internal const string UpgradeProperty = "IsUpgradeRequest";

    internal const string ConnectionProperty = "Connection";

    internal const string AcceptWebSocketMethod = "AcceptWebSocketAsync";

    internal const string SendResponseMethod = "SendResponseAsync";
}
