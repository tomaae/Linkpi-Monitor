using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class PreviewActivityTests
{
    [Fact]
    public async Task HiddenPreviewsSkipImageWorkButKeepMetricsAndResumeWhenRequested()
    {
        var handler = new LinkPiTestHandler { ConfigJson = """[{"id":0,"type":"vi","enable":true}]""" };
        handler.RpcResults["enc.getSysState"] = "{\"cpu\":42}";
        handler.Previews[0] = new PreviewResponse(TestDevices.OnePixelPng);
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var hidden = await client.GetSnapshotAsync(CancellationToken.None, includePreviews: false);
        Assert.Equal(42, hidden.CpuPercent);
        Assert.DoesNotContain(handler.Requests, request => request.RpcMethod == "enc.snap" || request.Path.StartsWith("/snap/"));
        var visible = await client.GetSnapshotAsync(CancellationToken.None, includePreviews: true);
        Assert.True(Assert.Single(visible.Channels).HasPreview);
        Assert.Contains(handler.Requests, request => request.RpcMethod == "enc.snap");
    }

    [Fact]
    public Task HiddenWindowDoesNotGeneratePreviews() => WpfTestHost.RunAsync(async () =>
    {
        var handler = new LinkPiTestHandler { ConfigJson = """[{"id":0,"type":"vi","enable":true}]""" };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var window = new MainWindow();
        WpfTestHost.Set(window, "_client", client);
        try
        {
            await (Task)WpfTestHost.Invoke(window, "RefreshAsync")!;
            Assert.Equal("Online", window.ConnectionStatus);
            Assert.DoesNotContain(handler.Requests, request => request.RpcMethod == "enc.snap");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SkippedPreviewRetainsImageUntilNewPreviewOrDisabledSource()
    {
        var incoming = TestDevices.Channel();
        incoming.CanPreview = true;
        var current = TestDevices.Channel();
        current.PreviewImage = System.Windows.Media.Imaging.BitmapFrame.Create(
            new System.IO.MemoryStream(TestDevices.OnePixelPng));
        var preview = current.PreviewImage;
        current.UpdateFrom(incoming, updatePreview: false);
        Assert.Same(preview, current.PreviewImage);
        current.UpdateFrom(incoming, updatePreview: true);
        Assert.Null(current.PreviewImage);
        current.PreviewImage = preview;
        incoming.CanPreview = false;
        current.UpdateFrom(incoming, updatePreview: false);
        Assert.Null(current.PreviewImage);
    }
}
