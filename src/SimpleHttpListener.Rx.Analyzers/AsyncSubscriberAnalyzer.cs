using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SimpleHttpListener.Rx.Analyzers;

/// <summary>
/// SHLRX001: reports an <c>async</c> delegate passed to an Rx <c>Subscribe</c> overload that
/// takes an <see cref="Action"/>, which binds it as <c>async void</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncSubscriberAnalyzer : DiagnosticAnalyzer
{
    private const string ObservableExtensionsMetadataName = "System.ObservableExtensions";
    private const string ObservableMetadataName = "System.IObservable`1";
    private const string SubscribeMethodName = "Subscribe";
    private const string AsyncStateMachineAttributeName = "AsyncStateMachineAttribute";

    /// <summary>Marks a diagnostic the code fix can act on; see <see cref="DiagnosticIds.FixableProperty"/>.</summary>
    private static readonly ImmutableDictionary<string, string?> FixableProperties =
        ImmutableDictionary<string, string?>.Empty.Add(DiagnosticIds.FixableProperty, bool.TrueString);

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.AsyncSubscriber,
        title: "Async delegate passed to Subscribe",
        messageFormat:
            "This async delegate is bound as 'async void' by Subscribe: exceptions bypass OnError, "
            + "message order is not preserved, and nothing applies backpressure",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Subscribe takes an Action, so an async delegate passed to it is an async void method: "
            + "an exception it throws is raised on the thread pool instead of reaching the subscription's "
            + "OnError handler, a second message can start before the first finishes, and the source is "
            + "never slowed to the rate the handler can keep up with. Project the work into the pipeline "
            + "with Observable.FromAsync and flatten it with Concat (preserves order) or Merge (allows "
            + "concurrency) instead.",
        helpLinkUri: DiagnosticIds.HelpLink(DiagnosticIds.AsyncSubscriber));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            // Nothing to match in a compilation without Rx: one symbol lookup and out.
            var observableExtensions = compilationStart.Compilation
                .GetTypeByMetadataName(ObservableExtensionsMetadataName);
            var observable = compilationStart.Compilation
                .GetTypeByMetadataName(ObservableMetadataName);

            if (observableExtensions is null || observable is null)
            {
                return;
            }

            var symbols = new SubscribeSymbols(observableExtensions, observable);

            compilationStart.RegisterOperationAction(
                operationContext => Analyze(operationContext, symbols),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, SubscribeSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (!symbols.IsRxSubscribe(invocation.TargetMethod))
        {
            return;
        }

        // The rewrite changes the stream's element type, so the remaining arguments have to
        // belong to an overload set known to accept the new one. Only Rx's own Subscribe
        // qualifies; a third-party Subscribe over IObservable<T> still warns, without a fix.
        var fixable = symbols.IsObservableExtensions(invocation.TargetMethod);

        foreach (var argument in invocation.Arguments)
        {
            // Parameter is correct whether or not the extension call was written in reduced
            // form, so this needs no special casing for ObservableExtensions.Subscribe(xs, f).
            if (argument.Parameter is not { } parameter || !IsActionDelegate(parameter.Type))
            {
                continue;
            }

            switch (Unwrap(argument.Value))
            {
                case IAnonymousFunctionOperation { Symbol.IsAsync: true } lambda:
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        lambda.Syntax.GetLocation(),
                        fixable ? FixableProperties : null));
                    break;

                // The sneakier form: 'async' appears only at the method's declaration, which
                // may not even be in this compilation, so nothing is visible at the call site.
                case IMethodReferenceOperation { Method: { ReturnsVoid: true } method } methodReference
                    when IsAsync(method):
                    context.ReportDiagnostic(Diagnostic.Create(Rule, methodReference.Syntax.GetLocation()));
                    break;
            }
        }
    }

    /// <summary>Peels the conversion and delegate-creation wrappers around an argument value.</summary>
    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    break;
                case IDelegateCreationOperation delegateCreation:
                    operation = delegateCreation.Target;
                    break;
                default:
                    return operation;
            }
        }
    }

    private static bool IsActionDelegate(ITypeSymbol type) =>
        type is INamedTypeSymbol
        {
            Name: nameof(Action),
            ContainingNamespace: { Name: nameof(System), ContainingNamespace.IsGlobalNamespace: true }
        };

    /// <summary>
    /// Whether <paramref name="method"/> is an async method. <see cref="IMethodSymbol.IsAsync"/>
    /// only covers source; a method compiled into another assembly carries
    /// <c>AsyncStateMachineAttribute</c> instead, which is what makes a referenced
    /// <c>async void</c> handler detectable at all.
    /// </summary>
    private static bool IsAsync(IMethodSymbol method)
    {
        if (method.IsAsync)
        {
            return true;
        }

        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == AsyncStateMachineAttributeName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Per-compilation symbols, looked up once instead of per invocation.</summary>
    private sealed class SubscribeSymbols(INamedTypeSymbol observableExtensions, INamedTypeSymbol observable)
    {
        internal bool IsObservableExtensions(IMethodSymbol method) =>
            SymbolEqualityComparer.Default.Equals(method.ContainingType, observableExtensions);

        internal bool IsRxSubscribe(IMethodSymbol method)
        {
            if (method.Name != SubscribeMethodName)
            {
                return false;
            }

            if (IsObservableExtensions(method))
            {
                return true;
            }

            if (!method.IsExtensionMethod)
            {
                return false;
            }

            // Any extension method named Subscribe over IObservable<T> has the same contract
            // and the same footgun, whoever supplies it. ReducedFrom recovers the declared
            // form (whose first parameter is the 'this' parameter) when the call was written
            // as xs.Subscribe(...); a call written as Extensions.Subscribe(xs, ...) is
            // already in that form.
            var declared = method.ReducedFrom ?? method;
            var subscribed = declared.Parameters.FirstOrDefault()?.Type;

            return subscribed is INamedTypeSymbol named
                && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, observable);
        }
    }
}
