using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        var hardwareTask = GetOptionalJsonAsync("config/hardware.json", cancellationToken);

        await Task.WhenAll(configTask, pushConfigTask, systemTask, inputTask, epgTask, pushStateTask, hardwareTask)
            .ConfigureAwait(false);

        var system = await systemTask;
        var rawConfig = await configTask;
        var rawPushConfig = await pushConfigTask;
        var rawHardware = await hardwareTask;
        var channels = ParseChannels(rawConfig, await inputTask, await epgTask, rawHardware);
        await LoadPreviewImagesAsync(channels, cancellationToken).ConfigureAwait(false);
        var pushState = await pushStateTask;
        var pushConfiguration = ParsePushConfiguration(rawPushConfig, channels, rawHardware);

        return new LinkPiSnapshot
        {
            CpuPercent = GetInt(system, "cpu"),
            MemoryPercent = GetInt(system, "mem"),
            TemperatureCelsius = GetInt(system, "temperature"),
            Channels = channels,
            PushDestinations = ParsePushDestinations(pushConfiguration, pushState, channels),
            PushConfiguration = pushConfiguration,
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

    private async Task<JsonElement> GetOptionalJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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
        JsonElement epg,
        JsonElement hardware)
    {
        var inputStates = inputState.ValueKind == JsonValueKind.Array
            ? inputState.EnumerateArray().ToDictionary(
                item => GetInt(item, "chnId", -1))
            : [];
        var inputAvailability = inputStates.ToDictionary(
            item => item.Key,
            item => GetBool(item.Value, "avalible"));

        var epgById = epg.ValueKind == JsonValueKind.Array
            ? epg.EnumerateArray().ToDictionary(item => GetInt(item, "id", -1))
            : [];

        if (config.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var configuredChannels = config.EnumerateArray()
            .Where(IsUserFacingChannel)
            .ToArray();
        var audioSources = BuildAudioSourceOptions(configuredChannels);
        var encodeCapabilities = GetObject(GetObject(hardware, "capability"), "encode");
        var supports4K = GetString(encodeCapabilities, "maxSize").Contains("4K", StringComparison.OrdinalIgnoreCase);
        var supportsBFrames = GetBool(encodeCapabilities, "BFrame");
        var channels = new List<ChannelDisplay>(configuredChannels.Length);

        foreach (var channel in configuredChannels)
        {
            var id = GetInt(channel, "id", channels.Count);
            var name = GetString(channel, "name", $"Channel {id}");
            var enabled = GetBool(channel, "enable");
            var status = GetChannelStatus(id, enabled, inputAvailability);
            var encoder = GetObject(channel, "encv");
            var audio = GetObject(channel, "enca");
            var stream = GetObject(channel, "stream");
            var canPreview = CanPreview(channel);
            epgById.TryGetValue(id, out var epgEntry);
            inputStates.TryGetValue(id, out var sourceState);

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
                PreviewMessage = !enabled
                    ? "Stream is disabled"
                    : canPreview ? "Loading snapshot…" : "Preview unavailable",
                WatchUri = enabled ? GetWatchUri(epgEntry) : null,
                IsEnabled = enabled,
                CanPreview = canPreview,
                SourceWidth = GetInt(sourceState, "width"),
                SourceHeight = GetInt(sourceState, "height"),
                Configuration = ParseChannelConfiguration(channel, audioSources, supports4K, supportsBFrames)
            });
        }

        return channels;
    }

    private static bool IsUserFacingChannel(JsonElement channel)
    {
        var type = GetString(channel, "type");
        return !type.Equals("file", StringComparison.OrdinalIgnoreCase) &&
               !type.Equals("fine", StringComparison.OrdinalIgnoreCase) &&
               !type.Equals("colorKey", StringComparison.OrdinalIgnoreCase);
    }

    private static ChannelConfiguration ParseChannelConfiguration(
        JsonElement channel,
        IReadOnlyList<SelectionOption> audioSources,
        bool supports4K,
        bool supportsBFrames)
    {
        var type = GetString(channel, "type");
        var net = GetObject(channel, "net");
        var cap = GetObject(channel, "cap");
        var crop = GetObject(cap, "crop");
        var capture = GetObject(channel, "capture");
        var captureSize = $"{GetString(capture, "width", "1920")}x{GetString(capture, "height", "1080")}";
        var isHdmi = type.Equals("vi", StringComparison.OrdinalIgnoreCase);
        var isUsb = type.Equals("usb", StringComparison.OrdinalIgnoreCase);

        return new ChannelConfiguration
        {
            General = new GeneralChannelConfiguration
            {
                Name = GetString(channel, "name"),
                Enabled = GetBool(channel, "enable")
            },
            Input = new PhysicalInputConfiguration
            {
                IsHdmi = isHdmi,
                IsUsbCamera = isUsb,
                Interface = GetString(channel, "interface", isHdmi ? "HDMI" : "USB"),
                Device = GetString(channel, "rdir", isUsb ? "No camera detected" : string.Empty),
                CaptureSize = captureSize,
                CaptureSizes = BuildCaptureSizeOptions(captureSize),
                Framerate = GetString(capture, "framerate", "30"),
                Rotate = GetString(cap, "rotate", "0"),
                CropLeft = GetString(crop, "L", "0"),
                CropTop = GetString(crop, "T", "0"),
                CropRight = GetString(crop, "R", "0"),
                CropBottom = GetString(crop, "B", "0"),
                Contrast = GetString(cap, "contrast", "0"),
                Deinterlace = GetBool(cap, "deinterlace"),
                NtscCompatible = GetBool(cap, "ntsc")
            },
            Decode = new DecodeConfiguration
            {
                IsNetworkSource = type.Equals("net", StringComparison.OrdinalIgnoreCase),
                SourceUrl = GetString(net, "path"),
                InputFramerate = GetString(net, "framerate", "-1"),
                Protocol = GetString(net, "protocol", "tcp"),
                BufferMode = GetString(net, "bufferMode", "0"),
                MinimumDelay = GetString(net, "minDelay", "500"),
                DecodeVideo = GetBool(net, "decodeV"),
                DecodeAudio = GetBool(net, "decodeA"),
                Rotate = GetString(cap, "rotate", "0"),
                CropLeft = GetString(crop, "L", "0"),
                CropTop = GetString(crop, "T", "0"),
                CropRight = GetString(crop, "R", "0"),
                CropBottom = GetString(crop, "B", "0"),
                Deinterlace = GetBool(cap, "deinterlace"),
                Contrast = GetString(cap, "contrast", "0")
            },
            MainEncoder = ParseEncoder(GetObject(channel, "encv"), GetBool(channel, "enable"), supports4K, supportsBFrames),
            SubEncoder = ParseEncoder(GetObject(channel, "encv2"), GetBool(channel, "enable2"), supports4K, supportsBFrames),
            Audio = ParseAudioEncoder(GetObject(channel, "enca"), audioSources),
            MainStream = ParseStreamOutput(GetObject(channel, "stream")),
            SubStream = ParseStreamOutput(GetObject(channel, "stream2")),
            Hls = ParseHls(GetObject(channel, "hls")),
            Transport = ParseTransportStream(GetObject(channel, "ts")),
            Ndi = ParseNdi(GetObject(channel, "ndi"))
        };
    }

    private static EncoderConfiguration ParseEncoder(
        JsonElement encoder,
        bool enabled,
        bool supports4K,
        bool supportsBFrames)
    {
        var width = GetString(encoder, "width", "-1");
        var height = GetString(encoder, "height", "-1");
        var size = $"{width}x{height}";
        var timestampMode = GetString(encoder, "syncTSMode", "linkpi");

        return new EncoderConfiguration
        {
            Enabled = enabled,
            VideoSize = size,
            VideoSizes = BuildVideoSizeOptions(size, supports4K),
            VideoFormat = $"{GetString(encoder, "codec", "close")},{GetString(encoder, "profile", "base")}",
            RateControl = GetString(encoder, "rcmode", "cbr"),
            Bitrate = GetString(encoder, "bitrate"),
            Framerate = GetString(encoder, "framerate", "-1"),
            Gop = GetString(encoder, "gop", "1"),
            LowLatency = GetBool(encoder, "lowLatency"),
            GopMode = GetString(encoder, "gopmode", "0"),
            GopModes = BuildGopModeOptions(supportsBFrames),
            MinimumQp = GetString(encoder, "minqp", "22"),
            MaximumQp = GetString(encoder, "maxqp", "36"),
            FixedIQp = GetString(encoder, "Iqp", "25"),
            FixedPQp = GetString(encoder, "Pqp", "25"),
            TimestampMode = $"{GetBool(encoder, "syncTS").ToString().ToLowerInvariant()},{timestampMode}"
        };
    }

    private static AudioEncoderConfiguration ParseAudioEncoder(
        JsonElement audio,
        IReadOnlyList<SelectionOption> sourceOptions)
    {
        var currentSource = GetString(audio, "audioSrc");
        return new AudioEncoderConfiguration
        {
            Codec = GetString(audio, "codec", "close"),
            Source = currentSource,
            Sources = EnsureOption(sourceOptions, currentSource, $"Source {currentSource}"),
            Gain = GetString(audio, "gain", "0"),
            SampleRate = GetString(audio, "samplerate", "-1"),
            Channels = GetString(audio, "channels", "2"),
            Bitrate = GetString(audio, "bitrate")
        };
    }

    private static StreamOutputConfiguration ParseStreamOutput(JsonElement stream)
    {
        var rtsp = GetObject(stream, "rtsp");
        var srt = GetObject(stream, "srt");
        var udp = GetObject(stream, "udp");
        var rist = GetObject(stream, "rist");
        var push = GetObject(stream, "push");

        return new StreamOutputConfiguration
        {
            Http = GetBool(stream, "http"),
            Hls = GetBool(stream, "hls"),
            Rtmp = GetBool(stream, "rtmp"),
            WebRtc = GetBool(stream, "webrtc"),
            Suffix = GetString(stream, "suffix"),
            Rtsp = new RtspConfiguration
            {
                Enabled = rtsp.ValueKind == JsonValueKind.Object
                    ? GetBool(rtsp, "enable")
                    : IsEnabled(rtsp),
                Username = GetString(rtsp, "name"),
                Password = GetString(rtsp, "passwd"),
                Authentication = GetBool(rtsp, "auth"),
                Onvif = GetBool(rtsp, "onvif")
            },
            Srt = new SrtConfiguration
            {
                Enabled = GetBool(srt, "enable"),
                Mode = GetString(srt, "mode", "listener"),
                IpAddress = GetString(srt, "ip"),
                StreamId = GetString(srt, "streamid"),
                Port = GetString(srt, "port"),
                Latency = GetString(srt, "latency"),
                Password = GetString(srt, "passwd")
            },
            Udp = new UdpConfiguration
            {
                Enabled = GetBool(udp, "enable"),
                IpAddress = GetString(udp, "ip"),
                Port = GetString(udp, "port"),
                Ttl = GetString(udp, "ttl", "5"),
                FlowControl = GetBool(udp, "flowCtrl"),
                Bandwidth = GetString(udp, "bandwidth", "100"),
                RtpHeader = GetBool(udp, "rtp")
            },
            Rist = new RistConfiguration
            {
                Enabled = GetBool(rist, "enable"),
                IpAddress = GetString(rist, "ip"),
                Port = GetString(rist, "port")
            },
            Push = new PushStreamConfiguration
            {
                Enabled = GetBool(push, "enable"),
                Url = GetString(push, "path"),
                Format = GetString(push, "format", "auto"),
                HevcId = GetString(push, "hevc_id", "12"),
                Compatibility = GetString(push, "flvflags")
            }
        };
    }

    private static HlsConfiguration ParseHls(JsonElement hls) => new()
    {
        SegmentLength = GetString(hls, "hls_time"),
        ListLength = GetString(hls, "hls_list_size"),
        BaseUrl = GetString(hls, "hls_base_url"),
        Filename = GetString(hls, "hls_filename")
    };

    private static TransportStreamConfiguration ParseTransportStream(JsonElement transport) => new()
    {
        PacketSize = GetString(transport, "tsSize", "1316"),
        Pid = GetString(transport, "mpegts_start_pid"),
        PmtPid = GetString(transport, "mpegts_pmt_start_pid"),
        ServiceId = GetString(transport, "mpegts_service_id"),
        StreamId = GetString(transport, "mpegts_transport_stream_id"),
        NetworkId = GetString(transport, "mpegts_original_network_id")
    };

    private static NdiConfiguration ParseNdi(JsonElement ndi) => new()
    {
        Enabled = GetBool(ndi, "enable"),
        Name = GetString(ndi, "name"),
        Group = GetString(ndi, "group")
    };

    private static IReadOnlyList<SelectionOption> BuildAudioSourceOptions(IEnumerable<JsonElement> channels)
    {
        var options = new List<SelectionOption>
        {
            new("source", "Source default"),
            new("line", "Line input"),
            new("usbAlsa", "USB microphone")
        };

        options.AddRange(channels.Select(channel => new SelectionOption(
            GetString(channel, "id"),
            GetString(channel, "name", $"Channel {GetString(channel, "id")}"))));
        return options;
    }

    private static IReadOnlyList<SelectionOption> BuildVideoSizeOptions(string current, bool supports4K)
    {
        var standard = new List<SelectionOption>
        {
            new("-1x-1", "Automatic"),
            new("1920x1080", "1080p (1920×1080)"),
            new("1280x720", "720p (1280×720)"),
            new("640x360", "360p (640×360)"),
            new("1080x1920", "Portrait 1080×1920"),
            new("720x1280", "Portrait 720×1280"),
            new("360x640", "Portrait 360×640")
        };

        if (supports4K)
        {
            standard.Insert(1, new SelectionOption("3840x2160", "4K (3840×2160)"));
        }

        return EnsureOption(standard, current, current.Replace('x', '×'));
    }

    private static IReadOnlyList<SelectionOption> BuildCaptureSizeOptions(string current)
    {
        IReadOnlyList<SelectionOption> standard =
        [
            new("3840x2160", "4K (3840×2160)"),
            new("1920x1080", "1080p (1920×1080)"),
            new("1280x720", "720p (1280×720)"),
            new("640x480", "VGA (640×480)"),
            new("640x360", "360p (640×360)")
        ];
        return EnsureOption(standard, current, current.Replace('x', '×'));
    }

    private static IReadOnlyList<SelectionOption> BuildGopModeOptions(bool supportsBFrames)
    {
        var modes = new List<SelectionOption>
        {
            new("0", "Normal"),
            new("1", "SmartP"),
            new("2", "DualP")
        };
        if (supportsBFrames)
        {
            modes.Add(new SelectionOption("3", "BiPredB"));
        }

        return modes;
    }

    private static IReadOnlyList<SelectionOption> EnsureOption(
        IReadOnlyList<SelectionOption> options,
        string value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value) || options.Any(option => option.Value == value))
        {
            return options;
        }

        return [.. options, new SelectionOption(value, label)];
    }

    private async Task LoadPreviewImagesAsync(
        IReadOnlyList<ChannelDisplay> channels,
        CancellationToken cancellationToken)
    {
        var previewChannels = channels.Where(channel => channel.CanPreview).ToArray();
        if (previewChannels.Length == 0)
        {
            return;
        }

        try
        {
            // This is the same passive preview cycle used by the LinkPi dashboard.
            await InvokeRpcAsync("RPC", "enc.snap", cancellationToken).ConfigureAwait(false);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            foreach (var channel in previewChannels)
            {
                channel.PreviewMessage = "Snapshot unavailable";
            }

            return;
        }

        await Task.WhenAll(previewChannels.Select(channel => LoadPreviewImageAsync(channel, cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task LoadPreviewImageAsync(ChannelDisplay channel, CancellationToken cancellationToken)
    {
        try
        {
            var cacheKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var response = await _httpClient.GetAsync(
                $"snap/snap{channel.Id}.jpg?rnd={cacheKey}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The preview response is not an image.");
            }

            if (response.Content.Headers.ContentLength is > 10 * 1024 * 1024)
            {
                throw new InvalidDataException("The preview image is too large.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var imageStream = new MemoryStream();
            await responseStream.CopyToAsync(imageStream, cancellationToken).ConfigureAwait(false);
            if (imageStream.Length is <= 0 or > 10 * 1024 * 1024)
            {
                throw new InvalidDataException("The preview image has an invalid size.");
            }

            imageStream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 730;
            image.StreamSource = imageStream;
            image.EndInit();
            image.Freeze();

            channel.PreviewImage = image;
            channel.PreviewMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            channel.PreviewMessage = "Snapshot unavailable";
        }
    }

    private static bool CanPreview(JsonElement channel)
    {
        if (!GetBool(channel, "enable"))
        {
            return false;
        }

        var type = GetString(channel, "type");
        if (type.Equals("ndi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !type.Equals("net", StringComparison.OrdinalIgnoreCase) ||
            GetBool(GetObject(channel, "net"), "decodeV");
    }

    private static PushConfiguration ParsePushConfiguration(
        JsonElement pushConfig,
        IReadOnlyList<ChannelDisplay> channels,
        JsonElement hardware)
    {
        var videoSources = channels.Select(channel => new SelectionOption(
            channel.Id.ToString(CultureInfo.InvariantCulture), channel.Name)).ToArray();
        var audioSources = new List<SelectionOption> { new("close", "Disabled") };
        audioSources.AddRange(videoSources);
        var types = new List<SelectionOption>
        {
            new("normal", "Normal push"),
            new("webrtc", "WebRTC")
        };
        if (GetString(hardware, "chip").Equals("SS524V100", StringComparison.OrdinalIgnoreCase))
        {
            types.Add(new SelectionOption("trtc", "TRTC"));
        }

        var configuration = new PushConfiguration
        {
            Autorun = GetBool(pushConfig, "autorun"),
            VideoSources = videoSources,
            AudioSources = audioSources,
            Types = types
        };

        if (!pushConfig.TryGetProperty("url", out var destinations) || destinations.ValueKind != JsonValueKind.Array)
        {
            return configuration;
        }

        foreach (var destination in destinations.EnumerateArray())
        {
            var videoSource = GetString(destination, "srcV");
            var audioSource = GetString(destination, "srcA", "close");
            var type = GetString(destination, "type", "normal");
            configuration.Destinations.Add(new PushDestinationConfiguration
            {
                Name = GetString(destination, "des", $"Push {configuration.Destinations.Count + 1}"),
                Type = type,
                Types = EnsureOption(types, type, type),
                VideoSource = videoSource,
                VideoSources = EnsureOption(videoSources, videoSource, $"Channel {videoSource}"),
                AudioSource = audioSource,
                AudioSources = EnsureOption(audioSources, audioSource, $"Source {audioSource}"),
                Stream = GetString(destination, "stream", "main"),
                Url = GetString(destination, "path"),
                Compatibility = GetString(destination, "flvflags"),
                Enabled = GetBool(destination, "enable")
            });
        }

        return configuration;
    }

    private IReadOnlyList<PushDisplay> ParsePushDestinations(
        PushConfiguration pushConfiguration,
        JsonElement pushState,
        IReadOnlyList<ChannelDisplay> channels)
    {
        var runtimeStatuses = pushState.TryGetProperty("status", out var statuses) && statuses.ValueKind == JsonValueKind.Array
            ? statuses.EnumerateArray().ToArray()
            : [];
        var globallyPushing = GetBool(pushState, "pushing");
        var results = new List<PushDisplay>();
        var index = 0;

        foreach (var destination in pushConfiguration.Destinations)
        {
            var runtime = index < runtimeStatuses.Length ? runtimeStatuses[index] : default;
            var enabled = destination.Enabled;
            var speed = GetInt(runtime, "speed");
            var isStreaming = enabled && globallyPushing && speed > 0;
            var sourceId = int.TryParse(destination.VideoSource, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSource)
                ? parsedSource
                : -1;
            var sourceName = channels.FirstOrDefault(channel => channel.Id == sourceId)?.Name ?? $"Channel {sourceId}";

            results.Add(new PushDisplay
            {
                Index = index,
                Name = destination.Name,
                Type = destination.Type,
                Source = sourceName,
                Destination = SanitizeDestination(destination.Url),
                Status = !enabled ? "Disabled" : isStreaming ? "Streaming" : "Waiting",
                Speed = speed > 0 ? $"{speed / 1000d:0.0} Mbps" : "—",
                Duration = FormatDuration(GetLong(runtime, "duration")),
                StatusBrush = !enabled ? OfflineBrush : isStreaming ? OnlineBrush : WarningBrush,
                Configuration = destination
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
