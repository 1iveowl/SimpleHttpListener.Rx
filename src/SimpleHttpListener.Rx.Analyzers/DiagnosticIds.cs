namespace SimpleHttpListener.Rx.Analyzers;

/// <summary>
/// Diagnostic IDs and the property keys analyzers hand to their code fixes.
/// </summary>
/// <remarks>
/// Compiled into both the analyzer and the code-fix assembly from this one file: the two
/// cannot reference each other (an analyzer assembly must not pull in the Workspaces layer —
/// rule RS1038 — and the code fixes are packed by the analyzer project), so sharing the
/// source is what keeps a single definition of each string.
/// <para>
/// Every ID here is public API from the release that first ships it: consumers write them
/// into <c>.editorconfig</c>, <c>NoWarn</c> and suppressions, so an ID must never be reused
/// for a different rule.
/// </para>
/// </remarks>
internal static class DiagnosticIds
{
    /// <summary>An async subscriber was passed to an Rx <c>Subscribe</c> overload.</summary>
    internal const string AsyncSubscriber = "SHLRX001";

    /// <summary>An upgrade-request branch neither completes the handshake nor disposes the connection.</summary>
    internal const string LeakedUpgradeConnection = "SHLRX002";

    /// <summary>
    /// Set on <see cref="AsyncSubscriber"/> diagnostics whose delegate is an anonymous
    /// function, which the code fix can rewrite. Absent for method-group arguments, where a
    /// fix would have to change the referenced method's signature.
    /// </summary>
    internal const string LambdaProperty = "IsLambda";

    internal const string HelpLinkBase = "https://github.com/1iveowl/SimpleHttpListener.Rx#";
}
