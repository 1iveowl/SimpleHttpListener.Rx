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
/// Expected diagnostics come from <c>{|SHLRX001:...|}</c> markup in the sources themselves.
/// </summary>
internal static class RxVerifier
{
    private static readonly ReferenceAssemblies WithRx = ReferenceAssemblies.Net.Net80
        .AddPackages([new PackageIdentity("System.Reactive", "7.0.0")]);

    internal static Task VerifyAnalyzerAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        AnalyzerTest<TAnalyzer>(source, WithRx).RunAsync();

    /// <summary>
    /// Runs against a compilation with no System.Reactive reference, which is how the
    /// analyzer's compilation-start bail-out gets exercised.
    /// </summary>
    internal static Task VerifyAnalyzerWithoutRxAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        AnalyzerTest<TAnalyzer>(source, ReferenceAssemblies.Net.Net80).RunAsync();

    /// <summary>
    /// Runs against a compilation that also contains <see cref="ListenerStub"/>, for the
    /// rules that key on the listener's own types.
    /// </summary>
    internal static Task VerifyAnalyzerWithListenerAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        AnalyzerTest<TAnalyzer>(source, WithRx, ListenerStub.Source).RunAsync();

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
        var test = AnalyzerTest<TAnalyzer>(source, WithRx);

        test.TestState.AdditionalReferences.Add(
            await CompileToMetadataAsync(librarySource, test.ReferenceAssemblies));

        await test.RunAsync();
    }

    internal static Task VerifyCodeFixAsync<TAnalyzer, TCodeFix>(
        string source,
        string fixedSource,
        int? codeActionIndex = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new() =>
        new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            TestCode = source,
            FixedCode = fixedSource,
            ReferenceAssemblies = WithRx,
            CodeActionIndex = codeActionIndex
        }.RunAsync();

    /// <summary>Asserts the diagnostic is reported but offers nothing to apply.</summary>
    internal static Task VerifyNoFixAsync<TAnalyzer, TCodeFix>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new() =>
        VerifyCodeFixAsync<TAnalyzer, TCodeFix>(source, source);

    private static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> AnalyzerTest<TAnalyzer>(
        string source,
        ReferenceAssemblies referenceAssemblies,
        string? additionalSource = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = referenceAssemblies
        };

        if (additionalSource is not null)
        {
            test.TestState.Sources.Add(additionalSource);
        }

        return test;
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
}
