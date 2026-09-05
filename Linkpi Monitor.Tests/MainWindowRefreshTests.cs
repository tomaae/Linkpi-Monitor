using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class MainWindowRefreshTests
{
    [Fact]
    public Task TimeoutMarksPreviouslyOnlineDeviceUnavailable() => WpfTestHost.RunAsync(async () =>
    {
        var handler = new LinkPiTestHandler();
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var window = new MainWindow();
        WpfTestHost.Set(window, "_client", client);
        try
        {
            await (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
            Assert.Equal("Online", window.ConnectionStatus);
            handler.Override = _ => throw new TaskCanceledException("Request timed out", new TimeoutException());
            await (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
            Assert.Equal("Unavailable", window.ConnectionStatus);
            Assert.Equal("—", window.CpuDisplay);
            Assert.NotEmpty(window.ErrorMessage);
        }
        finally { window.Close(); }
    });
}
