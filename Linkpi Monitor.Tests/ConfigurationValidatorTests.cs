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
}
