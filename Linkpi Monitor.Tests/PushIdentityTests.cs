using System.Text.Json;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class PushIdentityTests
{
    private const string Original = """
        {"url":[{"des":"A","firmwareOnly":"A"},{"des":"B","firmwareOnly":"B","srcV":"2"}]}
        """;

    [Fact]
    public async Task RemovingFirstDestinationPreservesSecondAndDoesNotContaminateNewDestination()
    {
        var handler = new LinkPiTestHandler { PushJson = Original };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = (await client.GetSnapshotAsync(CancellationToken.None)).PushConfiguration;
        configuration.Destinations.RemoveAt(0);
        configuration.Destinations[0].Name = "Renamed B";
        configuration.Destinations.Add(new PushDestinationConfiguration { Name = "New" });
        await client.SavePushConfigurationAsync(configuration, TestContext.Current.CancellationToken);
        using var rpc = JsonDocument.Parse(Assert.Single(handler.Requests, r => r.RpcMethod == "push.update").Body);
        using var saved = JsonDocument.Parse(rpc.RootElement.GetProperty("params")[0].GetString()!);
        var destinations = saved.RootElement.GetProperty("url");
        Assert.Equal("B", destinations[0].GetProperty("firmwareOnly").GetString());
        Assert.Equal("Renamed B", destinations[0].GetProperty("des").GetString());
        Assert.Equal(JsonValueKind.String, destinations[0].GetProperty("srcV").ValueKind);
        Assert.False(destinations[1].TryGetProperty("firmwareOnly", out _));
    }

    [Fact]
    public async Task ExternalDestinationChangesRequireReloadInsteadOfMergingWrongRows()
    {
        var handler = new LinkPiTestHandler { PushJson = Original };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = (await client.GetSnapshotAsync(CancellationToken.None)).PushConfiguration;
        handler.PushJson = """{"url":[{"des":"B"},{"des":"A"}]}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SavePushConfigurationAsync(configuration, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(handler.Requests, r => r.RpcMethod == "push.update");
    }
}
