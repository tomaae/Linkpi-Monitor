using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class DashboardUpdateTests
{
    [Fact]
    public void ReconciliationRetainsItemsAndHandlesAddRemoveAndReorderWithoutReset()
    {
        var first = new Row(1, "first");
        var second = new Row(2, "second");
        var rows = new ObservableCollection<Row> { first, second, new(3, "removed") };
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => actions.Add(args.Action);
        CollectionReconciler.Update(rows, new[] { new Row(2, "updated"), new Row(4, "new"), new Row(1, "first") },
            row => row.Id, (old, incoming) => old.Name = incoming.Name);
        Assert.Same(second, rows[0]);
        Assert.Same(first, rows[2]);
        Assert.Equal("updated", second.Name);
        Assert.Equal([2, 4, 1], rows.Select(row => row.Id));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        actions.Clear();
        CollectionReconciler.Update(rows, rows.ToArray(), row => row.Id, (_, _) => { });
        Assert.Empty(actions);
    }

    [Fact]
    public async Task UnchangedConfigurationIsReusedButEditorsCannotMutateIt()
    {
        var handler = new LinkPiTestHandler { ConfigJson = """[{"id":0,"type":"vi","enable":false,"name":"HDMI"}]""" };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var first = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);
        var second = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);
        Assert.Same(first.Configuration, second.Configuration);
        var editable = first.CreateEditableCopy();
        editable.Configuration.General.Name = "unsaved";
        editable.Configuration.MainStream.Rtsp.Password = "changed";
        Assert.Equal("HDMI", first.Configuration.General.Name);
        Assert.NotEqual("changed", first.Configuration.MainStream.Rtsp.Password);
        handler.ConfigJson = handler.ConfigJson.Replace("HDMI", "New HDMI");
        var third = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);
        Assert.NotSame(first.Configuration, third.Configuration);
        Assert.Equal("New HDMI", third.Configuration.General.Name);
        handler.HardwareJson = """{"capability":{"encode":{"maxSize":"4K"}}}""";
        var fourth = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);
        Assert.NotSame(third.Configuration, fourth.Configuration);
    }

    [Fact]
    public Task DashboardRefreshKeepsChannelAndPushObjectsAndUpdatesProperties() => WpfTestHost.RunAsync(async () =>
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = """[{"id":0,"type":"vi","enable":false,"name":"Old"}]""",
            PushJson = """{"url":[{"des":"Old destination"}]}"""
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var window = new MainWindow();
        WpfTestHost.Set(window, "_client", client);
        try
        {
            await (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
            var channel = Assert.Single(window.Channels);
            var push = Assert.Single(window.PushDestinations);
            var notifications = new List<string?>();
            channel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            handler.ConfigJson = handler.ConfigJson.Replace("Old", "New");
            handler.PushJson = handler.PushJson.Replace("Old destination", "New destination");
            await (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
            Assert.Same(channel, Assert.Single(window.Channels));
            Assert.Same(push, Assert.Single(window.PushDestinations));
            Assert.Equal("New", channel.Name);
            Assert.Equal("New destination", push.Name);
            Assert.Contains(nameof(ChannelDisplay.Name), notifications);
        }
        finally { window.Close(); }
    });

    private sealed class Row(int id, string name)
    {
        public int Id { get; } = id;
        public string Name { get; set; } = name;
    }
}
