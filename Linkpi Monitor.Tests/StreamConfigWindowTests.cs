using System.Windows.Threading;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class StreamConfigWindowTests
{
    [Fact]
    public Task ChannelWithStatusBrushOpensAndClosesWithoutEdits() => WpfTestHost.RunAsync(() =>
    {
        var channel = TestDevices.Channel();
        using var client = new LinkPiClient(TestDevices.Default, new LinkPiTestHandler());
        var window = new StreamConfigWindow(channel, client);
        var editableChannel = Assert.IsType<ChannelDisplay>(window.DataContext);
        Assert.NotSame(channel.Configuration, editableChannel.Configuration);

        window.Dispatcher.InvokeAsync(window.Close, DispatcherPriority.ApplicationIdle);

        Assert.False(window.ShowDialog());
        return Task.CompletedTask;
    });
}
