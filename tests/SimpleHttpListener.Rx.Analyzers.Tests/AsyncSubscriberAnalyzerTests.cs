using SimpleHttpListener.Rx.Analyzers.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Analyzers.Tests;

/// <summary>
/// Diagnostic locations are expressed as <c>{|SHLRX001:...|}</c> markup around the span the
/// analyzer is expected to report, so a test reads as the code it describes.
/// </summary>
public class AsyncSubscriberAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Reactive.Linq;
        using System.Threading.Tasks;

        """;

    private static Task VerifyAsync(string source) =>
        RxVerifier.VerifyAnalyzerAsync<AsyncSubscriberAnalyzer>(Preamble + source);

    [Fact]
    public Task Async_lambda_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|});
        }
        """);

    [Fact]
    public Task Async_lambda_with_block_body_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x =>
                {
                    await Task.Delay(x);
                }|});
        }
        """);

    [Fact]
    public Task Async_lambda_with_parenthesized_parameters_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async (int x) => await Task.Delay(x)|});
        }
        """);

    [Fact]
    public Task Async_anonymous_method_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async delegate (int x) { await Task.Delay(x); }|});
        }
        """);

    [Fact]
    public Task Async_lambda_in_onError_position_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe(
                    x => { },
                    {|SHLRX001:async (Exception e) => await Task.Delay(1)|});
        }
        """);

    [Fact]
    public Task Async_lambda_in_onCompleted_position_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe(
                    x => { },
                    e => { },
                    {|SHLRX001:async () => await Task.Delay(1)|});
        }
        """);

    [Fact]
    public Task Every_async_argument_in_one_call_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe(
                    {|SHLRX001:async x => await Task.Delay(x)|},
                    {|SHLRX001:async (Exception e) => await Task.Delay(1)|});
        }
        """);

    [Fact]
    public Task Async_lambda_on_the_cancellation_token_overload_is_flagged() => VerifyAsync(
        """
        using System.Threading;

        class C
        {
            void M(IObservable<int> xs, CancellationToken ct) =>
                xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|}, ct);
        }
        """);

    [Fact]
    public Task Async_lambda_in_unreduced_static_call_form_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) =>
                ObservableExtensions.Subscribe(xs, {|SHLRX001:async x => await Task.Delay(x)|});
        }
        """);

    [Fact]
    public Task Async_void_method_group_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) => xs.Subscribe({|SHLRX001:HandleAsync|});

            async void HandleAsync(int x) => await Task.Delay(x);
        }
        """);

    [Fact]
    public Task Async_void_method_group_from_another_type_is_flagged() => VerifyAsync(
        """
        class Handlers
        {
            public static async void HandleAsync(int x) => await Task.Delay(x);
        }

        class C
        {
            void M(IObservable<int> xs) => xs.Subscribe({|SHLRX001:Handlers.HandleAsync|});
        }
        """);

    [Fact]
    public Task Async_void_method_group_from_another_assembly_is_flagged() =>
        RxVerifier.VerifyAnalyzerAgainstCompiledLibraryAsync<AsyncSubscriberAnalyzer>(
            Preamble + """
            using Library;

            class C
            {
                void M(IObservable<int> xs) => xs.Subscribe({|SHLRX001:Handlers.HandleAsync|});
            }
            """,
            // Compiled separately: IMethodSymbol.IsAsync is false here, and only the
            // AsyncStateMachineAttribute in metadata still gives the handler away.
            """
            namespace Library;

            public static class Handlers
            {
                public static async void HandleAsync(int x) => await System.Threading.Tasks.Task.Delay(x);
            }
            """);

    [Fact]
    public Task Async_lambda_passed_to_a_custom_observable_subscribe_extension_is_flagged() => VerifyAsync(
        """
        static class MyExtensions
        {
            public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext, string label) =>
                source.Subscribe(onNext);
        }

        class C
        {
            void M(IObservable<int> xs) =>
                xs.Subscribe({|SHLRX001:async x => await Task.Delay(x)|}, "label");
        }
        """);

    [Fact]
    public Task Synchronous_lambda_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) => xs.Subscribe(x => Console.WriteLine(x));
        }
        """);

    [Fact]
    public Task Synchronous_method_group_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs) => xs.Subscribe(Handle);

            void Handle(int x) => Console.WriteLine(x);
        }
        """);

    [Fact]
    public Task Lambda_returning_a_task_to_a_func_parameter_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            IObservable<int> M(IObservable<int> xs) =>
                xs.SelectMany(async x =>
                {
                    await Task.Delay(x);
                    return x;
                });
        }
        """);

    [Fact]
    public Task Async_lambda_inside_FromAsync_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            IDisposable M(IObservable<int> xs) =>
                xs.Select(x => Observable.FromAsync(async () => await Task.Delay(x)))
                  .Concat()
                  .Subscribe(_ => { });
        }
        """);

    [Fact]
    public Task Observer_overload_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<int> xs, IObserver<int> observer) => xs.Subscribe(observer);
        }
        """);

    [Fact]
    public Task Async_lambda_passed_to_an_unrelated_method_named_subscribe_is_not_flagged() => VerifyAsync(
        """
        class Bus
        {
            public void Subscribe(Action<int> handler) => handler(0);
        }

        class C
        {
            void M(Bus bus) => bus.Subscribe(async x => await Task.Delay(x));
        }
        """);

    [Fact]
    public Task Subscribe_without_rx_in_the_compilation_is_not_flagged() =>
        RxVerifier.VerifyAnalyzerWithoutRxAsync<AsyncSubscriberAnalyzer>(
            """
            using System;
            using System.Threading.Tasks;

            static class Fake
            {
                public static void Subscribe<T>(this IObservable<T> source, Action<T> onNext) { }
            }

            class C
            {
                void M(IObservable<int> xs) => xs.Subscribe(async x => await Task.Delay(x));
            }
            """);
}
