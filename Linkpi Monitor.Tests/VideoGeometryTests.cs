using System.Windows.Media;
using System.Windows.Media.Imaging;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class VideoGeometryTests
{
    [Fact]
    public void UsesSourceDimensionsAndReducesVlcRatio()
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(width: 1920, height: 1080));

        Assert.True(geometry.IsKnown);
        Assert.Equal(16d / 9d, geometry.AspectRatio, 6);
        Assert.Equal("16:9", geometry.VlcAspectRatio);
    }

    [Fact]
    public void AppliesCropBeforeCalculatingRatio()
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(
            width: 1920,
            height: 1080,
            left: "240",
            right: "240"));

        Assert.Equal(4d / 3d, geometry.AspectRatio, 6);
        Assert.Equal("4:3", geometry.VlcAspectRatio);
    }

    [Theory]
    [InlineData("90")]
    [InlineData("270")]
    [InlineData("-90")]
    [InlineData("450")]
    public void QuarterTurnRotationSwapsDimensions(string rotation)
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(
            width: 1920,
            height: 1080,
            rotate: rotation));

        Assert.Equal(9d / 16d, geometry.AspectRatio, 6);
        Assert.Equal("9:16", geometry.VlcAspectRatio);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("180")]
    [InlineData("360")]
    [InlineData("invalid")]
    public void NonQuarterTurnRotationKeepsDimensions(string rotation)
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(
            width: 1920,
            height: 1080,
            rotate: rotation));

        Assert.Equal(16d / 9d, geometry.AspectRatio, 6);
    }

    [Fact]
    public void FallsBackToEncoderDimensions()
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(encodedSize: "1024x768"));

        Assert.True(geometry.IsKnown);
        Assert.Equal(4d / 3d, geometry.AspectRatio, 6);
        Assert.Equal("4:3", geometry.VlcAspectRatio);
    }

    [Fact]
    public void FallsBackToPreviewDimensions()
    {
        var preview = new WriteableBitmap(8, 5, 96, 96, PixelFormats.Bgra32, null);
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(
            encodedSize: "invalid",
            preview: preview));

        Assert.True(geometry.IsKnown);
        Assert.Equal(8d / 5d, geometry.AspectRatio, 6);
        Assert.Equal("8:5", geometry.VlcAspectRatio);
    }

    [Theory]
    [InlineData(0, 0, "-1x-1")]
    [InlineData(-1, 1080, "bad")]
    [InlineData(1920, 0, "0x720")]
    public void UnknownDimensionsUseSixteenByNineFallback(int width, int height, string encodedSize)
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(width, height, encodedSize));

        Assert.False(geometry.IsKnown);
        Assert.Null(geometry.VlcAspectRatio);
        Assert.Equal(16d / 9d, geometry.AspectRatio, 6);
    }

    [Fact]
    public void InvalidAndNegativeCropValuesAreIgnored()
    {
        var geometry = VideoGeometry.FromChannel(TestDevices.Channel(
            width: 640,
            height: 480,
            left: "-20",
            top: "bad",
            right: "800",
            bottom: "600"));

        Assert.Equal(4d / 3d, geometry.AspectRatio, 6);
        Assert.Equal("4:3", geometry.VlcAspectRatio);
    }

    [Theory]
    [InlineData(16d / 9d, 365, 170, 302.222222, 170)]
    [InlineData(4d / 3d, 365, 170, 226.666667, 170)]
    [InlineData(9d / 16d, 365, 170, 95.625, 170)]
    [InlineData(4d, 365, 170, 365, 91.25)]
    public void FitWithinPreservesRatio(
        double ratio,
        double maximumWidth,
        double maximumHeight,
        double expectedWidth,
        double expectedHeight)
    {
        var geometry = new VideoGeometry(ratio, true, null);

        var fitted = geometry.FitWithin(maximumWidth, maximumHeight);

        Assert.Equal(expectedWidth, fitted.Width, 5);
        Assert.Equal(expectedHeight, fitted.Height, 5);
        Assert.InRange(fitted.Width, 1, maximumWidth);
        Assert.InRange(fitted.Height, 1, maximumHeight);
    }

    [Fact]
    public void ChannelPreviewDimensionsUseEffectiveGeometry()
    {
        var channel = TestDevices.Channel(width: 1920, height: 1080, rotate: "90");

        Assert.Equal(95.625, channel.PreviewRenderWidth, 5);
        Assert.Equal(170, channel.PreviewRenderHeight, 5);
    }
}
