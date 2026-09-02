using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media;

namespace Linkpi_Monitor;

public sealed class LinkPiClient : IDisposable
{
    private static readonly Brush OnlineBrush = Freeze("#39D98A");
    private static readonly Brush WarningBrush = Freeze("#FFB547");
    private static readonly Brush OfflineBrush = Freeze("#77808C");

    private readonly DeviceSettings _device;
    private readonly HttpClient _httpClient;
    private int _requestId;

    public LinkPiClient(DeviceSettings device)
    {
        _device = device;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(device.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    public async Task<LinkPiSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var configTask = GetJsonAsync("config/config.json", cancellationToken);
        var pushConfigTask = GetJsonAsync("config/push.json", cancellationToken);
        var systemTask = InvokeRpcAsync("RPC", "enc.getSysState", cancellationToken);
        var inputTask = InvokeRpcAsync("RPC", "enc.getInputState", cancellationToken);
        var epgTask = InvokeRpcAsync("RPC", "enc.getEPG", cancellationToken);
        var pushStateTask = InvokeRpcAsync("RPC", "push.getState", cancellationToken);

        await Task.WhenAll(configTask, pushConfigTask, systemTask, inputTask, epgTask, pushStateTask);

        var system = await systemTask;
        var channels = ParseChannels(await configTask, await inputTask, await epgTask);
        var pushState = await pushStateTask;

        return new LinkPiSnapshot
        {
            CpuPercent = GetInt(system, "cpu"),
            MemoryPercent = GetInt(system, "mem"),
            TemperatureCelsius = GetInt(system, "temperature"),
            Channels = channels,
            PushDestinations = ParsePushDestinations(await pushConfigTask, pushState, channels),
            IsPushing = GetBool(pushState, "pushing")
        };
    }

    private async Task<JsonElement> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> InvokeRpcAsync(
        string endpoint,
        string method,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = Array.Empty<object>(),
            id = Interlocked.Increment(ref _requestId)
        };

        using var response = await _httpClient.PostAsJsonAsync(endpoint, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"LinkPi RPC {method} failed: {error.GetRawText()}");
        }

        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private IReadOnlyList<ChannelDisplay> ParseChannels(
        JsonElement config,
        JsonElement inputState,
        JsonElement epg)
    {
        var inputAvailability = inputState.ValueKind == JsonValueKind.Array
            ? inputState.EnumerateArray().ToDictionary(
                item => GetInt(item, "chnId", -1),
                item => GetBool(item, "avalible"))
            : [];

        var epgById = epg.ValueKind == JsonValueKind.Array
            ? epg.EnumerateArray().ToDictionary(item => GetInt(item, "id", -1))
            : [];

        var channels = new List<ChannelDisplay>();
        if (config.ValueKind != JsonValueKind.Array)
        {
            return channels;
        }

        foreach (var channel in config.EnumerateArray())
        {
            var id = GetInt(channel, "id", channels.Count);
            var name = GetString(channel, "name", $"Channel {id}");
            var enabled = GetBool(channel, "enable");
            var status = GetChannelStatus(id, enabled, inputAvailability);
            var encoder = GetObject(channel, "encv");
            var audio = GetObject(channel, "enca");
            var stream = GetObject(channel, "stream");
            epgById.TryGetValue(id, out var epgEntry);

            channels.Add(new ChannelDisplay
            {
                Id = id,
                Name = name,
                SourceType = GetSourceType(id, GetString(channel, "type")),
                Status = status.Text,
                StatusBrush = status.Brush,
                Initial = string.IsNullOrWhiteSpace(name) ? id.ToString(CultureInfo.InvariantCulture) : name[..1].ToUpperInvariant(),
                VideoSummary = GetVideoSummary(encoder),
                AudioSummary = GetAudioSummary(audio),
                OutputsSummary = GetOutputsSummary(stream),
                PreviewMessage = enabled
                    ? "Snapshot unavailable through read-only API"
                    : "Stream is disabled",
                WatchUri = enabled ? GetWatchUri(epgEntry) : null,
                IsEnabled = enabled
            });
        }

        return channels;
    }

