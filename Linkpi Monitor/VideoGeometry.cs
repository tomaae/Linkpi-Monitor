using System.Windows.Media.Imaging;

namespace Linkpi_Monitor;

internal readonly record struct VideoGeometry(
    double AspectRatio,
    bool IsKnown,
    string? VlcAspectRatio)
{
    private const double FallbackAspectRatio = 16d / 9d;

    public static VideoGeometry FromChannel(ChannelDisplay channel)
    {
        var width = channel.SourceWidth;
        var height = channel.SourceHeight;
        if ((width <= 0 || height <= 0) &&
            TryParseVideoSize(channel.Configuration.MainEncoder.VideoSize, out var encodedWidth, out var encodedHeight))
        {
            width = encodedWidth;
            height = encodedHeight;
        }

        if ((width <= 0 || height <= 0) &&
            channel.PreviewImage is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } preview)
        {
            width = preview.PixelWidth;
            height = preview.PixelHeight;
        }

        if (width <= 0 || height <= 0)
        {
            return new VideoGeometry(FallbackAspectRatio, false, null);
        }

        var decode = channel.Configuration.Decode;
        var croppedWidth = width - ParseCrop(decode.CropLeft) - ParseCrop(decode.CropRight);
        var croppedHeight = height - ParseCrop(decode.CropTop) - ParseCrop(decode.CropBottom);
        width = croppedWidth > 0 ? croppedWidth : width;
        height = croppedHeight > 0 ? croppedHeight : height;

        if (ParseRotation(decode.Rotate) is 90 or 270)
        {
            (width, height) = (height, width);
        }

        var divisor = GreatestCommonDivisor(width, height);
        return new VideoGeometry((double)width / height, true, $"{width / divisor}:{height / divisor}");
    }

    public (double Width, double Height) FitWithin(double maximumWidth, double maximumHeight)
    {
        var width = maximumHeight * AspectRatio;
        var height = maximumHeight;
        if (width > maximumWidth)
        {
            width = maximumWidth;
            height = width / AspectRatio;
        }

        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static bool TryParseVideoSize(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        var separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return separator > 0 &&
               int.TryParse(value[..separator], out width) &&
               int.TryParse(value[(separator + 1)..], out height) &&
               width > 0 && height > 0;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Max(1, left);
    }

    private static int ParseCrop(string value) =>
        int.TryParse(value, out var parsed) ? Math.Max(0, parsed) : 0;

    private static int ParseRotation(string value) =>
        int.TryParse(value, out var parsed) ? ((parsed % 360) + 360) % 360 : 0;
}
