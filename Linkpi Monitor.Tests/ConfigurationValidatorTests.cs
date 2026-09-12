using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void ChannelValidationRejectsMalformedAndOutOfRangeDeviceValues()
    {
        var configuration = ValidChannel();
        configuration.General.Name = " ";
        configuration.MainEncoder.Bitrate = "fast";
        configuration.MainEncoder.MinimumQp = "40";
        configuration.MainEncoder.MaximumQp = "20";
        configuration.MainStream.Udp.Enabled = true;
        configuration.MainStream.Udp.IpAddress = string.Empty;
        configuration.MainStream.Udp.Port = "70000";

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("Channel name", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("bitrate", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("minimum QP", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("UDP address", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("UDP port", StringComparison.Ordinal));
    }

    [Fact]
    public void PushValidationRequiresUniqueNamesAndValidEnabledDestinationUrl()
    {
        var configuration = new PushConfiguration();
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            Name = "Primary",
            Enabled = true,
            VideoSource = "0",
            Url = "not a URL"
        });
        configuration.Destinations.Add(new PushDestinationConfiguration { Name = "primary" });

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("absolute URL", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unique", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidConfigurationsHaveNoErrors()
    {
        Assert.Empty(ConfigurationValidator.Validate(ValidChannel()));
        Assert.Empty(ConfigurationValidator.Validate(new PushConfiguration()));
        Assert.Empty(ConfigurationValidator.Validate(new HardwareConfiguration()));
    }

    [Fact]
    public void ChannelValidationCoversConditionalInputsOutputsAndNumericLimits()
    {
        var configuration = new ChannelConfiguration
        {
            General = new GeneralChannelConfiguration { Name = new string('N', 129) },
            Input = new PhysicalInputConfiguration
            {
                IsHdmi = true,
                IsUsbCamera = true,
                Contrast = "64",
                CropLeft = "-1",
                CropTop = "bad",
                CropRight = "16385",
                CropBottom = " ",
                CaptureSize = "640",
                Framerate = "0"
            },
            Decode = new DecodeConfiguration
            {
                IsNetworkSource = true,
                SourceUrl = string.Empty,
                InputFramerate = "241",
                MinimumDelay = "120001",
                Contrast = "bad",
                CropLeft = "-1",
                CropTop = "bad",
                CropRight = "16385",
                CropBottom = " "
            },
            MainEncoder = InvalidEncoder(),
            SubEncoder = InvalidEncoder(),
            Audio = new AudioEncoderConfiguration { Bitrate = "7" },
            MainStream = InvalidStream(),
            SubStream = InvalidStream(),
            Hls = new HlsConfiguration { SegmentLength = "0", ListLength = "100001" },
            Transport = new TransportStreamConfiguration
            {
                PacketSize = "187",
                Pid = "8192",
                PmtPid = "-1",
                ServiceId = "65536",
                StreamId = "bad",
                NetworkId = "65536"
            }
        };

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("Channel name cannot exceed", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("USB capture size", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Network source URL is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SRT port", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("UDP TTL", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("RIST address", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Push URL", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("MPEG-TS network ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("leftx1080")]
    [InlineData("1920xhigh")]
    [InlineData("0x1080")]
    [InlineData("16385x1080")]
    [InlineData("1920x0")]
    [InlineData("1920x16385")]
    public void ChannelValidationRejectsEveryMalformedVideoSizeShape(string videoSize)
    {
        var configuration = ValidChannel();
        configuration.MainEncoder.VideoSize = videoSize;

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("Main encoder video size", StringComparison.Ordinal));
    }

    [Fact]
    public void PushValidationCoversRequiredOptionalAndMaximumLengthRules()
    {
        var configuration = new PushConfiguration();
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            Name = new string('D', 129),
            Enabled = true,
            VideoSource = string.Empty,
            Url = string.Empty
        });
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            Name = "Disabled",
            Enabled = false,
            Url = "relative/path"
        });
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            Name = "Valid",
            Enabled = false,
            Url = "https://example.test/live"
        });
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            Name = " ",
            Enabled = false,
            Url = string.Empty
        });

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("cannot exceed 128", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("video source is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("URL is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("absolute URL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bad", "36")]
    [InlineData("22", "bad")]
    public void ChannelValidationRejectsEitherMalformedQpBoundary(string minimumQp, string maximumQp)
    {
        var configuration = ValidChannel();
        configuration.MainEncoder.MinimumQp = minimumQp;
        configuration.MainEncoder.MaximumQp = maximumQp;

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("QP", StringComparison.Ordinal));
    }

    [Fact]
    public void HardwareValidationCoversAudioAndEveryVideoAdjustment()
    {
        var configuration = new HardwareConfiguration
        {
            HasUsbAudioInput = true,
            HasLineAudio = true,
            UsbAudioInput = new AudioInputConfiguration { NoiseReductionLevel = "0" },
            LineAudioInput = new AudioInputConfiguration { NoiseReductionLevel = "17" },
            VideoOutputs =
            [
                new VideoOutputConfiguration
                {
                    Name = "HDMI output",
                    Luma = "-1",
                    Contrast = "101",
                    Saturation = "bad",
                    Hue = " "
                }
            ]
        };

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Equal(6, errors.Count);
        Assert.Contains(errors, error => error.Contains("USB noise-reduction", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Line-input noise-reduction", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("HDMI output hue", StringComparison.Ordinal));
        Assert.StartsWith("Please correct", ConfigurationValidator.Format(errors), StringComparison.Ordinal);
    }

    private static ChannelConfiguration ValidChannel() => new()
    {
        General = new GeneralChannelConfiguration { Name = "Channel" },
        MainEncoder = new EncoderConfiguration
        {
            Enabled = true,
            VideoSize = "1920x1080",
            Bitrate = "6000",
            Framerate = "30",
            Gop = "2"
        },
        SubEncoder = new EncoderConfiguration
        {
            VideoSize = "640x360",
            Framerate = "30",
            Gop = "2"
        }
    };

    private static EncoderConfiguration InvalidEncoder() => new()
    {
        Enabled = true,
        VideoSize = "invalid",
        Bitrate = "100001",
        Framerate = "241",
        Gop = "0",
        MinimumQp = "52",
        MaximumQp = "-1",
        FixedIQp = "bad",
        FixedPQp = "52"
    };

    private static StreamOutputConfiguration InvalidStream() => new()
    {
        Srt = new SrtConfiguration { Enabled = true, Port = "0", Latency = "120001" },
        Udp = new UdpConfiguration
        {
            Enabled = true,
            IpAddress = string.Empty,
            Port = "65536",
            Ttl = "0",
            Bandwidth = "101"
        },
        Rist = new RistConfiguration { Enabled = true, IpAddress = string.Empty, Port = "0" },
        Push = new PushStreamConfiguration { Enabled = true, Url = string.Empty, HevcId = "256" }
    };
}
