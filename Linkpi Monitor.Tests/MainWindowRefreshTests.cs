using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class MainWindowRefreshTests
{
    [Fact]
    public Task ClosingWaitsForActiveRefreshAndIgnoresLateResults() => WpfTestHost.RunAsync(async () =>
    {
        var handler = new DelayedHandler();
        var client = new LinkPiClient(TestDevices.Default, handler);
        var window = new MainWindow();
        WpfTestHost.Set(window, "_client", client);
        var refresh = (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
        await handler.Started.Task;
        window.Close();
        await refresh;
        // Drain queued shutdown work and prove no disposed gate or late Online update occurs.
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.NotEqual("Online", window.ConnectionStatus);
        await (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
    });

    private sealed class DelayedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

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
