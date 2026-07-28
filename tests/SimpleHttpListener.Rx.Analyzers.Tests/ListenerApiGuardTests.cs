using SimpleHttpListener.Rx.Model;
using Xunit;

namespace SimpleHttpListener.Rx.Analyzers.Tests;

/// <summary>
/// SHLRX002 matches the listener's types by name, and its tests compile against a stub rather
/// than the real assembly (net10.0 is beyond what the analyzer test harness can reference).
/// These tests close that gap by asserting the analyzer's own constants against the real
/// types, so a rename in the library fails here instead of leaving the rule silently matching
/// nothing in the field.
/// </summary>
public class ListenerApiGuardTests
{
    [Fact]
    public void The_request_type_still_has_the_metadata_name_the_analyzer_matches() =>
        Assert.Equal(ListenerApi.RequestTypeMetadataName, typeof(HttpRequestResponse).FullName);

    [Theory]
    [InlineData(ListenerApi.UpgradeProperty)]
    [InlineData(ListenerApi.ConnectionProperty)]
    public void The_request_type_still_has_the_properties_the_analyzer_reads(string propertyName) =>
        Assert.NotNull(typeof(HttpRequestResponse).GetProperty(propertyName));

    [Theory]
    [InlineData(typeof(WebSocketExtensions), ListenerApi.AcceptWebSocketMethod)]
    [InlineData(typeof(HttpSender), ListenerApi.SendResponseMethod)]
    public void The_handling_methods_are_still_named_as_the_rule_documents_them(Type type, string methodName) =>
        // By name only: SendResponseAsync is overloaded, and the rule reasons about the names
        // rather than any particular signature.
        Assert.Contains(type.GetMethods(), method => method.Name == methodName);
}
