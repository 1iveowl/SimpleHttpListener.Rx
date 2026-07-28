using SimpleHttpListener.Rx.Analyzers.Tests.TestHelpers;
using Xunit;

namespace SimpleHttpListener.Rx.Analyzers.Tests;

/// <summary>
/// SHLRX002 is built to under-report: the positives are the shapes where the upgrade path
/// provably walks away from the connection, and everything that escapes the rule's sight is
/// expected to stay silent.
/// </summary>
public class UpgradeConnectionAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Reactive.Linq;
        using System.Threading.Tasks;
        using SimpleHttpListener.Rx;
        using SimpleHttpListener.Rx.Model;

        """;

    private static Task VerifyAsync(string source) =>
        RxVerifier.VerifyAnalyzerWithListenerAsync<UpgradeConnectionAnalyzer>(Preamble + source);

    [Fact]
    public Task Returning_from_an_upgrade_branch_is_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if ({|SHLRX002:request.IsUpgradeRequest|})
                {
                    return;
                }

                _ = request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task An_empty_upgrade_branch_at_the_end_of_a_method_is_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if ({|SHLRX002:request.IsUpgradeRequest|})
                {
                }
            }
        }
        """);

    [Fact]
    public Task An_upgrade_branch_that_only_logs_is_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if ({|SHLRX002:request.IsUpgradeRequest|})
                {
                    Console.WriteLine("rejecting upgrade for " + request.Path);
                    return;
                }

                _ = request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task An_inverted_test_whose_upgrade_path_falls_through_is_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if ({|SHLRX002:!request.IsUpgradeRequest|})
                {
                    _ = request.SendResponseAsync(new HttpResponse());
                }
            }
        }
        """);

    [Fact]
    public Task An_upgrade_branch_inside_a_subscribe_lambda_is_flagged() => VerifyAsync(
        """
        class C
        {
            void M(IObservable<HttpRequestResponse> requests) =>
                requests.Subscribe(request =>
                {
                    if ({|SHLRX002:request.IsUpgradeRequest|})
                    {
                        return;
                    }

                    _ = request.SendResponseAsync(new HttpResponse());
                });
        }
        """);

    [Fact]
    public Task Completing_the_handshake_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            async Task Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    await request.AcceptWebSocketAsync();
                    return;
                }

                await request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task Disposing_the_connection_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    request.Connection?.Dispose();
                    return;
                }

                _ = request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task Disposing_in_a_finally_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            async Task Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    try
                    {
                        await request.AcceptWebSocketAsync();
                    }
                    finally
                    {
                        request.Connection?.Dispose();
                    }

                    return;
                }
            }
        }
        """);

    [Fact]
    public Task Declining_by_responding_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            async Task Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    await request.SendResponseAsync(new HttpResponse { StatusCode = 400 });
                    return;
                }

                await request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task Handing_the_request_to_another_method_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    _ = HandleWebSocketAsync(request);
                    return;
                }

                _ = request.SendResponseAsync(new HttpResponse());
            }

            static async Task HandleWebSocketAsync(HttpRequestResponse request)
            {
                try
                {
                    await request.AcceptWebSocketAsync();
                }
                finally
                {
                    request.Connection?.Dispose();
                }
            }
        }
        """);

    [Fact]
    public Task Storing_the_connection_in_a_field_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            IHttpConnection? _pending;

            void Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    _pending = request.Connection;
                    return;
                }
            }
        }
        """);

    [Fact]
    public Task Handling_after_the_branch_rejoins_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request)
            {
                if (request.IsUpgradeRequest)
                {
                    Console.WriteLine("upgrade");
                }

                _ = request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task An_upgrade_branch_inside_a_loop_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse[] requests)
            {
                foreach (var request in requests)
                {
                    if (request.IsUpgradeRequest)
                    {
                        continue;
                    }

                    _ = request.SendResponseAsync(new HttpResponse());
                }
            }
        }
        """);

    [Fact]
    public Task Handling_after_an_enclosing_loop_is_not_flagged() => VerifyAsync(
        """
        class C
        {
            void Handle(HttpRequestResponse request, bool ready)
            {
                while (!ready)
                {
                    if (request.IsUpgradeRequest)
                    {
                        break;
                    }
                }

                // Reached on the upgrade path too, so the branch is not walking away from
                // anything. Only statements in the function body itself are visible to the
                // rule, which is why a conditional nested inside a loop is left alone.
                _ = request.SendResponseAsync(new HttpResponse());
            }
        }
        """);

    [Fact]
    public Task A_test_on_something_other_than_the_listener_type_is_not_flagged() => VerifyAsync(
        """
        class Other
        {
            public bool IsUpgradeRequest { get; init; }
        }

        class C
        {
            void Handle(Other request)
            {
                if (request.IsUpgradeRequest)
                {
                    return;
                }
            }
        }
        """);

    [Fact]
    public Task Code_without_the_listener_in_the_compilation_is_not_flagged() =>
        RxVerifier.VerifyAnalyzerWithoutRxAsync<UpgradeConnectionAnalyzer>(
            """
            class Request
            {
                public bool IsUpgradeRequest { get; set; }
            }

            class C
            {
                void Handle(Request request)
                {
                    if (request.IsUpgradeRequest)
                    {
                        return;
                    }
                }
            }
            """);
}
