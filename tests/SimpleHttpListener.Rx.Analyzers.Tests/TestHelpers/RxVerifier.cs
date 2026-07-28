using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace SimpleHttpListener.Rx.Analyzers.Tests.TestHelpers;

/// <summary>
/// Analyzer and code-fix verifiers whose test compilations reference System.Reactive, so
/// sources under test can use the real <c>Subscribe</c> overloads rather than a stand-in.
/// </summary>
internal static class RxVerifier
{
    private static readonly ReferenceAssemblies WithRx = ReferenceAssemblies.Net.Net80
        .AddPackages([new PackageIdentity("System.Reactive", "7.0.0")]);

    internal static DiagnosticResult Diagnostic(string diagnosticId) =>
        new(diagnosticId, DiagnosticSeverity.Warning);

    internal static Task VerifyAnalyzerAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        RunAnalyzerAsync<TAnalyzer>(source, WithRx, expected);

    /// <summary>
    /// Runs against a compilation with no System.Reactive reference, which is how the
    /// analyzer's compilation-start bail-out gets exercised.
    /// </summary>
    internal static Task VerifyAnalyzerWithoutRxAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        RunAnalyzerAsync<TAnalyzer>(source, ReferenceAssemblies.Net.Net80, expected);

    /// <summary>
    /// Runs with <paramref name="librarySource"/> compiled to a real assembly and referenced
    /// as metadata. A project reference would not do: it hands Roslyn the other project's
    /// source symbols, where <c>IsAsync</c> is still true, so only this exercises the
    /// metadata path where <c>AsyncStateMachineAttribute</c> is all that remains.
    /// </summary>
    internal static async Task VerifyAnalyzerAgainstCompiledLibraryAsync<TAnalyzer>(
        string source,
        string librarySource)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = WithRx
        };

        test.TestState.AdditionalReferences.Add(
            await CompileToMetadataAsync(librarySource, test.ReferenceAssemblies));

        await test.RunAsync();
    }

    private static async Task<MetadataReference> CompileToMetadataAsync(
        string source,
        ReferenceAssemblies referenceAssemblies)
    {
        var references = await referenceAssemblies.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);

        var compilation = CSharpCompilation.Create(
            "TestLibrary",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                "The test library failed to compile: " + string.Join(Environment.NewLine, result.Diagnostics));
        }

        return MetadataReference.CreateFromImage(peStream.ToArray());
    }

    private static Task RunAnalyzerAsync<TAnalyzer>(
        string source,
        ReferenceAssemblies referenceAssemblies,
        DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = referenceAssemblies
        };

        test.ExpectedDiagnostics.AddRange(expected);

        return test.RunAsync();
    }

    internal static Task VerifyCodeFixAsync<TAnalyzer, TCodeFix>(
        string source,
        string fixedSource,
        int? codeActionIndex = null,
        params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            TestCode = source,
            FixedCode = fixedSource,
            ReferenceAssemblies = WithRx,
            CodeActionIndex = codeActionIndex
        };

        test.ExpectedDiagnostics.AddRange(expected);

        return test.RunAsync();
    }
}
