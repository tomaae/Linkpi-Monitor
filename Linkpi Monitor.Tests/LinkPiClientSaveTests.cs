using System.Net;
using System.Text;
using System.Text.Json;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class LinkPiClientSaveTests
{
    private static readonly DeviceSettings TestDevice = new()
    {
        Name = "Test LinkPi",
        BaseUrl = "http://linkpi.test",
        Username = "admin",
        Password = "test-password"
    };

    [Fact]
    public async Task SaveChannelPreservesFirmwareFieldsAndJsonValueTypes()
    {
        const string deviceJson = """
            [{
              "id":2,"type":"net","name":"Original","enable":false,"enable2":false,
              "firmwareOnly":{"token":"keep"},
              "net":{"path":"rtsp://old/source","framerate":"-1","protocol":"tcp","bufferMode":"0","minDelay":"500","decodeV":true,"decodeA":false,"customNet":7},
              "cap":{"rotate":"0","contrast":"0","crop":{"L":"0","T":"0","R":"0","B":"0"}},
              "encv":{"bitrate":1000},"encv2":{},"enca":{},"stream":{},"stream2":{},"hls":{},"ts":{},"ndi":{}
            }]
            """;
        using var handler = new RecordingHandler(deviceJson);
        using var client = new LinkPiClient(TestDevice, handler);
        var configuration = new ChannelConfiguration
        {
            General = new GeneralChannelConfiguration { Name = "Updated" },
            Decode = new DecodeConfiguration
            {
                IsNetworkSource = true,
                SourceUrl = "rtsp://new/source",
                InputFramerate = "25",
                Protocol = "udp",
                BufferMode = "2",
                MinimumDelay = "650",
                DecodeVideo = true,
                DecodeAudio = true,
                Rotate = "90",
                CropLeft = "4",
                CropTop = "6",
                CropRight = "8",
                CropBottom = "10",
                Contrast = "3"
            },
            MainEncoder = new EncoderConfiguration
            {
                Enabled = true,
                VideoSize = "1920x1080",
                VideoFormat = "h264,main",
                RateControl = "cbr",
                Bitrate = "2500",
                Framerate = "25",
                Gop = "2"
            }
        };

        await client.SaveChannelConfigurationAsync(2, configuration, TestContext.Current.CancellationToken);

        var relayRequest = Assert.Single(handler.Requests, request => request.Path == "/link/relay.php");
        using var relay = JsonDocument.Parse(relayRequest.Body);
        Assert.Equal("/conf/updateDefaultConf", relay.RootElement.GetProperty("url").GetString());
        var channel = relay.RootElement.GetProperty("data")[0];
        Assert.Equal("keep", channel.GetProperty("firmwareOnly").GetProperty("token").GetString());
        Assert.Equal(7, channel.GetProperty("net").GetProperty("customNet").GetInt32());
        Assert.Equal(JsonValueKind.String, channel.GetProperty("net").GetProperty("minDelay").ValueKind);
        Assert.Equal("650", channel.GetProperty("net").GetProperty("minDelay").GetString());
        Assert.Equal(JsonValueKind.Number, channel.GetProperty("encv").GetProperty("bitrate").ValueKind);
        Assert.Equal(2500, channel.GetProperty("encv").GetProperty("bitrate").GetInt32());
        Assert.Equal("Updated", channel.GetProperty("name").GetString());
        Assert.Equal(
            ["/config/config.json", "/link/action.php", "/link/relay.php"],
            handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task SavePushUsesUpdateOnlyAndPreservesUnknownFields()
    {
        const string pushJson = """
            {"autorun":"false","firmwareRoot":"keep-root","url":[{
              "des":"Original","enable":false,"type":"normal","srcV":"2","srcA":"close",
              "stream":"main","path":"rtmp://example.invalid/live","flvflags":"","firmwareOnly":42
            }]}
            """;
        using var handler = new RecordingHandler("[]", pushJson);
        using var client = new LinkPiClient(TestDevice, handler);
        var configuration = new PushConfiguration { Autorun = true, AutorunStoredAsString = true };
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            OriginalIndex = 0,
            Name = "Updated",
            Enabled = false,
            Type = "normal",
            VideoSource = "3",
            AudioSource = "close",
            Stream = "sub",
            Url = "rtmp://example.invalid/new",
            Compatibility = "ext_header"
        });

        await client.SavePushConfigurationAsync(configuration, TestContext.Current.CancellationToken);

        var rpcRequest = Assert.Single(handler.Requests, request => request.Path == "/RPC");
        using var rpc = JsonDocument.Parse(rpcRequest.Body);
        Assert.Equal("push.update", rpc.RootElement.GetProperty("method").GetString());
        var serializedConfiguration = rpc.RootElement.GetProperty("params")[0].GetString();
        Assert.NotNull(serializedConfiguration);
        using var saved = JsonDocument.Parse(serializedConfiguration);
        Assert.Equal("true", saved.RootElement.GetProperty("autorun").GetString());
        Assert.Equal("keep-root", saved.RootElement.GetProperty("firmwareRoot").GetString());
        var destination = saved.RootElement.GetProperty("url")[0];
        Assert.False(destination.GetProperty("enable").GetBoolean());
        Assert.Equal(42, destination.GetProperty("firmwareOnly").GetInt32());
        Assert.Equal("Updated", destination.GetProperty("des").GetString());
        Assert.Equal("sub", destination.GetProperty("stream").GetString());
        Assert.Equal(
            ["/link/action.php", "/config/push.json", "/RPC"],
            handler.Requests.Select(request => request.Path));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Body.Contains("push.start", StringComparison.OrdinalIgnoreCase) ||
            request.Body.Contains("push.stop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveHardwareTargetsMixByTypeAndPreservesOtherChannels()
    {
        const string deviceJson = """
            [
              {"id":8,"type":"net","marker":"network-eight"},
              {"id":13,"type":"mix","firmwareOnly":"keep-mix",
               "inputLine":{"name":"Line input","anr":"0","anr_level":"8","gain":"0"},
               "outputLine":{"src":"line","gain":"0"},
               "output":{"enable":true,"type":"hdmi","output":"1080P60","rotate":"0","src":"2","lowLatency":false,
                 "firmwareOutput":"keep-output","csc":{"matrix":"identity","luma":"50","contrast":"50","saturation":"50","hue":"50"}}}
            ]
            """;
        using var handler = new RecordingHandler(deviceJson);
        using var client = new LinkPiClient(TestDevice, handler);
        var configuration = new HardwareConfiguration
        {
            HasLineAudio = true,
            HasVideoOutput = true,
            LineAudioInput = new AudioInputConfiguration
            {
                Name = "Analog input",
                HasName = true,
                NoiseReduction = "1",
                NoiseReductionLevel = "6",
                Gain = "12"
            },
            LineAudioOutput = new AudioOutputConfiguration
            {
                Source = "3",
                SourceStoredAsString = true,
                Gain = "-6"
            },
            VideoOutputs =
            [
                new VideoOutputConfiguration
                {
                    ConfigurationKey = "output",
                    Enabled = true,
                    Type = "hdmi",
                    Resolution = "1080P50",
                    Rotate = "180",
                    Source = "3",
                    LowLatency = true,
                    ColorMatrix = "601_709",
                    Luma = "55",
                    Contrast = "56",
                    Saturation = "57",
                    Hue = "58",
                    ColorValuesStoredAsString = true
                }
            ]
        };

        await client.SaveHardwareConfigurationAsync(configuration, TestContext.Current.CancellationToken);

        var relayRequest = Assert.Single(handler.Requests, request => request.Path == "/link/relay.php");
        using var relay = JsonDocument.Parse(relayRequest.Body);
        var channels = relay.RootElement.GetProperty("data");
        Assert.Equal("network-eight", channels[0].GetProperty("marker").GetString());
        var mix = channels[1];
        Assert.Equal("keep-mix", mix.GetProperty("firmwareOnly").GetString());
        Assert.Equal("12", mix.GetProperty("inputLine").GetProperty("gain").GetString());
        Assert.Equal("3", mix.GetProperty("outputLine").GetProperty("src").GetString());
        var output = mix.GetProperty("output");
        Assert.Equal("keep-output", output.GetProperty("firmwareOutput").GetString());
        Assert.Equal("1080P50", output.GetProperty("output").GetString());
        Assert.Equal("55", output.GetProperty("csc").GetProperty("luma").GetString());
    }

    [Fact]
    public void DeviceSettingsSerializationHasNoWritePermissionFlag()
    {
        var json = JsonSerializer.Serialize(TestDevice);

        Assert.DoesNotContain("AllowChanges", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CanSaveChanges", json, StringComparison.Ordinal);
    }

    private sealed record CapturedRequest(string Method, string Path, string Body);

    private sealed class RecordingHandler(string deviceJson, string pushJson = "{}") : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add(new CapturedRequest(request.Method.Method, path, body));

            var content = path switch
            {
                "/config/config.json" => deviceJson,
                "/config/push.json" => pushJson,
                "/link/action.php" => "{}",
                "/link/relay.php" => "{\"status\":\"success\",\"msg\":\"ok\"}",
                "/RPC" => "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}",
                _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {path}")
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
