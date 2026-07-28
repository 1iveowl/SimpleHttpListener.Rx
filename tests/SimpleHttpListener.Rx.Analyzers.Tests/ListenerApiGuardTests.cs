using SimpleHttpListener.Rx.Analyzers.Tests.TestHelpers;
using SimpleHttpListener.Rx.Model;
using Xunit;

namespace SimpleHttpListener.Rx.Analyzers.Tests;

/// <summary>
/// SHLRX002 matches the listener's types by name, and its tests compile against a stub rather
/// than the real assembly (net10.0 is beyond what the analyzer test harness can reference).
/// These tests close that gap: rename a member in the library and they fail here, instead of
/// the analyzer silently matching nothing in the field.
/// </summary>
public class ListenerApiGuardTests
{
    [Fact]
    public void The_request_type_still_has_the_metadata_name_the_analyzer_matches() =>
        Assert.Equal(ListenerStub.RequestTypeMetadataName, typeof(HttpRequestResponse).FullName);

    [Theory]
    [InlineData(ListenerStub.UpgradePropertyName)]
    [InlineData(ListenerStub.ConnectionPropertyName)]
    public void The_request_type_still_has_the_properties_the_analyzer_reads(string propertyName) =>
        Assert.NotNull(typeof(HttpRequestResponse).GetProperty(propertyName));

    [Theory]
    [InlineData(typeof(WebSocketExtensions), ListenerStub.AcceptWebSocketMethodName)]
    [InlineData(typeof(HttpSender), ListenerStub.SendResponseMethodName)]
    public void The_handling_methods_are_still_named_as_the_stub_declares_them(Type type, string methodName) =>
        // By name only: SendResponseAsync is overloaded, and the stub models just the shape
        // the analyzer's tests exercise.
        Assert.Contains(type.GetMethods(), method => method.Name == methodName);
}