    private IReadOnlyList<PushDisplay> ParsePushDestinations(
        JsonElement pushConfig,
        JsonElement pushState,
        IReadOnlyList<ChannelDisplay> channels)
    {
        if (!pushConfig.TryGetProperty("url", out var destinations) || destinations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var runtimeStatuses = pushState.TryGetProperty("status", out var statuses) && statuses.ValueKind == JsonValueKind.Array
            ? statuses.EnumerateArray().ToArray()
            : [];
        var globallyPushing = GetBool(pushState, "pushing");
        var results = new List<PushDisplay>();
        var index = 0;

        foreach (var destination in destinations.EnumerateArray())
        {
            var runtime = index < runtimeStatuses.Length ? runtimeStatuses[index] : default;
            var enabled = GetBool(destination, "enable");
            var speed = GetInt(runtime, "speed");
            var isStreaming = enabled && globallyPushing && speed > 0;
            var sourceId = GetInt(destination, "srcV", -1);
            var sourceName = channels.FirstOrDefault(channel => channel.Id == sourceId)?.Name ?? $"Channel {sourceId}";

            results.Add(new PushDisplay
            {
                Index = index,
                Name = GetString(destination, "des", $"Push {index + 1}"),
                Type = GetString(destination, "type", "normal"),
                Source = sourceName,
                Destination = SanitizeDestination(GetString(destination, "path")),
                Status = !enabled ? "Disabled" : isStreaming ? "Streaming" : "Waiting",
                Speed = speed > 0 ? $"{speed / 1000d:0.0} Mbps" : "—",
                Duration = FormatDuration(GetLong(runtime, "duration")),
                StatusBrush = !enabled ? OfflineBrush : isStreaming ? OnlineBrush : WarningBrush
            });
            index++;
        }

        return results;
    }

    private (string Text, Brush Brush) GetChannelStatus(
        int id,
        bool enabled,
        IReadOnlyDictionary<int, bool> inputAvailability)
    {
        if (!enabled)
        {
            return ("Disabled", OfflineBrush);
        }

        if (inputAvailability.TryGetValue(id, out var available))
        {
            return available ? ("Online", OnlineBrush) : ("No signal", WarningBrush);
        }

        return ("Enabled", OnlineBrush);
    }

    private Uri? GetWatchUri(JsonElement epgEntry)
    {
        if (epgEntry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var advertisedUrls = GetString(epgEntry, "url")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var preferred = advertisedUrls.FirstOrDefault(value => value.StartsWith("rtsp:", StringComparison.OrdinalIgnoreCase))
            ?? advertisedUrls.FirstOrDefault(value => value.StartsWith("http:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(preferred))
        {
            return null;
        }

        var deviceUri = _httpClient.BaseAddress!;
        var schemeEnd = preferred.IndexOf(":///", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var scheme = preferred[..schemeEnd];
            var path = preferred[(schemeEnd + 3)..];
            var authority = scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) || deviceUri.IsDefaultPort
                ? deviceUri.Host
                : $"{deviceUri.Host}:{deviceUri.Port}";
            preferred = $"{scheme}://{authority}{path}";
        }

        return Uri.TryCreate(preferred, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string GetVideoSummary(JsonElement encoder)
    {
        var codec = GetString(encoder, "codec", "Video off");
        if (codec.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            return "Video disabled";
        }

        var width = GetInt(encoder, "width");
        var height = GetInt(encoder, "height");
        var framerate = GetInt(encoder, "framerate");
        var bitrate = GetInt(encoder, "bitrate");
        var rate = framerate < 0 ? "source fps" : $"{framerate} fps";
        return $"{codec.ToUpperInvariant()}  ·  {width}×{height}  ·  {rate}  ·  {bitrate} kbps";
    }

    private static string GetAudioSummary(JsonElement audio)
    {
        var codec = GetString(audio, "codec", "Audio off");
        var bitrate = GetInt(audio, "bitrate");
        var samplerate = GetInt(audio, "samplerate");
        return codec.Equals("close", StringComparison.OrdinalIgnoreCase)
            ? "Audio disabled"
            : $"{codec.ToUpperInvariant()}  ·  {bitrate} kbps  ·  {samplerate / 1000d:0.#} kHz";
    }

    private static string GetOutputsSummary(JsonElement stream)
    {
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return "No outputs";
        }

        var outputs = new List<string>();
        foreach (var name in new[] { "http", "hls", "rtmp", "rtsp", "srt", "udp", "webrtc", "rist" })
        {
            if (stream.TryGetProperty(name, out var value) && IsEnabled(value))
            {
                outputs.Add(name.ToUpperInvariant());
            }
        }

        return outputs.Count == 0 ? "No outputs" : string.Join("  ·  ", outputs);
    }

    private static bool IsEnabled(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var boolean)
                ? boolean
                : value.GetString() == "1",
            JsonValueKind.Object => GetBool(value, "enable"),
            _ => false
        };
    }

    private static string GetSourceType(int id, string configuredType) => id switch
    {
        0 => "HDMI input",
        1 => "USB camera",
        6 => "File source",
        7 => "Color key",
        8 => "Mix output",
        _ when configuredType.Contains("net", StringComparison.OrdinalIgnoreCase) => "Network decoder",
        _ => string.IsNullOrWhiteSpace(configuredType) ? "Stream" : configuredType
    };

    private static string SanitizeDestination(string path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(path) ? "Not configured" : "Configured destination";
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}";
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0 || seconds > TimeSpan.FromDays(30).TotalSeconds)
        {
            return "—";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
            : duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }

    private static JsonElement GetObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property
            : default;

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? fallback : property.ToString();
    }

    private static int GetInt(JsonElement element, string propertyName, int fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : fallback;
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return IsEnabled(property);
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public void Dispose() => _httpClient.Dispose();
}
