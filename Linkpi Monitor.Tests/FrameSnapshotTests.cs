using System.IO;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class FrameSnapshotTests
{
    [Fact]
    public Task ClipboardPayloadContainsExactPngAndCompatibleBitmap() => WpfTestHost.RunAsync(async () =>
    {
        var frame = await FrameSnapshot.CaptureAsync(path =>
        {
            File.WriteAllBytes(path, TestDevices.OnePixelPng);
            return true;
        }, CancellationToken.None);
        var data = frame.CreateClipboardData();
        Assert.Contains("PNG", data.GetFormats(autoConvert: false));
        using var png = Assert.IsType<MemoryStream>(data.GetData("PNG", autoConvert: false));
        Assert.Equal(TestDevices.OnePixelPng, png.ToArray());
        Assert.Same(frame.Image, data.GetImage());
    });

    [Fact]
    public async Task CaptureVerifiesNativePngAndCleansTemporaryFile()
    {
        string? temporaryPath = null;
        var frame = await FrameSnapshot.CaptureAsync(path =>
        {
            temporaryPath = path;
            File.WriteAllBytes(path, TestDevices.OnePixelPng);
            return true;
        }, CancellationToken.None);
        Assert.Equal(TestDevices.OnePixelPng, frame.PngBytes);
        Assert.Equal(1, frame.Image.PixelWidth);
        Assert.Equal(1, frame.Image.PixelHeight);
        Assert.True(frame.Image.IsFrozen);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task NativeSuccessWithoutFileIsReportedAsFailure()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => FrameSnapshot.CaptureAsync(_ => true, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrInvalidCaptureCleansPartialFile(bool nativeSuccess)
    {
        string? temporaryPath = null;
        await Assert.ThrowsAnyAsync<Exception>(() => FrameSnapshot.CaptureAsync(path =>
        {
            temporaryPath = path;
            File.WriteAllText(path, "not a PNG");
            return nativeSuccess;
        }, CancellationToken.None));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public Task SlowCaptureDoesNotBlockDispatcherAndCancellationDrainsNativeWork() => WpfTestHost.RunAsync(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? temporaryPath = null;
        var capture = FrameSnapshot.CaptureAsync(path =>
        {
            temporaryPath = path;
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            File.WriteAllBytes(path, TestDevices.OnePixelPng);
            return true;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.False(capture.IsCompleted);
            cancellation.Cancel();
            Assert.False(capture.IsCompleted);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.False(File.Exists(temporaryPath));
    });

    [Fact]
    public async Task SavePublishesExactPngAndCancelledSavePreservesExistingFile()
    {
        var frame = await FrameSnapshot.CaptureAsync(path =>
        {
            File.WriteAllBytes(path, TestDevices.OnePixelPng);
            return true;
        }, CancellationToken.None);
        var directory = Directory.CreateTempSubdirectory("LinkPiSnapshotTest-");
        try
        {
            var path = Path.Combine(directory.FullName, "frame.png");
            await File.WriteAllTextAsync(path, "existing");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frame.SaveAsync(path, cancellation.Token));
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
            await frame.SaveAsync(path, CancellationToken.None);
            Assert.Equal(TestDevices.OnePixelPng, await File.ReadAllBytesAsync(path));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(recursive: true); }
    }
}
