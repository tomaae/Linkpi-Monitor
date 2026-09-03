using System.ComponentModel;
using System.Windows;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class ConfigurationModelTests
{
    [Fact]
    public void EncoderEnabledRaisesOneChangeNotification()
    {
        var configuration = new EncoderConfiguration();
        var changes = new List<string?>();
        configuration.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        configuration.Enabled = true;
        configuration.Enabled = true;

        Assert.Equal([nameof(EncoderConfiguration.Enabled)], changes);
    }

    [Fact]
    public void PushAutorunRaisesOneChangeNotification()
    {
        var configuration = new PushConfiguration();
        var changes = new List<string?>();
        configuration.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        configuration.Autorun = true;
        configuration.Autorun = true;

        Assert.Equal([nameof(PushConfiguration.Autorun)], changes);
    }

    [Theory]
    [InlineData(false, false, false, Visibility.Collapsed)]
    [InlineData(true, false, false, Visibility.Visible)]
    [InlineData(false, true, false, Visibility.Visible)]
    [InlineData(false, false, true, Visibility.Visible)]
    public void HardwareVisibilityTracksAvailableControls(
        bool line,
        bool usb,
        bool video,
        Visibility expected)
    {
        var hardware = new HardwareConfiguration
        {
            HasLineAudio = line,
            HasUsbAudioInput = usb,
            HasVideoOutput = video
        };

        Assert.Equal(line ? Visibility.Visible : Visibility.Collapsed, hardware.LineAudioVisibility);
        Assert.Equal(usb ? Visibility.Visible : Visibility.Collapsed, hardware.UsbAudioVisibility);
        Assert.Equal(video ? Visibility.Visible : Visibility.Collapsed, hardware.VideoOutputVisibility);
        Assert.Equal(expected == Visibility.Visible || usb || video, hardware.HasHardwareControls);
    }

    [Theory]
    [InlineData(false, Visibility.Collapsed)]
    [InlineData(true, Visibility.Visible)]
    public void OptionalAudioEnableVisibilityIsCapabilityDriven(bool canDisable, Visibility expected)
    {
        var input = new AudioInputConfiguration { CanDisable = canDisable };

        Assert.Equal(expected, input.EnabledVisibility);
    }
}
