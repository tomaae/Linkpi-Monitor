using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Linkpi_Monitor;

public sealed class LinkPiClient : IDisposable
{
    internal const int MaximumPreviewBytes = 10 * 1024 * 1024;
    internal const long MaximumJsonBytes = 16 * 1024 * 1024;
    private static readonly Brush OnlineBrush = Freeze("#39D98A");
    private static readonly Brush WarningBrush = Freeze("#FFB547");
    private static readonly Brush OfflineBrush = Freeze("#77808C");

    private readonly DeviceSettings _device;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _previewTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _saveTimeout;
    private string? _channelConfigurationKey;
    private readonly Dictionary<int, ChannelConfiguration> _channelConfigurations = [];
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private bool _authenticated;
    private int _requestId;

    public LinkPiClient(DeviceSettings device)
        : this(device, new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            // Configuration writes can contain credentials and stream keys. Redirects are
            // deliberately handled as errors so a device cannot forward those requests.
            AllowAutoRedirect = false
        })
    {
    }

    internal LinkPiClient(
        DeviceSettings device,
        HttpMessageHandler handler,
        TimeSpan? previewTimeout = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? saveTimeout = null)
    {
        _device = device;
        _previewTimeout = previewTimeout ?? TimeSpan.FromSeconds(6);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(6);
        _saveTimeout = saveTimeout ?? TimeSpan.FromSeconds(30);
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(device.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaximumJsonBytes
        };
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_authenticated)
        {
            return;
        }

        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_authenticated)
            {
                return;
            }

            using var deadline = CreateDeadline(cancellationToken, _saveTimeout);
            using var response = await _httpClient.PostAsync(
                "link/action.php",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = _device.Username,
                    ["password"] = _device.Password
                }),
                deadline.Token).ConfigureAwait(false);
            ValidateResponseOrigin(response);
            if (response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("login.php", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException("LinkPi authentication failed.");
            }
            RejectRedirect(response);
            response.EnsureSuccessStatusCode();

            _authenticated = true;
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    private void ValidateResponseOrigin(HttpResponseMessage response)
    {
        var expected = _httpClient.BaseAddress!;
        if (response.RequestMessage?.RequestUri is not { } uri ||
            !uri.Scheme.Equals(expected.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != expected.Port)
        {
            throw new InvalidOperationException(
                "The LinkPi redirected the request to an unexpected host, scheme, or port.");
        }
    }

    private static void RejectRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidOperationException("The LinkPi returned an unexpected redirect.");
        }
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    public async Task SaveChannelConfigurationAsync(
        int channelId,
        ChannelConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var root = await GetMutableDefaultConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var channel = FindChannel(root, channelId);

        channel["name"] = configuration.General.Name;
        channel["enable"] = configuration.MainEncoder.Enabled;
        if (configuration.HasSubEncoder)
        {
            channel["enable2"] = configuration.SubEncoder.Enabled;
        }

        if (configuration.Decode.IsNetworkSource)
        {
            var net = EnsureObject(channel, "net");
            net["path"] = configuration.Decode.SourceUrl;
            net["framerate"] = NumberOrString(net, "framerate", configuration.Decode.InputFramerate);
            net["protocol"] = configuration.Decode.Protocol;
            net["bufferMode"] = NumberOrString(net, "bufferMode", configuration.Decode.BufferMode);
            net["minDelay"] = NumberOrString(net, "minDelay", configuration.Decode.MinimumDelay);
            net["decodeV"] = configuration.Decode.DecodeVideo;
            net["decodeA"] = configuration.Decode.DecodeAudio;
            ApplyPictureTransform(channel, configuration.Decode.Rotate, configuration.Decode.CropLeft,
                configuration.Decode.CropTop, configuration.Decode.CropRight, configuration.Decode.CropBottom,
                configuration.Decode.Contrast,
                configuration.Decode.HasDeinterlace ? configuration.Decode.Deinterlace : null,
                null);
        }
        else if (configuration.Input.IsHdmi)
        {
            ApplyPictureTransform(channel, configuration.Input.Rotate, configuration.Input.CropLeft,
                configuration.Input.CropTop, configuration.Input.CropRight, configuration.Input.CropBottom,
                configuration.Input.Contrast,
                configuration.Input.HasDeinterlace ? configuration.Input.Deinterlace : null,
                configuration.Input.NtscCompatible);
        }
        else if (configuration.Input.IsUsbCamera)
        {
            var capture = EnsureObject(channel, "capture");
            var size = configuration.Input.CaptureSize.Split('x', 2);
            if (size.Length == 2)
            {
                capture["width"] = NumberOrString(capture, "width", size[0]);
                capture["height"] = NumberOrString(capture, "height", size[1]);
            }
            capture["framerate"] = NumberOrString(capture, "framerate", configuration.Input.Framerate);
        }

        ApplyEncoder(EnsureObject(channel, "encv"), configuration.MainEncoder);
        if (configuration.HasSubEncoder)
        {
            ApplyEncoder(EnsureObject(channel, "encv2"), configuration.SubEncoder);
        }
        ApplyAudioEncoder(EnsureObject(channel, "enca"), configuration.Audio);
        ApplyStreamOutput(EnsureObject(channel, "stream"), configuration.MainStream);
        if (configuration.HasSubStream)
        {
            ApplyStreamOutput(EnsureObject(channel, "stream2"), configuration.SubStream);
        }
        if (configuration.HasHls)
        {
            ApplyHls(EnsureObject(channel, "hls"), configuration.Hls);
        }
        if (configuration.HasTransport)
        {
            ApplyTransport(EnsureObject(channel, "ts"), configuration.Transport);
        }
        if (configuration.HasNdi)
        {
            ApplyNdi(EnsureObject(channel, "ndi"), configuration.Ndi);
        }

        await SaveDefaultConfigurationAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public Task SavePushConfigurationAsync(
        PushConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        ExecuteAuthenticatedSaveAsync(
            token => SavePushConfigurationCoreAsync(configuration, token),
            cancellationToken);

    private async Task SavePushConfigurationCoreAsync(
        PushConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var current = await GetJsonAsync("config/push.json", cancellationToken, _saveTimeout).ConfigureAwait(false);
        var root = JsonNode.Parse(current.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("The LinkPi Push configuration is invalid.");
        root["autorun"] = configuration.AutorunStoredAsString
            ? JsonValue.Create(configuration.Autorun.ToString().ToLowerInvariant())
            : JsonValue.Create(configuration.Autorun);
        var existing = root["url"] as JsonArray;
        if (configuration.OriginalDestinationsJson is { } original &&
            !JsonNode.DeepEquals(JsonNode.Parse(original), existing))
        {
            throw new InvalidOperationException("Push destinations changed on the device. Reopen the configuration before saving.");
        }
        var destinations = new JsonArray();

        for (var index = 0; index < configuration.Destinations.Count; index++)
        {
            var source = configuration.Destinations[index];
            var target = source.OriginalIndex is int originalIndex && originalIndex >= 0 &&
                originalIndex < existing?.Count && existing[originalIndex] is JsonObject existingObject
                    ? (JsonObject)existingObject.DeepClone()
                    : new JsonObject();
            target["des"] = source.Name;
            target["enable"] = source.Enabled;
            target["type"] = source.Type;
            target["srcV"] = NumberOrString(target, "srcV", source.VideoSource);
            target["srcA"] = NumberOrString(target, "srcA", source.AudioSource);
            target["stream"] = source.Stream;
            target["path"] = source.Url;
            target["flvflags"] = source.Compatibility;
            destinations.Add(target);
        }

        root["url"] = destinations;
        var result = await InvokeRpcAsync(
            "RPC",
            "push.update",
            [root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })],
            cancellationToken,
            _saveTimeout).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("The LinkPi rejected the Push configuration.");
        }
    }

    public async Task SaveHardwareConfigurationAsync(
        HardwareConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var root = await GetMutableDefaultConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var mix = root.OfType<JsonObject>().FirstOrDefault(channel =>
            string.Equals(channel["type"]?.ToString(), "mix", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The LinkPi mix channel containing hardware settings was not found.");

        if (configuration.HasUsbAudioInput)
        {
            ApplyAudioInput(EnsureObject(mix, "inputUsbAlsa"), configuration.UsbAudioInput, includeEnable: true);
        }
        if (configuration.HasLineAudio)
        {
            ApplyAudioInput(EnsureObject(mix, "inputLine"), configuration.LineAudioInput, includeEnable: false);
            var lineOutput = EnsureObject(mix, "outputLine");
            lineOutput["src"] = configuration.LineAudioOutput.SourceStoredAsString
                ? JsonValue.Create(configuration.LineAudioOutput.Source)
                : NumberOrString(configuration.LineAudioOutput.Source);
            lineOutput["gain"] = NumberOrString(lineOutput, "gain", configuration.LineAudioOutput.Gain);
        }
        foreach (var videoOutput in configuration.VideoOutputs)
        {
            ApplyVideoOutput(EnsureObject(mix, videoOutput.ConfigurationKey), videoOutput);
        }

        await SaveDefaultConfigurationAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonArray> GetMutableDefaultConfigurationAsync(CancellationToken cancellationToken)
    {
        var current = await GetJsonAsync("config/config.json", cancellationToken, _saveTimeout).ConfigureAwait(false);
        return JsonNode.Parse(current.GetRawText()) as JsonArray
            ?? throw new InvalidOperationException("The LinkPi channel configuration is invalid.");
    }

    private Task SaveDefaultConfigurationAsync(JsonArray root, CancellationToken cancellationToken) =>
        ExecuteAuthenticatedSaveAsync(token => SaveDefaultConfigurationCoreAsync(root, token), cancellationToken);

    private async Task SaveDefaultConfigurationCoreAsync(JsonArray root, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken, _saveTimeout);
        using var response = await _httpClient.PostAsJsonAsync(
            "link/relay.php",
            new { url = "/conf/updateDefaultConf", data = root },
            deadline.Token).ConfigureAwait(false);
        ValidateResponseOrigin(response);
        ThrowIfAuthenticationExpired(response);
        RejectRedirect(response);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        if (!GetString(result, "status").Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The LinkPi rejected the configuration: {GetString(result, "msg", "unknown error")}");
        }
    }

    private async Task ExecuteAuthenticatedSaveAsync(
        Func<CancellationToken, Task> save,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await save(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (AuthenticationExpiredException)
        {
            _authenticated = false;
        }

        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        await save(cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfAuthenticationExpired(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        var locationPath = location is null
            ? string.Empty
            : location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            locationPath.EndsWith("login.php", StringComparison.OrdinalIgnoreCase) ||
            response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("login.php", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new AuthenticationExpiredException();
        }
    }

    public async Task<LinkPiSnapshot> GetSnapshotAsync(CancellationToken cancellationToken, bool includePreviews = true)
    {
        var configTask = ObserveAsync(GetJsonAsync("config/config.json", cancellationToken), "Channel configuration", cancellationToken);
        var pushConfigTask = ObserveAsync(GetJsonAsync("config/push.json", cancellationToken), "Push configuration", cancellationToken);
        var systemTask = ObserveAsync(InvokeRpcAsync("RPC", "enc.getSysState", cancellationToken), "System metrics", cancellationToken);
        var inputTask = ObserveAsync(InvokeRpcAsync("RPC", "enc.getInputState", cancellationToken), "Input state", cancellationToken);
        var epgTask = ObserveAsync(InvokeRpcAsync("RPC", "enc.getEPG", cancellationToken), "Stream discovery", cancellationToken);
        var pushStateTask = ObserveAsync(InvokeRpcAsync("RPC", "push.getState", cancellationToken), "Push state", cancellationToken);
        var hardwareTask = ObserveAsync(GetOptionalJsonAsync("config/hardware.json", cancellationToken), "Hardware capabilities", cancellationToken);

        await Task.WhenAll(configTask, pushConfigTask, systemTask, inputTask, epgTask, pushStateTask, hardwareTask)
            .ConfigureAwait(false);

        var configResult = await configTask;
        if (configResult.Error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(configResult.Error).Throw();
        }

        var pushConfigResult = await pushConfigTask;
        var systemResult = await systemTask;
        var inputResult = await inputTask;
        var epgResult = await epgTask;
        var pushStateResult = await pushStateTask;
        var hardwareResult = await hardwareTask;
        var warnings = new[] { pushConfigResult, systemResult, inputResult, epgResult, pushStateResult, hardwareResult }
            .Where(result => result.Error is not null)
            .Select(result => $"{result.Name} unavailable: {GetConciseMessage(result.Error!)}")
            .ToArray();

        var rawConfig = configResult.Value;
        var rawPushConfig = pushConfigResult.Value;
        var system = systemResult.Value;
        var rawHardware = hardwareResult.Value;
        var channels = ParseChannels(rawConfig, inputResult.Value, epgResult.Value, rawHardware);
        if (includePreviews) await LoadPreviewImagesAsync(channels, cancellationToken).ConfigureAwait(false);
        var pushState = pushStateResult.Value;
        var pushConfiguration = ParsePushConfiguration(rawPushConfig, channels, rawHardware);

        return new LinkPiSnapshot
        {
            CpuPercent = GetInt(system, "cpu"),
            MemoryPercent = GetInt(system, "mem"),
            TemperatureCelsius = GetInt(system, "temperature"),
            Channels = channels,
            PushDestinations = ParsePushDestinations(pushConfiguration, pushState, channels),
            PushConfiguration = pushConfiguration,
            Hardware = ParseHardwareConfiguration(rawConfig, rawHardware, channels),
            IsPushing = GetBool(pushState, "pushing"),
            HasSystemMetrics = systemResult.Error is null,
            Warnings = warnings
        };
    }

    private static async Task<QueryResult> ObserveAsync(
        Task<JsonElement> request,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return new QueryResult(name, await request.ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new QueryResult(name, default, exception);
        }
    }

    private static string GetConciseMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "request timed out",
        HttpRequestException { StatusCode: not null } http => $"HTTP {(int)http.StatusCode.Value}",
        _ => exception.Message
    };

    private async Task<JsonElement> GetJsonAsync(
        string relativeUrl,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var deadline = CreateDeadline(cancellationToken, timeout ?? _requestTimeout);
        using var response = await _httpClient.GetAsync(relativeUrl, deadline.Token);
        ValidateResponseOrigin(response);
        ThrowIfAuthenticationExpired(response);
        RejectRedirect(response);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> GetOptionalJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken, _requestTimeout);
        using var response = await _httpClient.GetAsync(relativeUrl, deadline.Token).ConfigureAwait(false);
        ValidateResponseOrigin(response);
        RejectRedirect(response);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> InvokeRpcAsync(
        string endpoint,
        string method,
        CancellationToken cancellationToken) =>
        await InvokeRpcAsync(endpoint, method, [], cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement> InvokeRpcAsync(
        string endpoint,
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = Interlocked.Increment(ref _requestId)
        };

        using var deadline = CreateDeadline(cancellationToken, timeout ?? _requestTimeout);
        using var response = await _httpClient.PostAsJsonAsync(endpoint, request, deadline.Token);
        ValidateResponseOrigin(response);
        ThrowIfAuthenticationExpired(response);
        RejectRedirect(response);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"LinkPi RPC {method} failed: {error.GetRawText()}");
        }

        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private static JsonObject FindChannel(JsonArray root, int channelId) =>
        root.OfType<JsonObject>().FirstOrDefault(channel => GetNodeInt(channel, "id", -1) == channelId)
        ?? throw new InvalidOperationException($"LinkPi channel {channelId} was not found.");

    private static int GetNodeInt(JsonObject value, string propertyName, int fallback = 0) =>
        int.TryParse(value[propertyName]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static JsonNode NumberOrString(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(value);

    private static JsonNode NumberOrString(JsonObject target, string propertyName, string value) =>
        target[propertyName] is JsonValue existing && existing.TryGetValue<string>(out _)
            ? JsonValue.Create(value)
            : NumberOrString(value);

    private static void ApplyPictureTransform(
        JsonObject channel,
        string rotate,
        string cropLeft,
        string cropTop,
        string cropRight,
        string cropBottom,
        string contrast,
        bool? deinterlace,
        bool? ntscCompatible)
    {
        var cap = EnsureObject(channel, "cap");
        var crop = EnsureObject(cap, "crop");
        cap["rotate"] = NumberOrString(cap, "rotate", rotate);
        cap["contrast"] = NumberOrString(cap, "contrast", contrast);
        if (deinterlace.HasValue)
        {
            cap["deinterlace"] = deinterlace.Value;
        }
        if (ntscCompatible.HasValue)
        {
            cap["ntsc"] = ntscCompatible.Value;
        }
        crop["L"] = NumberOrString(crop, "L", cropLeft);
        crop["T"] = NumberOrString(crop, "T", cropTop);
        crop["R"] = NumberOrString(crop, "R", cropRight);
        crop["B"] = NumberOrString(crop, "B", cropBottom);
    }

    private static void ApplyEncoder(JsonObject target, EncoderConfiguration source)
    {
        var size = source.VideoSize.Split('x', 2);
        if (size.Length == 2)
        {
            target["width"] = NumberOrString(target, "width", size[0]);
            target["height"] = NumberOrString(target, "height", size[1]);
        }
        var format = source.VideoFormat.Split(',', 2);
        target["codec"] = format[0];
        if (format.Length == 2)
        {
            target["profile"] = format[1];
        }
        target["rcmode"] = source.RateControl;
        target["bitrate"] = NumberOrString(target, "bitrate", source.Bitrate);
        target["framerate"] = NumberOrString(target, "framerate", source.Framerate);
        target["gop"] = NumberOrString(target, "gop", source.Gop);
        target["lowLatency"] = source.LowLatency;
        target["gopmode"] = NumberOrString(target, "gopmode", source.GopMode);
        target["minqp"] = NumberOrString(target, "minqp", source.MinimumQp);
        target["maxqp"] = NumberOrString(target, "maxqp", source.MaximumQp);
        target["Iqp"] = NumberOrString(target, "Iqp", source.FixedIQp);
        target["Pqp"] = NumberOrString(target, "Pqp", source.FixedPQp);
        var timestamp = source.TimestampMode.Split(',', 2);
        target["syncTS"] = bool.TryParse(timestamp[0], out var sync) && sync;
        if (timestamp.Length == 2)
        {
            target["syncTSMode"] = timestamp[1];
        }
    }

    private static void ApplyAudioEncoder(JsonObject target, AudioEncoderConfiguration source)
    {
        target["codec"] = source.Codec;
        target["audioSrc"] = NumberOrString(target, "audioSrc", source.Source);
        target["gain"] = NumberOrString(target, "gain", source.Gain);
        target["samplerate"] = NumberOrString(target, "samplerate", source.SampleRate);
        target["channels"] = NumberOrString(target, "channels", source.Channels);
        target["bitrate"] = NumberOrString(target, "bitrate", source.Bitrate);
    }

    private static void ApplyStreamOutput(JsonObject target, StreamOutputConfiguration source)
    {
        target["http"] = source.Http;
        target["hls"] = source.Hls;
        target["rtmp"] = source.Rtmp;
        target["webrtc"] = source.WebRtc;
        target["suffix"] = source.Suffix;

        var rtsp = EnsureObject(target, "rtsp");
        rtsp["enable"] = source.Rtsp.Enabled;
        rtsp["name"] = source.Rtsp.Username;
        rtsp["passwd"] = source.Rtsp.Password;
        rtsp["auth"] = source.Rtsp.Authentication;
        if (source.Rtsp.HasOnvif)
        {
            rtsp["onvif"] = source.Rtsp.Onvif;
        }

        var srt = EnsureObject(target, "srt");
        srt["enable"] = source.Srt.Enabled;
        srt["mode"] = source.Srt.Mode;
        srt["ip"] = source.Srt.IpAddress;
        if (source.Srt.HasStreamId)
        {
            srt["streamid"] = source.Srt.StreamId;
        }
        srt["port"] = NumberOrString(srt, "port", source.Srt.Port);
        srt["latency"] = NumberOrString(srt, "latency", source.Srt.Latency);
        srt["passwd"] = source.Srt.Password;

        var udp = EnsureObject(target, "udp");
        udp["enable"] = source.Udp.Enabled;
        udp["ip"] = source.Udp.IpAddress;
        udp["port"] = NumberOrString(udp, "port", source.Udp.Port);
        udp["ttl"] = NumberOrString(udp, "ttl", source.Udp.Ttl);
        udp["flowCtrl"] = source.Udp.FlowControl;
        udp["bandwidth"] = NumberOrString(udp, "bandwidth", source.Udp.Bandwidth);
        udp["rtp"] = source.Udp.RtpHeader;

        var rist = EnsureObject(target, "rist");
        rist["enable"] = source.Rist.Enabled;
        rist["ip"] = source.Rist.IpAddress;
        rist["port"] = NumberOrString(rist, "port", source.Rist.Port);

        var push = EnsureObject(target, "push");
        push["enable"] = source.Push.Enabled;
        push["path"] = source.Push.Url;
        push["format"] = source.Push.Format;
        push["hevc_id"] = NumberOrString(push, "hevc_id", source.Push.HevcId);
        push["flvflags"] = source.Push.Compatibility;
    }

    private static void ApplyHls(JsonObject target, HlsConfiguration source)
    {
        target["hls_time"] = NumberOrString(target, "hls_time", source.SegmentLength);
        target["hls_list_size"] = NumberOrString(target, "hls_list_size", source.ListLength);
        target["hls_base_url"] = source.BaseUrl;
        target["hls_filename"] = source.Filename;
    }

    private static void ApplyTransport(JsonObject target, TransportStreamConfiguration source)
    {
        target["tsSize"] = NumberOrString(target, "tsSize", source.PacketSize);
        target["mpegts_start_pid"] = NumberOrString(target, "mpegts_start_pid", source.Pid);
        target["mpegts_pmt_start_pid"] = NumberOrString(target, "mpegts_pmt_start_pid", source.PmtPid);
        target["mpegts_service_id"] = NumberOrString(target, "mpegts_service_id", source.ServiceId);
        target["mpegts_transport_stream_id"] = NumberOrString(target, "mpegts_transport_stream_id", source.StreamId);
        target["mpegts_original_network_id"] = NumberOrString(target, "mpegts_original_network_id", source.NetworkId);
    }

    private static void ApplyNdi(JsonObject target, NdiConfiguration source)
    {
        target["enable"] = source.Enabled;
        target["name"] = source.Name;
        target["group"] = source.Group;
    }

    private static void ApplyAudioInput(JsonObject target, AudioInputConfiguration source, bool includeEnable)
    {
        if (source.HasName)
        {
            target["name"] = source.Name;
        }
        target["anr"] = NumberOrString(target, "anr", source.NoiseReduction);
        target["anr_level"] = NumberOrString(target, "anr_level", source.NoiseReductionLevel);
        target["gain"] = NumberOrString(target, "gain", source.Gain);
        if (includeEnable)
        {
            target["enable"] = source.Enabled;
        }
    }

    private static void ApplyVideoOutput(JsonObject target, VideoOutputConfiguration source)
    {
        target["enable"] = source.Enabled;
        target["type"] = source.Type;
        target["output"] = source.Resolution;
        target["rotate"] = NumberOrString(target, "rotate", source.Rotate);
        if (source.HasMirror)
        {
            target["mirror"] = source.Mirror;
        }
        target["src"] = NumberOrString(target, "src", source.Source);
        target["lowLatency"] = source.LowLatency;
        var csc = EnsureObject(target, "csc");
        csc["matrix"] = source.ColorMatrix;
        csc["luma"] = source.ColorValuesStoredAsString ? JsonValue.Create(source.Luma) : NumberOrString(source.Luma);
        csc["contrast"] = source.ColorValuesStoredAsString ? JsonValue.Create(source.Contrast) : NumberOrString(source.Contrast);
        csc["saturation"] = source.ColorValuesStoredAsString ? JsonValue.Create(source.Saturation) : NumberOrString(source.Saturation);
        csc["hue"] = source.ColorValuesStoredAsString ? JsonValue.Create(source.Hue) : NumberOrString(source.Hue);
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
        var configurationKey = config.GetRawText() + "|" +
            (hardware.ValueKind == JsonValueKind.Undefined ? string.Empty : hardware.GetRawText());
        if (_channelConfigurationKey != configurationKey)
        {
            _channelConfigurations.Clear();
            _channelConfigurationKey = configurationKey;
        }
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
            var subStream = GetObject(channel, "stream2");
            var canPreview = CanPreview(channel);
            epgById.TryGetValue(id, out var epgEntry);
            inputStates.TryGetValue(id, out var sourceState);
            if (!_channelConfigurations.TryGetValue(id, out var configuration))
            {
                configuration = ParseChannelConfiguration(channel, audioSources, supports4K, supportsBFrames);
                _channelConfigurations[id] = configuration;
            }

            channels.Add(new ChannelDisplay
            {
                Id = id,
                Name = name,
                SourceType = GetSourceType(GetString(channel, "type")),
                Status = status.Text,
                StatusBrush = status.Brush,
                Initial = string.IsNullOrWhiteSpace(name) ? id.ToString(CultureInfo.InvariantCulture) : name[..1].ToUpperInvariant(),
                VideoSummary = GetVideoSummary(encoder),
                AudioSummary = GetAudioSummary(audio),
                OutputsSummary = GetOutputsSummary(stream, subStream),
                PreviewMessage = !enabled
                    ? "Stream is disabled"
                    : canPreview ? "Loading snapshot…" : "Preview unavailable",
                WatchUri = enabled ? GetWatchUri(epgEntry) : null,
                IsEnabled = enabled,
                CanPreview = canPreview,
                SourceWidth = GetInt(sourceState, "width"),
                SourceHeight = GetInt(sourceState, "height"),
                Configuration = configuration
            });
        }

        return channels;
    }

    private static bool IsUserFacingChannel(JsonElement channel)
    {
        var type = GetString(channel, "type");
        return !type.Equals("file", StringComparison.OrdinalIgnoreCase) &&
               !type.Equals("fine", StringComparison.OrdinalIgnoreCase) &&
               !type.Equals("image", StringComparison.OrdinalIgnoreCase) &&
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
                Name = GetString(channel, "name")
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
                HasDeinterlace = HasProperty(cap, "deinterlace"),
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
                HasDeinterlace = HasProperty(cap, "deinterlace"),
                Contrast = GetString(cap, "contrast", "0")
            },
            MainEncoder = ParseEncoder(GetObject(channel, "encv"), GetBool(channel, "enable"), supports4K, supportsBFrames),
            SubEncoder = ParseEncoder(GetObject(channel, "encv2"), GetBool(channel, "enable2"), supports4K, supportsBFrames),
            Audio = ParseAudioEncoder(GetObject(channel, "enca"), audioSources),
            MainStream = ParseStreamOutput(GetObject(channel, "stream")),
            SubStream = ParseStreamOutput(GetObject(channel, "stream2")),
            Hls = ParseHls(GetObject(channel, "hls")),
            Transport = ParseTransportStream(GetObject(channel, "ts")),
            Ndi = ParseNdi(GetObject(channel, "ndi")),
            HasSubEncoder = HasProperty(channel, "encv2"),
            HasSubStream = HasProperty(channel, "stream2"),
            HasHls = HasProperty(channel, "hls"),
            HasTransport = HasProperty(channel, "ts"),
            HasNdi = HasProperty(channel, "ndi")
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
                Onvif = GetBool(rtsp, "onvif"),
                HasOnvif = HasProperty(rtsp, "onvif")
            },
            Srt = new SrtConfiguration
            {
                Enabled = GetBool(srt, "enable"),
                Mode = GetString(srt, "mode", "listener"),
                IpAddress = GetString(srt, "ip"),
                StreamId = GetString(srt, "streamid"),
                HasStreamId = HasProperty(srt, "streamid"),
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_previewTimeout);
        try
        {
            var cacheKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var response = await _httpClient.GetAsync(
                $"snap/snap{channel.Id}.jpg?rnd={cacheKey}",
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The preview response is not an image.");
            }

            if (response.Content.Headers.ContentLength is > MaximumPreviewBytes)
            {
                throw new InvalidDataException("The preview image is too large.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(deadline.Token)
                .ConfigureAwait(false);
            using var imageStream = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                // Read at most one byte past the limit, even for chunked responses without a length.
                var remaining = (int)(MaximumPreviewBytes - imageStream.Length);
                var count = await responseStream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > remaining) throw new InvalidDataException("The preview image is too large.");
                imageStream.Write(buffer, 0, count);
            }
            if (imageStream.Length == 0)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            OriginalDestinationsJson = pushConfig.ValueKind == JsonValueKind.Object &&
                pushConfig.TryGetProperty("url", out var originalDestinations) &&
                originalDestinations.ValueKind == JsonValueKind.Array ? originalDestinations.GetRawText() : null,
            Autorun = GetBool(pushConfig, "autorun"),
            AutorunStoredAsString = PropertyIsString(pushConfig, "autorun"),
            VideoSources = videoSources,
            AudioSources = audioSources,
            Types = types
        };

        if (pushConfig.ValueKind != JsonValueKind.Object ||
            !pushConfig.TryGetProperty("url", out var destinations) || destinations.ValueKind != JsonValueKind.Array)
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
                OriginalIndex = configuration.Destinations.Count,
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
        var runtimeStatuses = pushState.ValueKind == JsonValueKind.Object &&
            pushState.TryGetProperty("status", out var statuses) && statuses.ValueKind == JsonValueKind.Array
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

    private static HardwareConfiguration ParseHardwareConfiguration(
        JsonElement config,
        JsonElement hardware,
        IReadOnlyList<ChannelDisplay> channels)
    {
        var functions = GetObject(hardware, "function");
        var capabilities = GetObject(hardware, "capability");
        var hasLineAudio = GetBool(functions, "line");
        var hasVideoOutput = GetBool(functions, "videoOut");
        var chip = GetString(hardware, "chip");
        var mix = config.ValueKind == JsonValueKind.Array
            ? config.EnumerateArray().FirstOrDefault(channel =>
                GetString(channel, "type").Equals("mix", StringComparison.OrdinalIgnoreCase))
            : default;
        var usbInput = GetObject(mix, "inputUsbAlsa");
        var lineInput = GetObject(mix, "inputLine");
        var lineOutput = GetObject(mix, "outputLine");
        var sourceOptions = channels.Select(channel => new SelectionOption(
            channel.Id.ToString(CultureInfo.InvariantCulture), channel.Name)).ToArray();
        var audioOutputSources = new List<SelectionOption>
        {
            new("line", "Line input"),
            new("usbAlsa", "USB microphone")
        };
        audioOutputSources.AddRange(sourceOptions);
        var currentAudioOutput = GetString(lineOutput, "src");
        var videoOutputs = new List<VideoOutputConfiguration>();

        if (hasVideoOutput)
        {
            AddVideoOutput(videoOutputs, GetObject(mix, "output"), "output", "HDMI output", sourceOptions, capabilities, alwaysVisible: true);
            AddVideoOutput(videoOutputs, GetObject(mix, "output2"), "output2", "Secondary output", sourceOptions, capabilities, alwaysVisible: false);
        }

        return new HardwareConfiguration
        {
            Model = GetString(hardware, "model", GetString(hardware, "fac")),
            Chip = chip,
            HasLineAudio = hasLineAudio,
            HasUsbAudioInput = usbInput.ValueKind == JsonValueKind.Object,
            HasVideoOutput = hasVideoOutput && videoOutputs.Count > 0,
            UsbAudioInput = ParseAudioInput(usbInput, "USB microphone", canDisable: true),
            LineAudioInput = ParseAudioInput(lineInput, "Line input", canDisable: false),
            LineAudioOutput = new AudioOutputConfiguration
            {
                Source = currentAudioOutput,
                SourceStoredAsString = PropertyIsString(lineOutput, "src"),
                Sources = EnsureOption(audioOutputSources, currentAudioOutput, $"Source {currentAudioOutput}"),
                Gain = GetString(lineOutput, "gain", "0")
            },
            VideoOutputs = videoOutputs
        };
    }

    private static AudioInputConfiguration ParseAudioInput(JsonElement input, string fallbackName, bool canDisable) => new()
    {
        Name = GetString(input, "name", fallbackName),
        HasName = HasProperty(input, "name"),
        Device = GetString(input, "usbid", canDisable ? "Not connected" : "Analog audio jack"),
        NoiseReduction = GetString(input, "anr", "0"),
        NoiseReductionLevel = GetString(input, "anr_level", "8"),
        Gain = GetString(input, "gain", "0"),
        Enabled = GetBool(input, "enable"),
        CanDisable = canDisable
    };

    private static void AddVideoOutput(
        ICollection<VideoOutputConfiguration> outputs,
        JsonElement output,
        string configurationKey,
        string fallbackName,
        IReadOnlyList<SelectionOption> sourceOptions,
        JsonElement capabilities,
        bool alwaysVisible)
    {
        if (output.ValueKind != JsonValueKind.Object || (!alwaysVisible && !GetBool(output, "ui")))
        {
            return;
        }

        var currentSource = GetString(output, "src");
        var currentResolution = GetString(output, "output", "1080P60");
        var csc = GetObject(output, "csc");
        var resolutions = new List<string>
        {
            "480P60", "576P50", "720P50", "720P60", "1080I50", "1080I60",
            "1080P24", "1080P25", "1080P30", "1080P50", "1080P60"
        };
        if (GetString(capabilities, "maxOutput").Contains("4K", StringComparison.OrdinalIgnoreCase))
        {
            resolutions.Add("4K30");
        }
        if (!resolutions.Contains(currentResolution, StringComparer.OrdinalIgnoreCase))
        {
            resolutions.Add(currentResolution);
        }

        outputs.Add(new VideoOutputConfiguration
        {
            ConfigurationKey = configurationKey,
            Name = fallbackName,
            Enabled = GetBool(output, "enable"),
            Type = GetString(output, "type", "hdmi"),
            Resolution = currentResolution,
            Rotate = GetString(output, "rotate", "0"),
            Mirror = GetBool(output, "mirror"),
            HasMirror = HasProperty(output, "mirror"),
            Source = currentSource,
            Sources = EnsureOption(sourceOptions, currentSource, $"Channel {currentSource}"),
            LowLatency = GetBool(output, "lowLatency"),
            ColorMatrix = GetString(csc, "matrix", "identity"),
            Luma = GetString(csc, "luma", "50"),
            Contrast = GetString(csc, "contrast", "50"),
            Saturation = GetString(csc, "saturation", "50"),
            Hue = GetString(csc, "hue", "50"),
            ColorValuesStoredAsString = PropertyIsString(csc, "luma"),
            Resolutions = resolutions
        });
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

        return ("Status unknown", OfflineBrush);
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

    private static string GetOutputsSummary(JsonElement stream, JsonElement subStream)
    {
        var outputs = GetEnabledOutputs(stream);
        var subOutputs = GetEnabledOutputs(subStream);
        if (subOutputs.Count == 0)
        {
            return outputs.Count == 0 ? "No outputs" : string.Join("  ·  ", outputs);
        }
        if (outputs.Count == 0)
        {
            return $"Sub: {string.Join("  ·  ", subOutputs)}";
        }
        return $"Main: {string.Join("  ·  ", outputs)}  ·  Sub: {string.Join("  ·  ", subOutputs)}";
    }

    private static List<string> GetEnabledOutputs(JsonElement stream)
    {
        var outputs = new List<string>();
        if (stream.ValueKind != JsonValueKind.Object) return outputs;
        foreach (var name in new[] { "http", "hls", "rtmp", "rtsp", "srt", "udp", "webrtc", "rist" })
        {
            if (stream.TryGetProperty(name, out var value) && IsEnabled(value))
            {
                outputs.Add(name.ToUpperInvariant());
            }
        }

        return outputs;
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

    private static string GetSourceType(string configuredType) => configuredType.ToLowerInvariant() switch
    {
        "vi" => "HDMI input",
        "usb" => "USB camera",
        "mix" => "Mix output",
        "net" => "Network decoder",
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

    private static bool HasProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out _);

    private static bool PropertyIsString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String;

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString()! : property.ToString();
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

    public void Dispose()
    {
        _authenticationGate.Dispose();
        _httpClient.Dispose();
    }

    private sealed class AuthenticationExpiredException : Exception
    {
        public AuthenticationExpiredException() : base("The LinkPi authentication session expired.") { }
    }

    private readonly record struct QueryResult(string Name, JsonElement Value, Exception? Error);
}
