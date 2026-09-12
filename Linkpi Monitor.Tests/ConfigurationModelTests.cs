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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void OptionalChannelSectionVisibilityIsCapabilityDriven(bool hls, bool transport, bool ndi)
    {
        var configuration = new ChannelConfiguration
        {
            HasHls = hls,
            HasTransport = transport,
            HasNdi = ndi
        };

        Assert.Equal(hls ? Visibility.Visible : Visibility.Collapsed, configuration.HlsVisibility);
        Assert.Equal(transport ? Visibility.Visible : Visibility.Collapsed, configuration.TransportVisibility);
        Assert.Equal(ndi ? Visibility.Visible : Visibility.Collapsed, configuration.NdiVisibility);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void PhysicalInputFlagCombinesHdmiAndUsb(bool hdmi, bool usb, bool expected)
    {
        var input = new PhysicalInputConfiguration { IsHdmi = hdmi, IsUsbCamera = usb };

        Assert.Equal(expected, input.IsPhysicalInput);
    }

    [Fact]
    public void ConfigurationCatalogsExposeSupportedFirmwareValues()
    {
        var decode = new DecodeConfiguration();
        var encoder = new EncoderConfiguration();
        var audio = new AudioEncoderConfiguration();
        var streamPush = new PushStreamConfiguration();
        var destination = new PushDestinationConfiguration();
        var transport = new TransportStreamConfiguration();
        var audioInput = new AudioInputConfiguration();
        var videoOutput = new VideoOutputConfiguration();

        Assert.Equal(["0", "1", "2", "3"], decode.BufferModes.Select(option => option.Value));
        Assert.Equal(["tcp", "udp"], decode.Protocols.Select(option => option.Value));
        Assert.Contains(encoder.VideoFormats, option => option.Value == "h265,main");
        Assert.Contains(encoder.RateControls, option => option.Value == "fixqp");
        Assert.Contains(encoder.TimestampModes, option => option.Value == "true,sinsam");
        Assert.Contains(audio.Codecs, option => option.Value == "opus");
        Assert.Contains(audio.Gains, option => option.Value == "-24");
        Assert.Contains(audio.SampleRates, option => option.Value == "44100");
        Assert.Equal(["1", "2"], audio.ChannelModes.Select(option => option.Value));
        Assert.Contains("rtp_mpegts", streamPush.Formats);
        Assert.Contains(streamPush.CompatibilityModes, option => option.Value == "ext_header");
        Assert.Equal(["main", "sub"], destination.Streams.Select(option => option.Value));
        Assert.Contains(destination.CompatibilityModes, option => option.Value == "ext_header");
        Assert.Contains("1316", transport.PacketSizes);
        Assert.Equal(16, audioInput.NoiseReductionLevels.Count);
        Assert.Contains(audioInput.NoiseReductionModes, option => option.Value == "1");
        Assert.Contains(videoOutput.Types, option => option.Value == "dvi");
        Assert.Contains(videoOutput.ColorMatrices, option => option.Value == "709_601");
    }

    [Fact]
    public void PushEditableCopyPreservesIdentityWithoutMutatingDashboardState()
    {
        var original = new PushConfiguration
        {
            OriginalDestinationsJson = "[{\"des\":\"Primary\"}]",
            Autorun = true,
            VideoSources = [new SelectionOption("0", "HDMI")]
        };
        original.Destinations.Add(new PushDestinationConfiguration
        {
            OriginalIndex = 0,
            Name = "Primary",
            Url = "rtmp://example.test/live"
        });

        var copy = original.CreateEditableCopy();
        copy.Autorun = false;
        copy.Destinations[0].Name = "Changed";
        copy.Destinations.Add(new PushDestinationConfiguration());

        Assert.True(original.Autorun);
        Assert.Single(original.Destinations);
        Assert.Equal("Primary", original.Destinations[0].Name);
        Assert.Equal(0, copy.Destinations[0].OriginalIndex);
        Assert.Equal(original.OriginalDestinationsJson, copy.OriginalDestinationsJson);
    }

    [Fact]
    public void HardwareEditableCopyDoesNotMutateDashboardState()
    {
        var original = new HardwareConfiguration
        {
            HasVideoOutput = true,
            VideoOutputs = [new VideoOutputConfiguration { Name = "HDMI output", Luma = "50" }]
        };

        var copy = original.CreateEditableCopy();
        copy.VideoOutputs[0].Luma = "75";

        Assert.Equal("50", original.VideoOutputs[0].Luma);
        Assert.Equal("75", copy.VideoOutputs[0].Luma);
    }
}
