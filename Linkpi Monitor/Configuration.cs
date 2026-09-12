using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linkpi_Monitor;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public List<DeviceSettings> Devices { get; init; } = [];
    public int RefreshIntervalSeconds { get; init; } = 5;

    [JsonIgnore]
    public string ConfigurationNotice { get; internal set; } = string.Empty;

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Linkpi Monitor",
        "config.json");

    public static string LegacyConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadFromLocationsAsync(ConfigPath, LegacyConfigPath, cancellationToken);

    internal static async Task<AppSettings> LoadFromLocationsAsync(
        string configPath,
        string legacyConfigPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath) &&
            !string.Equals(configPath, legacyConfigPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(legacyConfigPath))
        {
            var migratedSettings = await LoadAsync(legacyConfigPath, cancellationToken);
            await migratedSettings.SaveAsync(configPath, cancellationToken);
            migratedSettings.ConfigurationNotice =
                $"Your configuration was moved to {configPath}. The original file was left unchanged.";
            return migratedSettings;
        }

        return await LoadAsync(configPath, cancellationToken);
    }

    internal static async Task<AppSettings> LoadAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            var defaultSettings = new AppSettings
            {
                RefreshIntervalSeconds = 5,
                Devices = []
            };
            await defaultSettings.SaveAsync(configPath, cancellationToken);
            return defaultSettings;
        }

        await using var stream = File.OpenRead(configPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfiguration("The root value must be a JSON object.");
        }

        var refreshSeconds = 5;
        if (root.TryGetProperty("RefreshIntervalSeconds", out var refreshElement))
        {
            if (refreshElement.ValueKind != JsonValueKind.Number || !refreshElement.TryGetInt32(out refreshSeconds))
            {
                throw InvalidConfiguration("RefreshIntervalSeconds must be a whole number.");
            }

            refreshSeconds = Math.Clamp(refreshSeconds, 2, 300);
        }

        var devices = new List<DeviceSettings>();
        if (root.TryGetProperty("Devices", out var deviceArray))
        {
            if (deviceArray.ValueKind != JsonValueKind.Array)
            {
                throw InvalidConfiguration("Devices must be a JSON array.");
            }

            var index = 0;
            foreach (var element in deviceArray.EnumerateArray())
            {
                devices.Add(ReadDevice(element, $"Devices[{index}]"));
                index++;
            }
        }
        else if (root.TryGetProperty("LinkPi", out var legacyDevice))
        {
            devices.Add(ReadDevice(legacyDevice, "LinkPi"));
        }
        else
        {
            throw InvalidConfiguration("Either Devices or the legacy LinkPi device must be present.");
        }

        var duplicate = devices
            .GroupBy(device => device.BaseUrl, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw InvalidConfiguration($"The device URL '{duplicate.Key}' is configured more than once.");
        }

        return new AppSettings { Devices = devices, RefreshIntervalSeconds = refreshSeconds };
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(ConfigPath, cancellationToken);

    internal async Task SaveAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var persistedSettings = new PersistedSettings(
                Devices.Select(device => new PersistedDevice(
                    device.Name,
                    device.BaseUrl,
                    device.Username,
                    CredentialProtector.Protect(device.Password))).ToList(),
                RefreshIntervalSeconds);
            var json = JsonSerializer.Serialize(persistedSettings, SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, configPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static DeviceSettings ReadDevice(JsonElement element, string location)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfiguration($"{location} must be a JSON object.");
        }

        var baseUrl = ReadString(element, "BaseUrl", location, required: true);
        if (!TryNormalizeBaseUrl(baseUrl, out var normalizedBaseUrl, out var validationMessage))
        {
            throw InvalidConfiguration($"{location}.BaseUrl {validationMessage}");
        }

        var parsedUri = new Uri(normalizedBaseUrl, UriKind.Absolute);
        var name = ReadString(element, "Name", location).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = parsedUri.Host;
        }

        return new DeviceSettings
        {
            Name = name,
            BaseUrl = normalizedBaseUrl,
            Username = ReadString(element, "Username", location).Trim(),
            Password = CredentialProtector.Unprotect(ReadString(element, "Password", location))
        };
    }

    private static string ReadString(
        JsonElement element,
        string propertyName,
        string location,
        bool required = false)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            if (required)
            {
                throw InvalidConfiguration($"{location}.{propertyName} is required.");
            }

            return string.Empty;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidConfiguration($"{location}.{propertyName} must be a string.");
        }

        var value = property.GetString()!;
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw InvalidConfiguration($"{location}.{propertyName} cannot be empty.");
        }

        return value;
    }

    internal static bool TryNormalizeBaseUrl(
        string value,
        out string normalizedBaseUrl,
        out string validationMessage)
    {
        var baseUrl = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUri) ||
            string.IsNullOrWhiteSpace(parsedUri.Host) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            normalizedBaseUrl = string.Empty;
            validationMessage = "must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUri.UserInfo) ||
            parsedUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsedUri.Query) ||
            !string.IsNullOrEmpty(parsedUri.Fragment))
        {
            normalizedBaseUrl = string.Empty;
            validationMessage = "must contain only a scheme, host, and optional port.";
            return false;
        }

        normalizedBaseUrl = parsedUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        validationMessage = string.Empty;
        return true;
    }

    private static InvalidDataException InvalidConfiguration(string message) =>
        new($"Invalid config.json: {message}");

    private sealed record PersistedSettings(
        List<PersistedDevice> Devices,
        int RefreshIntervalSeconds);

    private sealed record PersistedDevice(
        string Name,
        string BaseUrl,
        string Username,
        string Password);

}

public sealed record DeviceSettings
{
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
