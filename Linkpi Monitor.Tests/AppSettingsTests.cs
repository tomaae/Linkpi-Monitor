using System.Text.Json;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public async Task MissingFileCreatesUsableDefaultConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");

        var settings = await AppSettings.LoadAsync(path);

        var device = Assert.Single(settings.Devices);
        Assert.Equal(5, settings.RefreshIntervalSeconds);
        Assert.Equal("LinkPi", device.Name);
        Assert.Equal("http://192.168.1.100", device.BaseUrl);
        Assert.Equal("admin", device.Username);
        Assert.Empty(device.Password);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(-10, 2)]
    [InlineData(1, 2)]
    [InlineData(30, 30)]
    [InlineData(301, 300)]
    public async Task RefreshIntervalIsClamped(int configured, int expected)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""
            {"RefreshIntervalSeconds":{{configured}},"Devices":[]}
            """);

        var settings = await AppSettings.LoadAsync(path);

        Assert.Equal(expected, settings.RefreshIntervalSeconds);
        Assert.Empty(settings.Devices);
    }

    [Fact]
    public async Task LoadsMultipleDevicesAndNormalizesNamesAndTrailingSlashes()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, """
            {
              "Devices": [
                {"Name":"Studio","BaseUrl":"http://10.0.1.6///","Username":"one","Password":"secret-one"},
                {"Name":"  ","BaseUrl":"https://encoder.example:8443/","Username":"two","Password":"secret-two"}
              ]
            }
            """);

        var settings = await AppSettings.LoadAsync(path);

        Assert.Collection(settings.Devices,
            first =>
            {
                Assert.Equal("Studio", first.Name);
                Assert.Equal("http://10.0.1.6", first.BaseUrl);
                Assert.Equal("one", first.Username);
                Assert.Equal("secret-one", first.Password);
            },
            second =>
            {
                Assert.Equal("encoder.example", second.Name);
                Assert.Equal("https://encoder.example:8443", second.BaseUrl);
            });
    }

    [Fact]
    public async Task LoadsLegacySingleDeviceShape()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, """
            {"RefreshIntervalSeconds":8,"LinkPi":{"Name":"Legacy","BaseUrl":"http://legacy.test","Username":"admin","Password":"pw"}}
            """);

        var settings = await AppSettings.LoadAsync(path);

        var device = Assert.Single(settings.Devices);
        Assert.Equal(8, settings.RefreshIntervalSeconds);
        Assert.Equal("Legacy", device.Name);
        Assert.Equal("http://legacy.test", device.BaseUrl);
    }

    [Fact]
    public async Task MissingDeviceCollectionProducesEmptyList()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{}");

        var settings = await AppSettings.LoadAsync(path);

        Assert.Empty(settings.Devices);
        Assert.Equal(5, settings.RefreshIntervalSeconds);
    }

    [Theory]
    [InlineData("ftp://encoder.test")]
    [InlineData("encoder.test")]
    [InlineData("")]
    public async Task RejectsInvalidOrUnsupportedBaseUrl(string baseUrl)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Devices = new[] { new { Name = "Bad", BaseUrl = baseUrl } }
        }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path));

        Assert.Contains("valid HTTP or HTTPS", exception.Message);
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{not-json");

        await Assert.ThrowsAnyAsync<JsonException>(() => AppSettings.LoadAsync(path));
    }

    [Fact]
    public async Task SaveAndReloadRoundTripsDevicesWithoutWriteFlags()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 17,
            Devices =
            [
                new DeviceSettings
                {
                    Name = "Encoder",
                    BaseUrl = "https://encoder.test:9443",
                    Username = "operator",
                    Password = "local-secret"
                }
            ]
        };

        await settings.SaveAsync(path);
        var savedText = await File.ReadAllTextAsync(path);
        var reloaded = await AppSettings.LoadAsync(path);

        Assert.Contains(Environment.NewLine, savedText);
        Assert.DoesNotContain("AllowChanges", savedText, StringComparison.Ordinal);
        Assert.DoesNotContain("CanSaveChanges", savedText, StringComparison.Ordinal);
        Assert.Equal(17, reloaded.RefreshIntervalSeconds);
        Assert.Equal(settings.Devices, reloaded.Devices);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task PreCancelledSaveDoesNotLeaveTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AppSettings().SaveAsync(path, cancellation.Token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LinkPiMonitorTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
