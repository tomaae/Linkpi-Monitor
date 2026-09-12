using System.Net;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class LinkPiClientSnapshotTests
{
    [Fact]
    public async Task ParsesChannelsStatusesCapabilitiesAndAllConfigurationSections()
    {
        const string channels = """
            [
              {
                "id":"2","type":"net","name":"Camera A","enable":"1","enable2":true,
                "net":{"path":"srt://source.test:9000","framerate":"25","protocol":"udp","bufferMode":"2","minDelay":"650","decodeV":true,"decodeA":"true"},
                "cap":{"rotate":"90","contrast":3,"deinterlace":true,"crop":{"L":"4","T":6,"R":"8","B":10}},
                "encv":{"width":3840,"height":2160,"codec":"h265","profile":"main","rcmode":"vbr","bitrate":"8000","framerate":25,"gop":"2","lowLatency":true,"gopmode":"3","minqp":18,"maxqp":"42","Iqp":24,"Pqp":"26","syncTS":true,"syncTSMode":"sinsam"},
                "encv2":{"width":640,"height":360,"codec":"h264","profile":"base","bitrate":800,"framerate":15,"gop":1},
                "enca":{"codec":"aac","audioSrc":"line","gain":"6","samplerate":48000,"channels":"2","bitrate":128},
                "stream":{"http":true,"hls":"1","rtmp":0,"webrtc":"false","suffix":"camera-a",
                  "rtsp":{"enable":true,"name":"viewer","passwd":"secret","auth":true,"onvif":true},
                  "srt":{"enable":true,"mode":"caller","ip":"203.0.113.10","streamid":"feed","port":"9001","latency":120,"passwd":"srt-secret"},
                  "udp":{"enable":"1","ip":"239.0.0.1","port":5000,"ttl":"8","flowCtrl":true,"bandwidth":"120","rtp":true},
                  "rist":{"enable":true,"ip":"198.51.100.8","port":"9100"},
                  "push":{"enable":true,"path":"rtmp://push.test/live/key","format":"flv","hevc_id":"14","flvflags":"ext_header"}},
                "stream2":{"rtsp":true},
                "hls":{"hls_time":"4","hls_list_size":6,"hls_base_url":"/segments/","hls_filename":"segment-%d.ts"},
                "ts":{"tsSize":"1316","mpegts_start_pid":256,"mpegts_pmt_start_pid":"4096","mpegts_service_id":1,"mpegts_transport_stream_id":"2","mpegts_original_network_id":3},
                "ndi":{"enable":true,"name":"Studio NDI","group":"Public"}
              },
              {"id":0,"type":"vi","name":"HDMI","enable":true,"encv":{"codec":"h264","width":1920,"height":1080,"framerate":30,"bitrate":6000},"enca":{"codec":"close"}},
              {"id":1,"type":"usb","name":"USB camera","enable":true,"capture":{"width":640,"height":480,"framerate":30},"encv":{},"enca":{}},
              {"id":8,"type":"mix","name":"Program","enable":false,"encv":{},"enca":{}},
              {"id":9,"type":"file","name":"Internal file","enable":true},
              {"id":10,"type":"fine","name":"Internal fine","enable":true},
              {"id":11,"type":"colorKey","name":"Internal key","enable":true},
              {"id":12,"type":"image","name":"Internal image","enable":true}
            ]
            """;
        var handler = SnapshotHandler(channels);
        handler.HardwareJson = """
            {"model":"ENC1Pro","chip":"SS524V100","capability":{"encode":{"maxSize":"4K30","BFrame":true}}}
            """;
        handler.RpcResults["enc.getSysState"] = "{\"cpu\":\"44\",\"mem\":53,\"temperature\":\"52\"}";
        handler.RpcResults["enc.getInputState"] = """
            [{"chnId":2,"avalible":"1","width":"1920","height":1080},{"chnId":0,"avalible":false},{"chnId":1,"avalible":true}]
            """;
        handler.RpcResponses["enc.snap"] = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-1}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(44, snapshot.CpuPercent);
        Assert.Equal(53, snapshot.MemoryPercent);
        Assert.Equal(52, snapshot.TemperatureCelsius);
        Assert.Equal([2, 0, 1, 8], snapshot.Channels.Select(channel => channel.Id));
        Assert.DoesNotContain(snapshot.Channels, channel =>
            channel.SourceType is "File source" or "Color key" or "Image source");

        var network = snapshot.Channels[0];
        Assert.Equal("Online", network.Status);
        Assert.Equal("Network decoder", network.SourceType);
        Assert.Equal("H265  ·  3840×2160  ·  25 fps  ·  8000 kbps", network.VideoSummary);
        Assert.Equal("AAC  ·  128 kbps  ·  48 kHz", network.AudioSummary);
        Assert.Equal("Main: HTTP  ·  HLS  ·  RTSP  ·  SRT  ·  UDP  ·  RIST  ·  Sub: RTSP", network.OutputsSummary);
        Assert.Equal("Snapshot unavailable", network.PreviewMessage);
        Assert.True(network.Configuration.Decode.IsNetworkSource);
        Assert.Equal("srt://source.test:9000", network.Configuration.Decode.SourceUrl);
        Assert.Equal("udp", network.Configuration.Decode.Protocol);
        Assert.Equal("650", network.Configuration.Decode.MinimumDelay);
        Assert.True(network.Configuration.Decode.DecodeVideo);
        Assert.True(network.Configuration.Decode.DecodeAudio);
        Assert.True(network.Configuration.Decode.HasDeinterlace);
        Assert.Equal("90", network.Configuration.Decode.Rotate);

        var main = network.Configuration.MainEncoder;
        Assert.True(main.Enabled);
        Assert.Equal("3840x2160", main.VideoSize);
        Assert.Contains(main.VideoSizes, option => option.Value == "3840x2160" && option.Label.StartsWith("4K"));
        Assert.Equal("h265,main", main.VideoFormat);
        Assert.Equal("vbr", main.RateControl);
        Assert.True(main.LowLatency);
        Assert.Equal("3", main.GopMode);
        Assert.Contains(main.GopModes, option => option.Value == "3" && option.Label == "BiPredB");
        Assert.Equal("18", main.MinimumQp);
        Assert.Equal("42", main.MaximumQp);
        Assert.Equal("24", main.FixedIQp);
        Assert.Equal("26", main.FixedPQp);
        Assert.Equal("true,sinsam", main.TimestampMode);
        Assert.True(network.Configuration.SubEncoder.Enabled);

        var audio = network.Configuration.Audio;
        Assert.Equal("aac", audio.Codec);
        Assert.Equal("line", audio.Source);
        Assert.Equal("6", audio.Gain);
        Assert.Equal("48000", audio.SampleRate);
        Assert.Equal("2", audio.Channels);
        Assert.Equal("128", audio.Bitrate);
        Assert.Contains(audio.Sources, option => option.Value == "line");
        Assert.Contains(audio.Sources, option => option.Value == "2" && option.Label == "Camera A");

        var stream = network.Configuration.MainStream;
        Assert.True(stream.Http);
        Assert.True(stream.Hls);
        Assert.False(stream.Rtmp);
        Assert.False(stream.WebRtc);
        Assert.Equal("camera-a", stream.Suffix);
        Assert.True(stream.Rtsp.Enabled);
        Assert.Equal("viewer", stream.Rtsp.Username);
        Assert.Equal("secret", stream.Rtsp.Password);
        Assert.True(stream.Rtsp.Authentication);
        Assert.True(stream.Rtsp.Onvif);
        Assert.True(stream.Rtsp.HasOnvif);
        Assert.True(stream.Srt.Enabled);
        Assert.Equal("feed", stream.Srt.StreamId);
        Assert.True(stream.Srt.HasStreamId);
        Assert.True(stream.Udp.Enabled);
        Assert.True(stream.Udp.FlowControl);
        Assert.True(stream.Udp.RtpHeader);
        Assert.True(stream.Rist.Enabled);
        Assert.True(stream.Push.Enabled);
        Assert.Equal("flv", stream.Push.Format);
        Assert.Equal("14", stream.Push.HevcId);
        Assert.True(network.Configuration.SubStream.Rtsp.Enabled);

        Assert.Equal("4", network.Configuration.Hls.SegmentLength);
        Assert.Equal("6", network.Configuration.Hls.ListLength);
        Assert.Equal("/segments/", network.Configuration.Hls.BaseUrl);
        Assert.Equal("segment-%d.ts", network.Configuration.Hls.Filename);
        Assert.Equal("1316", network.Configuration.Transport.PacketSize);
        Assert.Equal("256", network.Configuration.Transport.Pid);
        Assert.Equal("4096", network.Configuration.Transport.PmtPid);
        Assert.Equal("1", network.Configuration.Transport.ServiceId);
        Assert.Equal("2", network.Configuration.Transport.StreamId);
        Assert.Equal("3", network.Configuration.Transport.NetworkId);
        Assert.True(network.Configuration.Ndi.Enabled);
        Assert.Equal("Studio NDI", network.Configuration.Ndi.Name);
        Assert.Equal("Public", network.Configuration.Ndi.Group);

        Assert.Equal("No signal", snapshot.Channels[1].Status);
        Assert.Equal("Online", snapshot.Channels[2].Status);
        Assert.True(snapshot.Channels[2].Configuration.Input.IsUsbCamera);
        Assert.Equal("640x480", snapshot.Channels[2].Configuration.Input.CaptureSize);
        Assert.Equal("Disabled", snapshot.Channels[3].Status);
        Assert.Equal("Stream is disabled", snapshot.Channels[3].PreviewMessage);
    }

    [Fact]
    public async Task ParsesPushStateAndHidesDestinationSecrets()
    {
        var handler = SnapshotHandler("""
            [{"id":0,"type":"vi","name":"HDMI","enable":false},{"id":2,"type":"net","name":"Camera A","enable":false}]
            """);
        handler.HardwareJson = "{\"chip\":\"SS524V100\"}";
        handler.PushJson = """
            {"autorun":"true","url":[
              {"des":"Primary","enable":true,"type":"normal","srcV":"0","srcA":"close","stream":"main","path":"rtmp://video.example:1936/live/secret-key","flvflags":""},
              {"des":"Backup","enable":true,"type":"custom","srcV":"99","srcA":"42","stream":"sub","path":"not-a-url","flvflags":"custom-flag"},
              {"des":"Off","enable":false,"type":"webrtc","srcV":"2","path":"","stream":"main"}
            ]}
            """;
        handler.RpcResults["push.getState"] = """
            {"pushing":"1","status":[{"speed":"6100","duration":"90061"},{"speed":0,"duration":3000000},{"speed":9000,"duration":0}]}
            """;
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.IsPushing);
        Assert.True(snapshot.PushConfiguration.Autorun);
        Assert.True(snapshot.PushConfiguration.AutorunStoredAsString);
        Assert.Contains(snapshot.PushConfiguration.Types, option => option.Value == "trtc");
        Assert.Collection(snapshot.PushDestinations,
            primary =>
            {
                Assert.Equal("Streaming", primary.Status);
                Assert.Equal("6.1 Mbps", primary.Speed);
                Assert.Equal("1d 01:01:01", primary.Duration);
                Assert.Equal("rtmp://video.example:1936", primary.Destination);
                Assert.DoesNotContain("secret-key", primary.Destination);
                Assert.Equal("HDMI", primary.Source);
            },
            backup =>
            {
                Assert.Equal("Waiting", backup.Status);
                Assert.Equal("—", backup.Speed);
                Assert.Equal("—", backup.Duration);
                Assert.Equal("Configured destination", backup.Destination);
                Assert.Equal("Channel 99", backup.Source);
                Assert.Contains(backup.Configuration.Types, option => option.Value == "custom");
                Assert.Contains(backup.Configuration.AudioSources, option => option.Value == "42");
            },
            disabled =>
            {
                Assert.Equal("Disabled", disabled.Status);
                Assert.Equal("9.0 Mbps", disabled.Speed);
                Assert.Equal("Not configured", disabled.Destination);
                Assert.Equal("Camera A", disabled.Source);
            });
    }

    [Fact]
    public async Task ParsesCapabilityDrivenAudioAndVideoHardware()
    {
        var handler = SnapshotHandler("""
            [
              {"id":0,"type":"vi","name":"HDMI","enable":false},
              {"id":13,"type":"mix","name":"Mix","enable":false,
               "inputUsbAlsa":{"name":"USB Mic","usbid":"USB-123","anr":"1","anr_level":6,"gain":"12","enable":true},
               "inputLine":{"name":"Jack input","anr":0,"anr_level":"8","gain":-6},
               "outputLine":{"src":"77","gain":"18"},
               "output":{"enable":true,"type":"dvi","output":"CUSTOM","rotate":180,"mirror":true,"src":"77","lowLatency":true,
                 "csc":{"matrix":"709_601","luma":"51","contrast":"52","saturation":"53","hue":"54"}},
               "output2":{"ui":true,"enable":false,"type":"hdmi","output":"4K30","rotate":"0","src":0,"lowLatency":false,"csc":{}}
              }
            ]
            """);
        handler.HardwareJson = """
            {"model":"ENC1Pro","chip":"SS524V100","function":{"line":true,"videoOut":"1"},"capability":{"maxOutput":"4K30"}}
            """;
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var hardware = (await client.GetSnapshotAsync(CancellationToken.None)).Hardware;

        Assert.Equal("ENC1Pro", hardware.Model);
        Assert.Equal("SS524V100", hardware.Chip);
        Assert.True(hardware.HasLineAudio);
        Assert.True(hardware.HasUsbAudioInput);
        Assert.True(hardware.HasVideoOutput);
        Assert.Equal("USB Mic", hardware.UsbAudioInput.Name);
        Assert.Equal("USB-123", hardware.UsbAudioInput.Device);
        Assert.True(hardware.UsbAudioInput.Enabled);
        Assert.True(hardware.UsbAudioInput.CanDisable);
        Assert.Equal("Jack input", hardware.LineAudioInput.Name);
        Assert.Equal("Analog audio jack", hardware.LineAudioInput.Device);
        Assert.False(hardware.LineAudioInput.CanDisable);
        Assert.Equal("77", hardware.LineAudioOutput.Source);
        Assert.True(hardware.LineAudioOutput.SourceStoredAsString);
        Assert.Contains(hardware.LineAudioOutput.Sources, option => option.Value == "77");

        Assert.Collection(hardware.VideoOutputs,
            primary =>
            {
                Assert.Equal("output", primary.ConfigurationKey);
                Assert.Equal("HDMI output", primary.Name);
                Assert.True(primary.Enabled);
                Assert.Equal("dvi", primary.Type);
                Assert.Equal("CUSTOM", primary.Resolution);
                Assert.Contains("CUSTOM", primary.Resolutions);
                Assert.Contains("4K30", primary.Resolutions);
                Assert.Equal("180", primary.Rotate);
                Assert.True(primary.Mirror);
                Assert.True(primary.HasMirror);
                Assert.Equal("77", primary.Source);
                Assert.Contains(primary.Sources, option => option.Value == "77");
                Assert.True(primary.LowLatency);
                Assert.Equal("709_601", primary.ColorMatrix);
                Assert.True(primary.ColorValuesStoredAsString);
            },
            secondary =>
            {
                Assert.Equal("output2", secondary.ConfigurationKey);
                Assert.Equal("Secondary output", secondary.Name);
                Assert.False(secondary.Enabled);
                Assert.False(secondary.HasMirror);
            });
    }

    [Fact]
    public async Task UsesFactoryModelWhenHardwareModelIsMissingAndHidesUnavailableOutputs()
    {
        var handler = SnapshotHandler("""
            [{"id":8,"type":"mix","name":"Mix","enable":false,"output":{"enable":true},"output2":{"ui":false,"enable":true}}]
            """);
        handler.HardwareJson = "{\"fac\":\"ENC1V3\",\"function\":{\"line\":false,\"videoOut\":false}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var hardware = (await client.GetSnapshotAsync(CancellationToken.None)).Hardware;

        Assert.Equal("ENC1V3", hardware.Model);
        Assert.False(hardware.HasHardwareControls);
        Assert.Empty(hardware.VideoOutputs);
        Assert.Equal("USB microphone", hardware.UsbAudioInput.Name);
        Assert.Equal("Not connected", hardware.UsbAudioInput.Device);
    }

    [Fact]
    public async Task RewritesDeviceRelativeWatchUrisAndLeavesAbsoluteUriUntouched()
    {
        var handler = SnapshotHandler("""
            [
              {"id":0,"type":"net","name":"RTSP","enable":true,"net":{"decodeV":false}},
              {"id":1,"type":"net","name":"HTTP","enable":true,"net":{"decodeV":false}},
              {"id":2,"type":"net","name":"Absolute","enable":true,"net":{"decodeV":false}}
            ]
            """);
        handler.RpcResults["enc.getEPG"] = """
            [
              {"id":0,"url":"http:///fallback|rtsp:///live/main"},
              {"id":1,"url":"http:///play/channel"},
              {"id":2,"url":"rtsp://media.example/live"}
            ]
            """;
        var device = TestDevices.Default with { BaseUrl = "http://encoder.test:8080" };
        using var client = new LinkPiClient(device, handler);

        var channels = (await client.GetSnapshotAsync(CancellationToken.None)).Channels;

        Assert.Equal("rtsp://encoder.test/live/main", channels[0].WatchUri?.AbsoluteUri);
        Assert.Equal("http://encoder.test:8080/play/channel", channels[1].WatchUri?.AbsoluteUri);
        Assert.Equal("rtsp://media.example/live", channels[2].WatchUri?.AbsoluteUri);
    }

    [Fact]
    public async Task DisabledChannelNeverGetsWatchUri()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"name\":\"HDMI\",\"enable\":false}]");
        handler.RpcResults["enc.getEPG"] = "[{\"id\":0,\"url\":\"rtsp:///live/main\"}]";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channel = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);

        Assert.Null(channel.WatchUri);
        Assert.False(channel.CanWatch);
    }

    [Fact]
    public async Task LoadsValidPreviewAndIsolatesInvalidPreview()
    {
        var handler = SnapshotHandler("""
            [
              {"id":0,"type":"vi","name":"Good","enable":true,"encv":{},"enca":{}},
              {"id":1,"type":"usb","name":"Bad","enable":true,"encv":{},"enca":{}}
            ]
            """);
        handler.Previews[0] = new PreviewResponse(TestDevices.OnePixelPng);
        handler.Previews[1] = new PreviewResponse(TestDevices.OnePixelPng, "application/json");
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channels = (await client.GetSnapshotAsync(CancellationToken.None)).Channels;

        Assert.True(channels[0].HasPreview);
        Assert.Empty(channels[0].PreviewMessage);
        Assert.False(channels[1].HasPreview);
        Assert.Equal("Snapshot unavailable", channels[1].PreviewMessage);
        Assert.Single(handler.Requests, request => request.RpcMethod == "enc.snap");
        Assert.Equal(2, handler.Requests.Count(request => request.Path.StartsWith("/snap/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RejectsPreviewWithAdvertisedOversizedContent()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"name\":\"HDMI\",\"enable\":true}]");
        handler.Previews[0] = new PreviewResponse([], AdvertisedLength: (10 * 1024 * 1024) + 1);
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channel = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);

        Assert.False(channel.HasPreview);
        Assert.Equal("Snapshot unavailable", channel.PreviewMessage);
    }

    [Fact]
    public async Task SnapshotRpcFailureMarksEveryPreviewUnavailableWithoutImageRequests()
    {
        var handler = SnapshotHandler("""
            [{"id":0,"type":"vi","name":"HDMI","enable":true},{"id":1,"type":"usb","name":"USB","enable":true}]
            """);
        handler.RpcResponses["enc.snap"] = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"unavailable\"}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channels = (await client.GetSnapshotAsync(CancellationToken.None)).Channels;

        Assert.All(channels, channel => Assert.Equal("Snapshot unavailable", channel.PreviewMessage));
        Assert.DoesNotContain(handler.Requests, request => request.Path.StartsWith("/snap/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotRequestSnapshotWhenNoChannelCanBePreviewed()
    {
        var handler = SnapshotHandler("""
            [
              {"id":0,"type":"vi","name":"Disabled","enable":false},
              {"id":1,"type":"ndi","name":"NDI","enable":true},
              {"id":2,"type":"net","name":"Audio only","enable":true,"net":{"decodeV":false}}
            ]
            """);
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channels = (await client.GetSnapshotAsync(CancellationToken.None)).Channels;

        Assert.Equal("Stream is disabled", channels[0].PreviewMessage);
        Assert.Equal("Preview unavailable", channels[1].PreviewMessage);
        Assert.Equal("Preview unavailable", channels[2].PreviewMessage);
        Assert.DoesNotContain(handler.Requests, request => request.RpcMethod == "enc.snap");
    }

    [Fact]
    public async Task MissingOptionalHardwareDocumentDoesNotFailSnapshot()
    {
        var handler = SnapshotHandler("[]");
        handler.HardwareStatusCode = HttpStatusCode.NotFound;
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.Hardware.HasHardwareControls);
        Assert.Empty(snapshot.Hardware.Model);
    }

    [Fact]
    public async Task RpcErrorIsReportedAsADegradedSubsystem()
    {
        var handler = SnapshotHandler("[]");
        handler.RpcResponses["enc.getSysState"] = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-1,\"message\":\"bad\"}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.False(snapshot.HasSystemMetrics);
        var warning = Assert.Single(snapshot.Warnings, value => value.StartsWith("System metrics", StringComparison.Ordinal));
        Assert.Contains("enc.getSysState", warning);
        Assert.Contains("bad", warning);
    }

    [Fact]
    public async Task PushFailureDoesNotDiscardChannelsOrSystemMetrics()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"name\":\"HDMI\",\"enable\":true}]");
        handler.RpcResults["enc.getSysState"] = "{\"cpu\":21,\"mem\":34,\"temperature\":45}";
        handler.RpcResponses["push.getState"] = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"message\":\"offline\"}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None, includePreviews: false);

        Assert.Single(snapshot.Channels);
        Assert.True(snapshot.HasSystemMetrics);
        Assert.Equal(21, snapshot.CpuPercent);
        Assert.Contains(snapshot.Warnings, warning => warning.StartsWith("Push state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonArrayConfigurationReturnsNoChannels()
    {
        var handler = SnapshotHandler("{\"unexpected\":true}");
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.Channels);
    }

    private static LinkPiTestHandler SnapshotHandler(string channels) => new()
    {
        ConfigJson = channels,
        PushJson = "{\"autorun\":false,\"url\":[]}",
        HardwareJson = "{}"
    };
}
