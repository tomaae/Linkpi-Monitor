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

        var settings = await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(5, settings.RefreshIntervalSeconds);
        Assert.Empty(settings.Devices);
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
            """, TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken);

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
            """, TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken);

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
            """, TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken);

        var device = Assert.Single(settings.Devices);
        Assert.Equal(8, settings.RefreshIntervalSeconds);
        Assert.Equal("Legacy", device.Name);
        Assert.Equal("http://legacy.test", device.BaseUrl);
    }

    [Fact]
    public async Task MissingDeviceCollectionIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("Either Devices", exception.Message);
    }

    [Fact]
    public async Task EmptyDeviceCollectionIsValid()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{\"Devices\":[]}", TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Empty(settings.Devices);
        Assert.Equal(5, settings.RefreshIntervalSeconds);
    }

    [Theory]
    [InlineData("ftp://encoder.test", "absolute HTTP or HTTPS")]
    [InlineData("encoder.test", "absolute HTTP or HTTPS")]
    [InlineData("", "cannot be empty")]
    public async Task RejectsInvalidOrUnsupportedBaseUrl(string baseUrl, string expectedMessage)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Devices = new[] { new { Name = "Bad", BaseUrl = baseUrl } }
        }), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task RejectsNonObjectRoot(string json)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("root value must be a JSON object", exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"device\"")]
    [InlineData("42")]
    public async Task RejectsNonArrayDevices(string devicesJson)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""{"Devices":{{devicesJson}}}""", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("Devices must be a JSON array", exception.Message);
    }

    [Theory]
    [InlineData("\"5\"")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task RejectsNonIntegerRefreshInterval(string intervalJson)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""{"RefreshIntervalSeconds":{{intervalJson}},"Devices":[]}""", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("RefreshIntervalSeconds must be a whole number", exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"device\"")]
    [InlineData("1")]
    public async Task RejectsNonObjectDeviceEntry(string deviceJson)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""{"Devices":[{{deviceJson}}]}""", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("Devices[0] must be a JSON object", exception.Message);
    }

    [Theory]
    [InlineData("{}", "is required")]
    [InlineData("{\"BaseUrl\":null}", "must be a string")]
    [InlineData("{\"BaseUrl\":42}", "must be a string")]
    [InlineData("{\"BaseUrl\":\"   \"}", "cannot be empty")]
    public async Task RejectsMissingOrInvalidBaseUrlProperty(string deviceJson, string expectedMessage)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""{"Devices":[{{deviceJson}}]}""", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("Devices[0].BaseUrl", exception.Message);
        Assert.Contains(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("Name", "42")]
    [InlineData("Username", "false")]
    [InlineData("Password", "{}")]
    public async Task RejectsNonStringOptionalDeviceProperty(string propertyName, string propertyJson)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""{"Devices":[{"BaseUrl":"http://encoder.test","{{propertyName}}":{{propertyJson}}}]}""", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains($"Devices[0].{propertyName} must be a string", exception.Message);
    }

    [Theory]
    [InlineData("http://user:password@encoder.test")]
    [InlineData("http://encoder.test/api")]
    [InlineData("http://encoder.test?mode=1")]
    [InlineData("http://encoder.test/#settings")]
    public async Task RejectsBaseUrlComponentsOutsideDeviceOrigin(string baseUrl)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Devices = new[] { new { BaseUrl = baseUrl } }
        }), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("only a scheme, host, and optional port", exception.Message);
    }

    [Fact]
    public async Task NormalizesWhitespaceNamesCredentialsAndDefaultPort()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, """
            {"Devices":[{"Name":"  Studio  ","BaseUrl":"  HTTP://Encoder.Test:80///  ","Username":"  admin  ","Password":" password "}]}
            """, TestContext.Current.CancellationToken);

        var device = Assert.Single((await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken)).Devices);

        Assert.Equal("Studio", device.Name);
        Assert.Equal("http://encoder.test", device.BaseUrl);
        Assert.Equal("admin", device.Username);
        Assert.Equal(" password ", device.Password);
    }

    [Fact]
    public async Task RejectsDuplicateNormalizedDeviceUrls()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, """
            {"Devices":[{"BaseUrl":"http://ENCODER.test"},{"BaseUrl":"http://encoder.test:80/"}]}
            """, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("configured more than once", exception.Message);
        Assert.Contains("http://encoder.test", exception.Message);
    }

    [Fact]
    public async Task RejectsNonObjectLegacyDevice()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{\"LinkPi\":null}", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("LinkPi must be a JSON object", exception.Message);
    }

    [Fact]
    public async Task InvalidConfigurationIsNotReplacedOrRewritten()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        const string invalidJson = "{\"Devices\":{\"BaseUrl\":\"http://encoder.test\"}}";
        await File.WriteAllTextAsync(path, invalidJson, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(invalidJson, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{not-json", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<JsonException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));
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

        await settings.SaveAsync(path, TestContext.Current.CancellationToken);
        var savedText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var reloaded = await AppSettings.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains(Environment.NewLine, savedText);
        Assert.DoesNotContain("AllowChanges", savedText, StringComparison.Ordinal);
        Assert.DoesNotContain("CanSaveChanges", savedText, StringComparison.Ordinal);
        Assert.DoesNotContain("local-secret", savedText, StringComparison.Ordinal);
        Assert.Contains(CredentialProtector.Prefix, savedText, StringComparison.Ordinal);
        Assert.Equal(17, reloaded.RefreshIntervalSeconds);
        Assert.Equal(settings.Devices, reloaded.Devices);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task PlainTextPasswordIsProtectedOnNextSave()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.File("plain.json");
        var savedPath = temporary.File("protected.json");
        await File.WriteAllTextAsync(sourcePath, """
            {"Devices":[{"BaseUrl":"http://encoder.test","Password":"legacy-secret"}]}
            """, TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadAsync(sourcePath, TestContext.Current.CancellationToken);
        await settings.SaveAsync(savedPath, TestContext.Current.CancellationToken);
        var savedText = await File.ReadAllTextAsync(savedPath, TestContext.Current.CancellationToken);

        Assert.Equal("legacy-secret", Assert.Single(settings.Devices).Password);
        Assert.DoesNotContain("legacy-secret", savedText, StringComparison.Ordinal);
        Assert.Equal("legacy-secret", Assert.Single((await AppSettings.LoadAsync(savedPath, TestContext.Current.CancellationToken)).Devices).Password);
    }

    [Fact]
    public async Task InvalidProtectedPasswordReportsUsefulError()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, $$"""
            {"Devices":[{"BaseUrl":"http://encoder.test","Password":"{{CredentialProtector.Prefix}}not-base64"}]}
            """, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppSettings.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("could not be decrypted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocationRoutingCreatesNewConfigurationWhenNeitherLocationExists()
    {
        using var temporary = new TemporaryDirectory();
        var currentPath = temporary.File("current/config.json");
        var legacyPath = temporary.File("legacy/config.json");

        var settings = await AppSettings.LoadFromLocationsAsync(
            currentPath,
            legacyPath,
            TestContext.Current.CancellationToken);

        Assert.Empty(settings.Devices);
        Assert.Equal(5, settings.RefreshIntervalSeconds);
        Assert.True(File.Exists(currentPath));
        Assert.False(File.Exists(legacyPath));
        Assert.Empty(settings.ConfigurationNotice);
    }

    [Fact]
    public async Task LocationRoutingMigratesLegacyConfigurationWithoutChangingOriginal()
    {
        using var temporary = new TemporaryDirectory();
        var currentPath = temporary.File("current/config.json");
        var legacyPath = temporary.File("legacy/config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        const string legacyJson =
            "{\"RefreshIntervalSeconds\":9,\"Devices\":[{\"Name\":\"Legacy\",\"BaseUrl\":\"http://legacy.test\",\"Password\":\"secret\"}]}";
        await File.WriteAllTextAsync(legacyPath, legacyJson, TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadFromLocationsAsync(
            currentPath,
            legacyPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(9, settings.RefreshIntervalSeconds);
        Assert.Equal("Legacy", Assert.Single(settings.Devices).Name);
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(legacyPath, TestContext.Current.CancellationToken));
        Assert.Contains(currentPath, settings.ConfigurationNotice, StringComparison.Ordinal);
        Assert.True(File.Exists(currentPath));
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(currentPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocationRoutingPrefersExistingCurrentConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var currentPath = temporary.File("current.json");
        var legacyPath = temporary.File("legacy.json");
        await File.WriteAllTextAsync(currentPath, "{\"RefreshIntervalSeconds\":7,\"Devices\":[]}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(legacyPath, "{\"RefreshIntervalSeconds\":11,\"Devices\":[]}", TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadFromLocationsAsync(
            currentPath,
            legacyPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, settings.RefreshIntervalSeconds);
        Assert.Empty(settings.ConfigurationNotice);
    }

    [Fact]
    public async Task LocationRoutingDoesNotTreatSamePathAsMigration()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("config.json");
        await File.WriteAllTextAsync(path, "{\"Devices\":[]}", TestContext.Current.CancellationToken);

        var settings = await AppSettings.LoadFromLocationsAsync(
            path,
            path.ToUpperInvariant(),
            TestContext.Current.CancellationToken);

        Assert.Empty(settings.Devices);
        Assert.Empty(settings.ConfigurationNotice);
    }

    [Fact]
    public void DefaultPathsAndEmptyCredentialsAreWellDefined()
    {
        Assert.EndsWith(Path.Combine("Linkpi Monitor", "config.json"), AppSettings.ConfigPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config.json"), AppSettings.LegacyConfigPath);
        Assert.Equal(string.Empty, CredentialProtector.Protect(string.Empty));
        Assert.Equal(string.Empty, CredentialProtector.Unprotect(string.Empty));
        Assert.Equal("plain", CredentialProtector.Unprotect("plain"));
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

    [Fact]
    public async Task FailedAtomicReplaceRemovesTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var occupiedPath = temporary.File("occupied");
        Directory.CreateDirectory(occupiedPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new AppSettings().SaveAsync(occupiedPath, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
        Assert.True(Directory.Exists(occupiedPath));
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
