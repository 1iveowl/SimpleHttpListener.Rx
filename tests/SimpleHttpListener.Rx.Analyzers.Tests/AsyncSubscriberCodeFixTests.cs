using SimpleHttpListener.Rx.Analyzers.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Analyzers.Tests;

/// <summary>
/// The fix offers two actions because the choice is semantic: index 0 is <c>Concat</c>
/// (one handler at a time, in order), index 1 is <c>Merge</c> (handlers may overlap).
/// </summary>
public class AsyncSubscriberCodeFixTests
{
    private const int ConcatFix = 0;
    private const int MergeFix = 1;

    private const string Preamble = """
        using System;
        using System.Reactive.Linq;
        using System.Threading.Tasks;

        """;

    private static Task VerifyFixAsync(string source, string fixedSource, int codeActionIndex) =>
        RxVerifier.VerifyCodeFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            Preamble + source,
            Preamble + fixedSource,
            codeActionIndex);

    [Fact]
    public Task Concat_fix_preserves_order() => VerifyFixAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|});
        }
        """,
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Select(x => Observable.FromAsync(async () => await Task.Delay(x))).Concat().Subscribe();
        }
        """,
        ConcatFix);

    [Fact]
    public Task Merge_fix_allows_concurrency() => VerifyFixAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|});
        }
        """,
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Select(x => Observable.FromAsync(async () => await Task.Delay(x))).Merge().Subscribe();
        }
        """,
        MergeFix);

    [Fact]
    public Task Block_bodied_lambda_is_rewritten() => VerifyFixAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x =>
                {
                    await Task.Delay(x);
                    Console.WriteLine(x);
                }|});
        }
        """,
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Select(x => Observable.FromAsync(async () =>
                {
                    await Task.Delay(x);
                    Console.WriteLine(x);
                })).Concat().Subscribe();
        }
        """,
        ConcatFix);

    [Fact]
    public Task Trailing_handlers_are_kept_on_the_rewritten_subscription() => VerifyFixAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe(
                    {|SHLRX001:async x => await Task.Delay(x)|},
                    ex => Console.WriteLine(ex),
                    () => Console.WriteLine("done"));
        }
        """,
        // The trailing handlers keep the line breaks the author gave them.
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Select(x => Observable.FromAsync(async () => await Task.Delay(x))).Concat().Subscribe(_ => { }, ex => Console.WriteLine(ex),
                    () => Console.WriteLine("done"));
        }
        """,
        ConcatFix);

    [Fact]
    public Task A_chained_source_expression_is_carried_over() => VerifyFixAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Where(x => x > 0).Subscribe({|SHLRX001:async x => await Task.Delay(x)|});
        }
        """,
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Where(x => x > 0).Select(x => Observable.FromAsync(async () => await Task.Delay(x))).Concat().Subscribe();
        }
        """,
        ConcatFix);

    [Fact]
    public Task Missing_rx_linq_using_is_added() =>
        RxVerifier.VerifyCodeFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            """
            using System;
            using System.Threading.Tasks;

            class C
            {
                void M(IObservable<int> xs) =>
                    xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|});
            }
            """,
            """
            using System;
            using System.Threading.Tasks;
            using System.Reactive.Linq;

            class C
            {
                void M(IObservable<int> xs) =>
                    xs.Select(x => Observable.FromAsync(async () => await Task.Delay(x))).Concat().Subscribe();
            }
            """,
            ConcatFix);

    [Fact]
    public Task A_conflicting_Observable_type_in_scope_does_not_break_the_fix() => VerifyFixAsync(
        """
        class Observable
        {
        }

        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|});
        }
        """,
        """
        class Observable
        {
        }

        class C
        {
            void M(IObservable<int> xs) =>
                xs.Select(x => System.Reactive.Linq.Observable.FromAsync(async () => await Task.Delay(x))).Concat().Subscribe();
        }
        """,
        ConcatFix);

    [Fact]
    public Task An_async_anonymous_method_offers_no_fix() =>
        RxVerifier.VerifyNoFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            Preamble + """
            class C
            {
                void M(IObservable<int> xs) =>
                    xs.Subscribe({|SHLRX001:async delegate (int x) { await Task.Delay(x); }|});
            }
            """);

    [Fact]
    public Task A_custom_subscribe_extension_offers_no_fix() =>
        RxVerifier.VerifyNoFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            Preamble + """
            static class Ext
            {
                // Typed to the element, so the rewritten IObservable<Unit> would not bind here.
                public static IDisposable Subscribe(this IObservable<string> source, Action<string> onNext, string label) =>
                    ObservableExtensions.Subscribe(source, onNext);
            }

            class C
            {
                void M(IObservable<string> xs) =>
                    xs.Subscribe({|SHLRX001:async s => await Task.Delay(s.Length)|}, "label");
            }
            """);

    [Fact]
    public Task Method_group_form_offers_no_fix() =>
        // Rewriting this would mean changing HandleAsync's own signature.
        RxVerifier.VerifyNoFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            Preamble + """
            class C
            {
                void M(IObservable<int> xs) => xs.Subscribe({|SHLRX001:HandleAsync|});

                async void HandleAsync(int x) => await Task.Delay(x);
            }
            """);

    [Fact]
    public Task A_method_group_inside_an_enclosing_lambda_does_not_rewrite_the_outer_call() =>
        // The enclosing 'i => ...' lambda belongs to a different Subscribe call. Two things
        // keep the fix off it — the diagnostic is not marked fixable, and the rewrite is
        // anchored to the reported span — and this goes red if both are lost.
        RxVerifier.VerifyNoFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            Preamble + """
            class C
            {
                void M(IObservable<int> xs, IObservable<int> other) =>
                    other.Subscribe(i => xs.Subscribe({|SHLRX001:HandleAsync|}));

                async void HandleAsync(int x) => await Task.Delay(x);
            }
            """);

    [Fact]
    public Task An_async_handler_in_the_onError_position_offers_no_fix() =>
        // FromAsync projects the onNext path; there is no equivalent for an error handler.
        RxVerifier.VerifyNoFixAsync<AsyncSubscriberAnalyzer, AsyncSubscriberCodeFixProvider>(
            Preamble + """
            class C
            {
                void M(IObservable<int> xs) =>
                    xs.Subscribe(
                        x => { },
                        {|SHLRX001:async (Exception e) => await Task.Delay(1)|});
            }
            """);
}
