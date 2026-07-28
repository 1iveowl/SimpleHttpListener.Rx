using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SimpleHttpListener.Rx.Analyzers;

/// <summary>
/// SHLRX002: reports an <c>IsUpgradeRequest</c> branch that leaves the connection to nobody.
/// </summary>
/// <remarks>
/// Deliberately narrow. Proving "on every path out of this branch the handshake completed or
/// the connection was disposed" is path-sensitive dataflow across lambdas and methods, which
/// no local rule can do honestly. So this reports one shape only — an upgrade path that never
/// touches the message again — and goes silent the moment the message or its connection is
/// passed anywhere, stored, or otherwise escapes where the rule cannot follow. A missed leak
/// is a cost worth paying; a rule people learn to suppress protects nothing.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UpgradeConnectionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.LeakedUpgradeConnection,
        title: "Upgrade request leaves its connection open",
        messageFormat:
            "This upgrade path neither completes the handshake nor disposes the connection, so the socket is leaked",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Once the listener emits a message with IsUpgradeRequest set it stops reading that "
            + "connection and ownership passes to the consumer. A path that returns without "
            + "completing the handshake (AcceptWebSocketAsync), answering the request "
            + "(SendResponseAsync, which declines the upgrade and closes), or disposing "
            + "Connection leaves a socket open that nothing will ever read or close. Nothing "
            + "fails in development; the symptom in production is socket exhaustion.",
        helpLinkUri: DiagnosticIds.HelpLink(DiagnosticIds.LeakedUpgradeConnection));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var requestType = compilationStart.Compilation.GetTypeByMetadataName(ListenerApi.RequestTypeMetadataName);

            if (requestType is null)
            {
                return;
            }

            compilationStart.RegisterOperationAction(
                operationContext => Analyze(operationContext, requestType),
                OperationKind.Conditional);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol requestType)
    {
        var conditional = (IConditionalOperation)context.Operation;

        if (GetUpgradeTest(conditional.Condition, requestType) is not { } test)
        {
            return;
        }

        var (requestSymbol, isNegated) = test;

        // Everything the rule can see has to live in one function body. A conditional nested
        // in a loop, a try, a using or any other construct has continuations this walk does
        // not follow — an enclosing finally that disposes, for one — so it is left alone.
        if (conditional.Parent is not IBlockOperation body
            || body.Parent is not (IMethodBodyOperation or IAnonymousFunctionOperation or ILocalFunctionOperation))
        {
            return;
        }

        // Which side of the branch the upgrade request takes. A negated test with no else
        // means the upgrade case is the fall-through.
        var upgradePath = isNegated ? conditional.WhenFalse : conditional.WhenTrue;

        if (UpgradePathOperations(body, conditional, upgradePath)
            .Any(operation => RetainsRequest(operation, requestSymbol)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.Condition.Syntax.GetLocation()));
    }

    /// <summary>
    /// Matches <c>request.IsUpgradeRequest</c> and <c>!request.IsUpgradeRequest</c> where the
    /// message is a plain local or parameter. Anything more involved is left alone.
    /// </summary>
    private static (ISymbol Request, bool IsNegated)? GetUpgradeTest(
        IOperation condition,
        INamedTypeSymbol requestType)
    {
        var isNegated = false;

        while (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } negation)
        {
            isNegated = !isNegated;
            condition = negation.Operand;
        }

        if (condition is not IPropertyReferenceOperation { Property.Name: ListenerApi.UpgradeProperty } property
            || !SymbolEqualityComparer.Default.Equals(property.Property.ContainingType, requestType))
        {
            return null;
        }

        return GetReferencedSymbol(property.Instance) is { } request ? (request, isNegated) : null;
    }

    /// <summary>
    /// Everything the upgrade path runs while the message is still its responsibility.
    /// Lazy, because the answer is usually settled by the first handling it finds.
    /// </summary>
    private static IEnumerable<IOperation> UpgradePathOperations(
        IBlockOperation body,
        IConditionalOperation conditional,
        IOperation? upgradePath)
    {
        var conditionalIndex = body.Operations.IndexOf(conditional);

        for (var i = 0; i < body.Operations.Length; i++)
        {
            var statement = body.Operations[i];

            // Everything before the test has already run on this path — taking ownership of
            // the connection up front is a normal way to make every exit safe. A local
            // function counts wherever it is written, since it is in scope throughout.
            var reachable = i < conditionalIndex || statement is ILocalFunctionOperation;

            // What follows the test is only reached when the branch falls through to it.
            reachable |= i > conditionalIndex && !Exits(upgradePath);

            if (!reachable)
            {
                continue;
            }

            foreach (var operation in statement.DescendantsAndSelf())
            {
                yield return operation;
            }
        }

        if (upgradePath is null)
        {
            yield break;
        }

        foreach (var operation in upgradePath.DescendantsAndSelf())
        {
            yield return operation;
        }
    }

    /// <summary>Whether this branch leaves the enclosing function rather than falling through.</summary>
    private static bool Exits(IOperation? branch) => branch switch
    {
        IReturnOperation or IThrowOperation => true,
        IBlockOperation { Operations.Length: > 0 } block => Exits(block.Operations[block.Operations.Length - 1]),
        _ => false
    };

    /// <summary>The local or parameter an operation refers to, if it is that simple.</summary>
    private static ISymbol? GetReferencedSymbol(IOperation? operation) => operation switch
    {
        IParameterReferenceOperation parameter => parameter.Parameter,
        ILocalReferenceOperation local => local.Local,
        _ => null
    };

    /// <summary>
    /// Whether this operation keeps hold of the message in a way the rule cannot follow:
    /// reaching its <c>Connection</c>, or handing the message to anything at all. Reading a
    /// scalar property such as <c>Path</c> does not count — logging a rejected upgrade is
    /// still a leak.
    /// </summary>
    private static bool RetainsRequest(IOperation operation, ISymbol requestSymbol)
    {
        if (GetReferencedSymbol(operation) is not { } referenced
            || !SymbolEqualityComparer.Default.Equals(referenced, requestSymbol))
        {
            return false;
        }

        return operation.Parent is not IPropertyReferenceOperation { Property.Name: not ListenerApi.ConnectionProperty };
    }
}
