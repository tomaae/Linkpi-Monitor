using System.Net;
using System.Text.Json;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class LinkPiClientExtendedSaveTests
{
    [Fact]
    public async Task SaveChannelWritesEverySupportedHdmiEncoderAndStreamField()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = """
                [{
                  "id":0,"type":"vi","name":"Old","enable":false,"enable2":false,"firmwareOnly":"keep",
                  "cap":{"rotate":"0","contrast":"0","deinterlace":false,"ntsc":false,"crop":{"L":"0","T":"0","R":"0","B":"0"}},
                  "encv":{"width":1920,"height":1080,"bitrate":1000,"customEncoder":7},"encv2":{},"enca":{},
                  "stream":{"rtsp":{"onvif":false},"srt":{"streamid":"old"},"customStream":"keep"},"stream2":{},
                  "hls":{},"ts":{},"ndi":{}
                }]
                """
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = CompleteChannelConfiguration();

        await client.SaveChannelConfigurationAsync(0, configuration);

        var channel = SavedChannels(handler)[0];
        Assert.Equal("Updated HDMI", channel.GetProperty("name").GetString());
        Assert.True(channel.GetProperty("enable").GetBoolean());
        Assert.True(channel.GetProperty("enable2").GetBoolean());
        Assert.Equal("keep", channel.GetProperty("firmwareOnly").GetString());

        var cap = channel.GetProperty("cap");
        Assert.Equal("180", cap.GetProperty("rotate").GetString());
        Assert.Equal("5", cap.GetProperty("contrast").GetString());
        Assert.True(cap.GetProperty("deinterlace").GetBoolean());
        Assert.True(cap.GetProperty("ntsc").GetBoolean());
        Assert.Equal("11", cap.GetProperty("crop").GetProperty("L").GetString());
        Assert.Equal("12", cap.GetProperty("crop").GetProperty("T").GetString());
        Assert.Equal("13", cap.GetProperty("crop").GetProperty("R").GetString());
        Assert.Equal("14", cap.GetProperty("crop").GetProperty("B").GetString());

        AssertEncoder(channel.GetProperty("encv"), 1920, 1080, "h265", "main", 7000, 50, 2, true, 3, 17, 43, 23, 27, true, "sinsam");
        Assert.Equal(7, channel.GetProperty("encv").GetProperty("customEncoder").GetInt32());
        AssertEncoder(channel.GetProperty("encv2"), 640, 360, "h264", "base", 900, 25, 4, false, 1, 20, 40, 25, 29, false, "linkpi");

        var audio = channel.GetProperty("enca");
        Assert.Equal("opus", audio.GetProperty("codec").GetString());
        Assert.Equal(0, audio.GetProperty("audioSrc").GetInt32());
        Assert.Equal(12, audio.GetProperty("gain").GetInt32());
        Assert.Equal(48000, audio.GetProperty("samplerate").GetInt32());
        Assert.Equal(2, audio.GetProperty("channels").GetInt32());
        Assert.Equal(192, audio.GetProperty("bitrate").GetInt32());

        AssertStream(channel.GetProperty("stream"), expectedSuffix: "main", expectedPort: 9000);
        Assert.Equal("keep", channel.GetProperty("stream").GetProperty("customStream").GetString());
        AssertStream(channel.GetProperty("stream2"), expectedSuffix: "sub", expectedPort: 9002);

        var hls = channel.GetProperty("hls");
        Assert.Equal(4, hls.GetProperty("hls_time").GetInt32());
        Assert.Equal(6, hls.GetProperty("hls_list_size").GetInt32());
        Assert.Equal("/base/", hls.GetProperty("hls_base_url").GetString());
        Assert.Equal("segment-%d.ts", hls.GetProperty("hls_filename").GetString());

        var transport = channel.GetProperty("ts");
        Assert.Equal(1316, transport.GetProperty("tsSize").GetInt32());
        Assert.Equal(256, transport.GetProperty("mpegts_start_pid").GetInt32());
        Assert.Equal(4096, transport.GetProperty("mpegts_pmt_start_pid").GetInt32());
        Assert.Equal(10, transport.GetProperty("mpegts_service_id").GetInt32());
        Assert.Equal(20, transport.GetProperty("mpegts_transport_stream_id").GetInt32());
        Assert.Equal(30, transport.GetProperty("mpegts_original_network_id").GetInt32());

        var ndi = channel.GetProperty("ndi");
        Assert.True(ndi.GetProperty("enable").GetBoolean());
        Assert.Equal("NDI output", ndi.GetProperty("name").GetString());
        Assert.Equal("Public", ndi.GetProperty("group").GetString());
    }

    [Fact]
    public async Task SaveUsbChannelWritesCaptureSizeAndFramerateWithExistingTypes()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = """
                [{"id":"1","type":"usb","capture":{"width":"640","height":480,"framerate":"30"},"encv":{},"encv2":{},"enca":{},"stream":{},"stream2":{},"hls":{},"ts":{},"ndi":{}}]
                """
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = new ChannelConfiguration
        {
            General = new GeneralChannelConfiguration { Name = "USB" },
            Input = new PhysicalInputConfiguration
            {
                IsUsbCamera = true,
                CaptureSize = "1280x720",
                Framerate = "60"
            }
        };

        await client.SaveChannelConfigurationAsync(1, configuration);

        var capture = SavedChannels(handler)[0].GetProperty("capture");
        Assert.Equal(JsonValueKind.String, capture.GetProperty("width").ValueKind);
        Assert.Equal("1280", capture.GetProperty("width").GetString());
        Assert.Equal(JsonValueKind.Number, capture.GetProperty("height").ValueKind);
        Assert.Equal(720, capture.GetProperty("height").GetInt32());
        Assert.Equal("60", capture.GetProperty("framerate").GetString());
    }

    [Fact]
    public async Task SaveNetworkOmitsUnsupportedOptionalDecodeAndStreamFields()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = """
                [{"id":2,"type":"net","net":{},"cap":{"crop":{}},"encv":{},"encv2":{},"enca":{},"stream":{"rtsp":{},"srt":{}},"stream2":{},"hls":{},"ts":{},"ndi":{}}]
                """
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = new ChannelConfiguration
        {
            Decode = new DecodeConfiguration
            {
                IsNetworkSource = true,
                DecodeVideo = true,
                DecodeAudio = false,
                HasDeinterlace = false,
                Deinterlace = true
            },
            MainStream = new StreamOutputConfiguration
            {
                Rtsp = new RtspConfiguration { HasOnvif = false, Onvif = true },
                Srt = new SrtConfiguration { HasStreamId = false, StreamId = "must-not-be-added" }
            }
        };

        await client.SaveChannelConfigurationAsync(2, configuration);

        var channel = SavedChannels(handler)[0];
        Assert.False(channel.GetProperty("cap").TryGetProperty("deinterlace", out _));
        Assert.False(channel.GetProperty("stream").GetProperty("rtsp").TryGetProperty("onvif", out _));
        Assert.False(channel.GetProperty("stream").GetProperty("srt").TryGetProperty("streamid", out _));
    }

    [Fact]
    public async Task SaveChannelRejectsMissingChannelBeforeAuthentication()
    {
        var handler = new LinkPiTestHandler { ConfigJson = "[{\"id\":1}]" };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SaveChannelConfigurationAsync(99, new ChannelConfiguration()));

        Assert.Contains("channel 99", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/link/action.php");
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/link/relay.php");
    }

    [Fact]
    public async Task SaveChannelRejectsNonArrayConfiguration()
    {
        var handler = new LinkPiTestHandler { ConfigJson = "{\"invalid\":true}" };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SaveChannelConfigurationAsync(0, new ChannelConfiguration()));

        Assert.Contains("configuration is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectedDefaultConfigurationIncludesDeviceMessage()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = "[{\"id\":0}]",
            RelayJson = "{\"status\":\"failed\",\"msg\":\"invalid value\"}"
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SaveChannelConfigurationAsync(0, new ChannelConfiguration()));

        Assert.Contains("invalid value", exception.Message);
    }

    [Fact]
    public async Task SavePushTruncatesRemovedDestinationsAddsNewOnesAndPreservesValueTypes()
    {
        var handler = new LinkPiTestHandler
        {
            PushJson = """
                {"autorun":true,"rootOnly":"keep","url":[
                  {"des":"First","enable":true,"type":"normal","srcV":"0","srcA":1,"stream":"main","path":"old","flvflags":"","unknown":"keep"},
                  {"des":"Removed","enable":true,"type":"normal","srcV":2,"srcA":"close","stream":"main","path":"removed","flvflags":""}
                ]}
                """
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = new PushConfiguration { Autorun = false, AutorunStoredAsString = false };
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            OriginalIndex = 0,
            Name = "Only",
            Enabled = false,
            Type = "webrtc",
            VideoSource = "3",
            AudioSource = "4",
            Stream = "sub",
            Url = "https://push.example/live/key",
            Compatibility = "ext_header"
        });

        await client.SavePushConfigurationAsync(configuration);

        var saved = SavedPush(handler);
        Assert.False(saved.GetProperty("autorun").GetBoolean());
        Assert.Equal("keep", saved.GetProperty("rootOnly").GetString());
        var destination = Assert.Single(saved.GetProperty("url").EnumerateArray());
        Assert.Equal("keep", destination.GetProperty("unknown").GetString());
        Assert.Equal("Only", destination.GetProperty("des").GetString());
        Assert.False(destination.GetProperty("enable").GetBoolean());
        Assert.Equal("webrtc", destination.GetProperty("type").GetString());
        Assert.Equal("3", destination.GetProperty("srcV").GetString());
        Assert.Equal(4, destination.GetProperty("srcA").GetInt32());
        Assert.Equal("sub", destination.GetProperty("stream").GetString());
        Assert.Equal("https://push.example/live/key", destination.GetProperty("path").GetString());
        Assert.Equal("ext_header", destination.GetProperty("flvflags").GetString());
    }

    [Fact]
    public async Task SavePushCreatesNumericSourceForNewDestination()
    {
        var handler = new LinkPiTestHandler { PushJson = "{\"url\":[]}" };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = new PushConfiguration();
        configuration.Destinations.Add(new PushDestinationConfiguration
        {
            Name = "New",
            VideoSource = "8",
            AudioSource = "close"
        });

        await client.SavePushConfigurationAsync(configuration);

        var destination = SavedPush(handler).GetProperty("url")[0];
        Assert.Equal(JsonValueKind.Number, destination.GetProperty("srcV").ValueKind);
        Assert.Equal(8, destination.GetProperty("srcV").GetInt32());
        Assert.Equal(JsonValueKind.String, destination.GetProperty("srcA").ValueKind);
        Assert.Equal("close", destination.GetProperty("srcA").GetString());
    }

    [Fact]
    public async Task SavePushRejectsNonObjectConfiguration()
    {
        var handler = new LinkPiTestHandler { PushJson = "[]" };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SavePushConfigurationAsync(new PushConfiguration()));
    }

    [Fact]
    public async Task SavePushRejectsFalseRpcResult()
    {
        var handler = new LinkPiTestHandler { PushJson = "{}" };
        handler.RpcResults["push.update"] = "false";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SavePushConfigurationAsync(new PushConfiguration()));

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticationIsReusedAcrossSaves()
    {
        var handler = new LinkPiTestHandler { PushJson = "{}" };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        await client.SavePushConfigurationAsync(new PushConfiguration());
        await client.SavePushConfigurationAsync(new PushConfiguration());

        Assert.Single(handler.Requests, request => request.Path == "/link/action.php");
        Assert.Equal(2, handler.Requests.Count(request => request.RpcMethod == "push.update"));
        var login = Assert.Single(handler.Requests, request => request.Path == "/link/action.php");
        Assert.Contains("username=test-user", login.Body);
        Assert.Contains("password=test-password", login.Body);
    }

    [Theory]
    [InlineData("http://linkpi.test/login.php", "authentication failed")]
    [InlineData("http://unexpected.test/home", "unexpected host")]
    [InlineData("https://linkpi.test/home", "unexpected host")]
    public async Task AuthenticationRejectsLoginRedirectAndCrossHostRedirect(string resultUri, string message)
    {
        var handler = new LinkPiTestHandler
        {
            PushJson = "{}",
            AuthenticationResultUri = new Uri(resultUri)
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SavePushConfigurationAsync(new PushConfiguration()));

        Assert.Contains(message, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/config/push.json");
    }

    [Fact]
    public async Task ParsedChannelSaveDoesNotMaterializeUnsupportedSections()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = "[{\"id\":5,\"type\":\"vi\",\"name\":\"Input\",\"encv\":{},\"enca\":{},\"stream\":{}}]"
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var channel = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None, includePreviews: false)).Channels);
        channel.Configuration.General.Name = "Renamed";

        await client.SaveChannelConfigurationAsync(channel.Id, channel.Configuration);

        var saved = SavedChannels(handler)[0];
        Assert.False(saved.TryGetProperty("encv2", out _));
        Assert.False(saved.TryGetProperty("stream2", out _));
        Assert.False(saved.TryGetProperty("hls", out _));
        Assert.False(saved.TryGetProperty("ts", out _));
        Assert.False(saved.TryGetProperty("ndi", out _));
    }

    [Fact]
    public async Task AuthenticationHttpFailurePropagates()
    {
        var handler = new LinkPiTestHandler { AuthenticationStatusCode = HttpStatusCode.Unauthorized };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SavePushConfigurationAsync(new PushConfiguration()));
    }

    [Fact]
    public async Task SaveHardwareWritesUsbInputNumericColorsAndOptionalMirror()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = """
                [{"id":8,"type":"mix","unknown":"keep",
                  "inputUsbAlsa":{"name":"Old","anr":0,"anr_level":8,"gain":0,"enable":false,"usbid":"keep-device"},
                  "output":{"enable":false,"rotate":0,"src":0,"csc":{"luma":50,"contrast":50,"saturation":50,"hue":50}}}]
                """
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = new HardwareConfiguration
        {
            HasUsbAudioInput = true,
            UsbAudioInput = new AudioInputConfiguration
            {
                HasName = true,
                Name = "USB microphone",
                NoiseReduction = "1",
                NoiseReductionLevel = "4",
                Gain = "18",
                Enabled = true
            },
            HasVideoOutput = true,
            VideoOutputs =
            [
                new VideoOutputConfiguration
                {
                    ConfigurationKey = "output",
                    Enabled = true,
                    Type = "hdmi",
                    Resolution = "4K30",
                    Rotate = "90",
                    HasMirror = true,
                    Mirror = true,
                    Source = "3",
                    LowLatency = true,
                    ColorMatrix = "601_709",
                    Luma = "61",
                    Contrast = "62",
                    Saturation = "63",
                    Hue = "64",
                    ColorValuesStoredAsString = false
                }
            ]
        };

        await client.SaveHardwareConfigurationAsync(configuration);

        var mix = SavedChannels(handler)[0];
        Assert.Equal("keep", mix.GetProperty("unknown").GetString());
        var input = mix.GetProperty("inputUsbAlsa");
        Assert.Equal("USB microphone", input.GetProperty("name").GetString());
        Assert.Equal(1, input.GetProperty("anr").GetInt32());
        Assert.Equal(4, input.GetProperty("anr_level").GetInt32());
        Assert.Equal(18, input.GetProperty("gain").GetInt32());
        Assert.True(input.GetProperty("enable").GetBoolean());
        Assert.Equal("keep-device", input.GetProperty("usbid").GetString());

        var output = mix.GetProperty("output");
        Assert.True(output.GetProperty("enable").GetBoolean());
        Assert.True(output.GetProperty("mirror").GetBoolean());
        Assert.Equal(90, output.GetProperty("rotate").GetInt32());
        Assert.Equal(3, output.GetProperty("src").GetInt32());
        Assert.Equal("4K30", output.GetProperty("output").GetString());
        Assert.True(output.GetProperty("lowLatency").GetBoolean());
        Assert.Equal(61, output.GetProperty("csc").GetProperty("luma").GetInt32());
    }

    [Fact]
    public async Task SaveHardwareRejectsConfigurationWithoutMixChannel()
    {
        var handler = new LinkPiTestHandler { ConfigJson = "[{\"id\":8,\"type\":\"net\"}]" };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SaveHardwareConfigurationAsync(new HardwareConfiguration()));

        Assert.Contains("mix channel", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/link/action.php");
    }

    private static ChannelConfiguration CompleteChannelConfiguration() => new()
    {
        General = new GeneralChannelConfiguration { Name = "Updated HDMI" },
        Input = new PhysicalInputConfiguration
        {
            IsHdmi = true,
            Rotate = "180",
            CropLeft = "11",
            CropTop = "12",
            CropRight = "13",
            CropBottom = "14",
            Contrast = "5",
            HasDeinterlace = true,
            Deinterlace = true,
            NtscCompatible = true
        },
        MainEncoder = Encoder(true, "1920x1080", "h265,main", "vbr", "7000", "50", "2", true, "3", "17", "43", "23", "27", "true,sinsam"),
        SubEncoder = Encoder(true, "640x360", "h264,base", "cbr", "900", "25", "4", false, "1", "20", "40", "25", "29", "false,linkpi"),
        Audio = new AudioEncoderConfiguration
        {
            Codec = "opus",
            Source = "0",
            Gain = "12",
            SampleRate = "48000",
            Channels = "2",
            Bitrate = "192"
        },
        MainStream = Stream("main", 9000),
        SubStream = Stream("sub", 9002),
        Hls = new HlsConfiguration { SegmentLength = "4", ListLength = "6", BaseUrl = "/base/", Filename = "segment-%d.ts" },
        Transport = new TransportStreamConfiguration { PacketSize = "1316", Pid = "256", PmtPid = "4096", ServiceId = "10", StreamId = "20", NetworkId = "30" },
        Ndi = new NdiConfiguration { Enabled = true, Name = "NDI output", Group = "Public" }
    };

    private static EncoderConfiguration Encoder(
        bool enabled, string size, string format, string rateControl, string bitrate, string framerate,
        string gop, bool lowLatency, string gopMode, string minQp, string maxQp, string iQp, string pQp,
        string timestamp) => new()
        {
            Enabled = enabled,
            VideoSize = size,
            VideoFormat = format,
            RateControl = rateControl,
            Bitrate = bitrate,
            Framerate = framerate,
            Gop = gop,
            LowLatency = lowLatency,
            GopMode = gopMode,
            MinimumQp = minQp,
            MaximumQp = maxQp,
            FixedIQp = iQp,
            FixedPQp = pQp,
            TimestampMode = timestamp
        };

    private static StreamOutputConfiguration Stream(string suffix, int port) => new()
    {
        Http = true,
        Hls = true,
        Rtmp = true,
        WebRtc = true,
        Suffix = suffix,
        Rtsp = new RtspConfiguration
        {
            Enabled = true,
            Username = "viewer",
            Password = "rtsp-secret",
            Authentication = true,
            HasOnvif = true,
            Onvif = true
        },
        Srt = new SrtConfiguration
        {
            Enabled = true,
            Mode = "caller",
            IpAddress = "203.0.113.10",
            HasStreamId = true,
            StreamId = suffix,
            Port = port.ToString(),
            Latency = "120",
            Password = "srt-secret"
        },
        Udp = new UdpConfiguration
        {
            Enabled = true,
            IpAddress = "239.1.1.1",
            Port = (port + 1).ToString(),
            Ttl = "7",
            FlowControl = true,
            Bandwidth = "150",
            RtpHeader = true
        },
        Rist = new RistConfiguration { Enabled = true, IpAddress = "198.51.100.2", Port = (port + 2).ToString() },
        Push = new PushStreamConfiguration
        {
            Enabled = true,
            Url = $"rtmp://push.example/{suffix}/key",
            Format = "flv",
            HevcId = "15",
            Compatibility = "ext_header"
        }
    };

    private static void AssertEncoder(
        JsonElement encoder, int width, int height, string codec, string profile, int bitrate, int framerate,
        int gop, bool lowLatency, int gopMode, int minQp, int maxQp, int iQp, int pQp, bool sync,
        string syncMode)
    {
        Assert.Equal(width, encoder.GetProperty("width").GetInt32());
        Assert.Equal(height, encoder.GetProperty("height").GetInt32());
        Assert.Equal(codec, encoder.GetProperty("codec").GetString());
        Assert.Equal(profile, encoder.GetProperty("profile").GetString());
        Assert.Equal(bitrate, encoder.GetProperty("bitrate").GetInt32());
        Assert.Equal(framerate, encoder.GetProperty("framerate").GetInt32());
        Assert.Equal(gop, encoder.GetProperty("gop").GetInt32());
        Assert.Equal(lowLatency, encoder.GetProperty("lowLatency").GetBoolean());
        Assert.Equal(gopMode, encoder.GetProperty("gopmode").GetInt32());
        Assert.Equal(minQp, encoder.GetProperty("minqp").GetInt32());
        Assert.Equal(maxQp, encoder.GetProperty("maxqp").GetInt32());
        Assert.Equal(iQp, encoder.GetProperty("Iqp").GetInt32());
        Assert.Equal(pQp, encoder.GetProperty("Pqp").GetInt32());
        Assert.Equal(sync, encoder.GetProperty("syncTS").GetBoolean());
        Assert.Equal(syncMode, encoder.GetProperty("syncTSMode").GetString());
    }

    private static void AssertStream(JsonElement stream, string expectedSuffix, int expectedPort)
    {
        Assert.True(stream.GetProperty("http").GetBoolean());
        Assert.True(stream.GetProperty("hls").GetBoolean());
        Assert.True(stream.GetProperty("rtmp").GetBoolean());
        Assert.True(stream.GetProperty("webrtc").GetBoolean());
        Assert.Equal(expectedSuffix, stream.GetProperty("suffix").GetString());
        var rtsp = stream.GetProperty("rtsp");
        Assert.True(rtsp.GetProperty("enable").GetBoolean());
        Assert.Equal("viewer", rtsp.GetProperty("name").GetString());
        Assert.Equal("rtsp-secret", rtsp.GetProperty("passwd").GetString());
        Assert.True(rtsp.GetProperty("auth").GetBoolean());
        Assert.True(rtsp.GetProperty("onvif").GetBoolean());
        var srt = stream.GetProperty("srt");
        Assert.True(srt.GetProperty("enable").GetBoolean());
        Assert.Equal("caller", srt.GetProperty("mode").GetString());
        Assert.Equal("203.0.113.10", srt.GetProperty("ip").GetString());
        Assert.Equal(expectedSuffix, srt.GetProperty("streamid").GetString());
        Assert.Equal(expectedPort, srt.GetProperty("port").GetInt32());
        Assert.Equal(120, srt.GetProperty("latency").GetInt32());
        Assert.Equal("srt-secret", srt.GetProperty("passwd").GetString());
        var udp = stream.GetProperty("udp");
        Assert.True(udp.GetProperty("enable").GetBoolean());
        Assert.Equal("239.1.1.1", udp.GetProperty("ip").GetString());
        Assert.Equal(expectedPort + 1, udp.GetProperty("port").GetInt32());
        Assert.Equal(7, udp.GetProperty("ttl").GetInt32());
        Assert.True(udp.GetProperty("flowCtrl").GetBoolean());
        Assert.Equal(150, udp.GetProperty("bandwidth").GetInt32());
        Assert.True(udp.GetProperty("rtp").GetBoolean());
        var rist = stream.GetProperty("rist");
        Assert.True(rist.GetProperty("enable").GetBoolean());
        Assert.Equal("198.51.100.2", rist.GetProperty("ip").GetString());
        Assert.Equal(expectedPort + 2, rist.GetProperty("port").GetInt32());
        var push = stream.GetProperty("push");
        Assert.True(push.GetProperty("enable").GetBoolean());
        Assert.Equal($"rtmp://push.example/{expectedSuffix}/key", push.GetProperty("path").GetString());
        Assert.Equal("flv", push.GetProperty("format").GetString());
        Assert.Equal(15, push.GetProperty("hevc_id").GetInt32());
        Assert.Equal("ext_header", push.GetProperty("flvflags").GetString());
    }

    private static JsonElement SavedChannels(LinkPiTestHandler handler)
    {
        var request = Assert.Single(handler.Requests, item => item.Path == "/link/relay.php");
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static JsonElement SavedPush(LinkPiTestHandler handler)
    {
        var request = Assert.Single(handler.Requests, item => item.RpcMethod == "push.update");
        using var rpc = JsonDocument.Parse(request.Body);
        var json = rpc.RootElement.GetProperty("params")[0].GetString();
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
