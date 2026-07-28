using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace SimpleHttpListener.Rx.Analyzers;

/// <summary>
/// Rewrites <c>xs.Subscribe(async x =&gt; ...)</c> into a pipeline that keeps the asynchronous
/// work inside the observable: <c>xs.Select(x =&gt; Observable.FromAsync(async () =&gt; ...))</c>
/// flattened with <c>Concat</c> or <c>Merge</c>.
/// </summary>
/// <remarks>
/// Two separate fixes, never one silently chosen: <c>Concat</c> runs handlers one at a time
/// in order and applies backpressure, <c>Merge</c> lets them overlap. Picking for the user
/// would change how their program behaves.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AsyncSubscriberCodeFixProvider))]
[Shared]
public sealed class AsyncSubscriberCodeFixProvider : CodeFixProvider
{
    private const string RxLinqNamespace = "System.Reactive.Linq";
    private const string ConcatOperator = "Concat";
    private const string MergeOperator = "Merge";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.AsyncSubscriber);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            // Only the anonymous-function form can be rewritten locally; fixing a method
            // group would mean changing the signature of a method declared elsewhere.
            if (!diagnostic.Properties.ContainsKey(DiagnosticIds.LambdaProperty))
            {
                continue;
            }

            if (GetRewritableCall(root, diagnostic.Location.SourceSpan) is not { } rewrite)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Await each item in order (Select + FromAsync + Concat)",
                    cancellationToken => Task.FromResult(
                        ApplyFix(context.Document, root, rewrite, ConcatOperator)),
                    equivalenceKey: ConcatOperator),
                diagnostic);

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Await items concurrently (Select + FromAsync + Merge)",
                    cancellationToken => Task.FromResult(
                        ApplyFix(context.Document, root, rewrite, MergeOperator)),
                    equivalenceKey: MergeOperator),
                diagnostic);
        }
    }

    /// <summary>
    /// Matches the one shape that can be rewritten without guessing: an async lambda in the
    /// <c>onNext</c> position of an <c>xs.Subscribe(...)</c> member-access call.
    /// </summary>
    private static RewritableCall? GetRewritableCall(SyntaxNode root, TextSpan diagnosticSpan)
    {
        // Anchored to the reported span rather than walked up to from it. Walking would, for
        // a diagnostic that is not itself a lambda, find whatever lambda happens to enclose
        // it and rewrite that call instead — mangling unrelated code. The caller's
        // LambdaProperty check already rules that case out; this makes the rewrite correct on
        // its own terms rather than by relying on the analyzer to have filtered first.
        var lambda = root.FindNode(diagnosticSpan, getInnermostNodeForTie: true)
            as AnonymousFunctionExpressionSyntax;

        if (lambda is null
            || lambda.Span != diagnosticSpan
            || lambda.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || argumentList.Arguments.IndexOf(argument) != 0)
        {
            return null;
        }

        // An anonymous method has no parameter to carry over to Select; the diagnostic still
        // reports it, it just has no automatic fix.
        var parameterName = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized =>
                parenthesized.ParameterList.Parameters[0].Identifier,
            _ => default
        };

        return parameterName.IsKind(SyntaxKind.None)
            ? null
            : new RewritableCall(invocation, memberAccess, argumentList, lambda, parameterName);
    }

    private static Document ApplyFix(
        Document document,
        SyntaxNode root,
        RewritableCall rewrite,
        string flattenOperator)
    {
        var body = rewrite.Lambda.Body;

        // async () => <original body>, handed to Observable.FromAsync as a Func<Task>.
        var asyncWork = ParenthesizedLambdaExpression(
            ParameterList(),
            body is BlockSyntax block ? block : null,
            body as ExpressionSyntax)
            .WithAsyncKeyword(Token(SyntaxKind.AsyncKeyword));

        var fromAsync = InvocationExpression(
            MemberAccess(IdentifierName("Observable"), "FromAsync"),
            ArgumentList(SingletonSeparatedList(Argument(asyncWork))));

        var projection = SimpleLambdaExpression(Parameter(rewrite.ParameterName), fromAsync);

        var select = InvocationExpression(
            MemberAccess(rewrite.MemberAccess.Expression, "Select"),
            ArgumentList(SingletonSeparatedList(Argument(projection))));

        var flattened = InvocationExpression(MemberAccess(select, flattenOperator));

        // Whatever followed onNext (onError, onCompleted, a CancellationToken) still applies
        // to the rewritten subscription; only the handler itself moved into the pipeline.
        var remaining = rewrite.ArgumentList.Arguments.RemoveAt(0);

        var subscribeArguments = remaining.Count == 0
            ? ArgumentList()
            : ArgumentList(remaining.Insert(0, Argument(DiscardingHandler())));

        var replacement = InvocationExpression(MemberAccess(flattened, "Subscribe"), subscribeArguments)
            .WithTriviaFrom(rewrite.Invocation)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(rewrite.Invocation, replacement);

        return document.WithSyntaxRoot(EnsureRxLinqUsing(newRoot));
    }

    /// <summary>The <c>_ =&gt; { }</c> placeholder that keeps trailing handlers in their overload.</summary>
    private static SimpleLambdaExpressionSyntax DiscardingHandler() =>
        SimpleLambdaExpression(Parameter(Identifier("_")), Block());

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax target, string name) =>
        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, IdentifierName(name));

    /// <summary>
    /// Adds <c>using System.Reactive.Linq;</c> when absent — the rewrite introduces
    /// <c>Select</c>, <c>Concat</c>/<c>Merge</c> and <c>Observable.FromAsync</c>, which a file
    /// calling only <c>Subscribe</c> need not have imported.
    /// </summary>
    private static SyntaxNode EnsureRxLinqUsing(SyntaxNode root)
    {
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return root;
        }

        foreach (var existing in compilationUnit.Usings)
        {
            if (existing.Alias is null && existing.Name?.ToString() == RxLinqNamespace)
            {
                return root;
            }
        }

        return compilationUnit.AddUsings(
            UsingDirective(ParseName(RxLinqNamespace)).WithAdditionalAnnotations(Formatter.Annotation));
    }

    /// <summary>The parts of a <c>Subscribe</c> call the rewrite needs.</summary>
    private sealed class RewritableCall(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        ArgumentListSyntax argumentList,
        AnonymousFunctionExpressionSyntax lambda,
        SyntaxToken parameterName)
    {
        internal InvocationExpressionSyntax Invocation { get; } = invocation;

        internal MemberAccessExpressionSyntax MemberAccess { get; } = memberAccess;

        internal ArgumentListSyntax ArgumentList { get; } = argumentList;

        internal AnonymousFunctionExpressionSyntax Lambda { get; } = lambda;

        internal SyntaxToken ParameterName { get; } = parameterName;
    }
}
