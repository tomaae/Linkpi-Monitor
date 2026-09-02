using System.IO;
using System.Text.Json;

namespace Linkpi_Monitor;

public sealed class AppSettings
{
    public List<DeviceSettings> Devices { get; init; } = [];
    public int RefreshIntervalSeconds { get; init; } = 5;

    public static async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            var defaultSettings = new AppSettings
            {
                RefreshIntervalSeconds = 5,
                Devices =
                [
                    new DeviceSettings
                    {
                        Name = "LinkPi",
                        BaseUrl = "http://192.168.1.100",
                        Username = "admin",
                        Password = string.Empty
                    }
                ]
            };
            var json = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, json, cancellationToken);
            return defaultSettings;
        }

        await using var stream = File.OpenRead(configPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var refreshSeconds = root.TryGetProperty("RefreshIntervalSeconds", out var refreshElement)
            ? Math.Clamp(refreshElement.GetInt32(), 2, 300)
            : 5;

        var devices = new List<DeviceSettings>();
        if (root.TryGetProperty("Devices", out var deviceArray) && deviceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in deviceArray.EnumerateArray())
            {
                devices.Add(ReadDevice(element));
            }
        }
        else if (root.TryGetProperty("LinkPi", out var legacyDevice))
        {
            devices.Add(ReadDevice(legacyDevice));
        }

        devices.RemoveAll(device => string.IsNullOrWhiteSpace(device.BaseUrl));
        if (devices.Count == 0)
        {
            throw new InvalidDataException("config.json does not contain a LinkPi device with a BaseUrl.");
        }

        return new AppSettings { Devices = devices, RefreshIntervalSeconds = refreshSeconds };
    }

    private static DeviceSettings ReadDevice(JsonElement element)
    {
        var baseUrl = ReadString(element, "BaseUrl").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"'{baseUrl}' is not a valid HTTP or HTTPS LinkPi BaseUrl.");
        }

        var name = ReadString(element, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = parsedUri.Host;
        }

        return new DeviceSettings
        {
            Name = name,
            BaseUrl = baseUrl,
            Username = ReadString(element, "Username"),
            Password = ReadString(element, "Password")
        };
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}

public sealed class DeviceSettings
{
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
