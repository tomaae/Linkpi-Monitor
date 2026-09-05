using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Linkpi_Monitor;

internal sealed class FrameSnapshot
{
    public byte[] PngBytes { get; }
    public BitmapSource Image { get; }

    public DataObject CreateClipboardData()
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(PngBytes, writable: false), autoConvert: false);
        data.SetImage(Image);
        return data;
    }

    private FrameSnapshot(byte[] pngBytes)
    {
        PngBytes = pngBytes;
        using var stream = new MemoryStream(pngBytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Image = decoder.Frames[0];
        Image.Freeze();
    }

    public static Task<FrameSnapshot> CaptureAsync(Func<string, bool> capture, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"LinkPiMonitor-{Guid.NewGuid():N}.png");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // LibVLC 3 captures synchronously, but its return value does not report disk failures.
                if (!capture(path)) throw new InvalidOperationException("No video frame is currently available.");
                cancellationToken.ThrowIfCancellationRequested();
                // A unique file and PNG decoding verify that this request actually produced an image.
                return new FrameSnapshot(File.ReadAllBytes(path));
            }
            finally { DeleteTemporaryFile(path); }
        }, cancellationToken);

    public async Task SaveAsync(string filePath, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, PngBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally { DeleteTemporaryFile(temporaryPath); }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
