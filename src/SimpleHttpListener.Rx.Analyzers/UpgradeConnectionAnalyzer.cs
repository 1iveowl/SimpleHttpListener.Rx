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
    private const string RequestMetadataName = "SimpleHttpListener.Rx.Model.HttpRequestResponse";
    private const string UpgradePropertyName = "IsUpgradeRequest";
    private const string ConnectionPropertyName = "Connection";

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
        helpLinkUri: DiagnosticIds.HelpLinkBase + "shlrx002");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var requestType = compilationStart.Compilation.GetTypeByMetadataName(RequestMetadataName);

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

        // Which side of the branch the upgrade request takes. A negated test with no else
        // means the upgrade case is the fall-through.
        var upgradePath = test.IsNegated ? conditional.WhenFalse : conditional.WhenTrue;

        var scanned = CollectUpgradePathOperations(conditional, upgradePath);

        if (scanned is null)
        {
            return;
        }

        foreach (var operation in scanned)
        {
            if (RetainsRequest(operation, test.RequestSymbol))
            {
                return;
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.Condition.Syntax.GetLocation()));
    }

    /// <summary>
    /// Matches <c>request.IsUpgradeRequest</c> and <c>!request.IsUpgradeRequest</c> where the
    /// message is a plain local or parameter. Anything more involved is left alone.
    /// </summary>
    private static UpgradeTest? GetUpgradeTest(IOperation condition, INamedTypeSymbol requestType)
    {
        var isNegated = false;

        while (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } negation)
        {
            isNegated = !isNegated;
            condition = negation.Operand;
        }

        if (condition is not IPropertyReferenceOperation { Property.Name: UpgradePropertyName } property
            || !SymbolEqualityComparer.Default.Equals(property.Property.ContainingType, requestType))
        {
            return null;
        }

        var requestSymbol = property.Instance switch
        {
            IParameterReferenceOperation parameter => (ISymbol)parameter.Parameter,
            ILocalReferenceOperation local => local.Local,
            _ => null
        };

        return requestSymbol is null ? null : new UpgradeTest(requestSymbol, isNegated);
    }

    /// <summary>
    /// The operations the upgrade path runs before leaving the enclosing function, or
    /// <see langword="null"/> when that cannot be determined locally.
    /// </summary>
    private static List<IOperation>? CollectUpgradePathOperations(
        IConditionalOperation conditional,
        IOperation? upgradePath)
    {
        var operations = new List<IOperation>();

        if (upgradePath is not null)
        {
            operations.AddRange(upgradePath.DescendantsAndSelf());

            // A branch that returns or throws never reaches the code after the conditional,
            // so what follows cannot be its rescue.
            if (Exits(upgradePath))
            {
                return operations;
            }
        }

        // Otherwise the upgrade path continues after the conditional, and anything there
        // still counts. Only a conditional sitting directly in the function body is followed;
        // inside a loop or a nested block the continuation is no longer plainly visible.
        if (conditional.Parent is not IBlockOperation block
            || block.Parent is not (IMethodBodyOperation or IAnonymousFunctionOperation or ILocalFunctionOperation))
        {
            return null;
        }

        var index = block.Operations.IndexOf(conditional);

        if (index < 0)
        {
            return null;
        }

        for (var i = index + 1; i < block.Operations.Length; i++)
        {
            operations.AddRange(block.Operations[i].DescendantsAndSelf());
        }

        return operations;
    }

    private static bool Exits(IOperation branch) => branch switch
    {
        IReturnOperation or IThrowOperation => true,
        IBlockOperation { Operations.Length: > 0 } block => Exits(block.Operations[block.Operations.Length - 1]),
        _ => false
    };

    /// <summary>
    /// Whether this operation keeps hold of the message in a way the rule cannot follow:
    /// reaching its <c>Connection</c>, or handing the message to anything at all. Reading a
    /// scalar property such as <c>Path</c> does not count — logging a rejected upgrade is
    /// still a leak.
    /// </summary>
    private static bool RetainsRequest(IOperation operation, ISymbol requestSymbol)
    {
        var referenced = operation switch
        {
            IParameterReferenceOperation parameter => (ISymbol)parameter.Parameter,
            ILocalReferenceOperation local => local.Local,
            _ => null
        };

        if (referenced is null || !SymbolEqualityComparer.Default.Equals(referenced, requestSymbol))
        {
            return false;
        }

        return operation.Parent is not IPropertyReferenceOperation { Property.Name: not ConnectionPropertyName };
    }

    private readonly struct UpgradeTest(ISymbol requestSymbol, bool isNegated)
    {
        internal ISymbol RequestSymbol { get; } = requestSymbol;

        internal bool IsNegated { get; } = isNegated;
    }
}
