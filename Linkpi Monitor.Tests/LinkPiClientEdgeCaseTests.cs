using System.Net;
using System.Text;
using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class LinkPiClientEdgeCaseTests
{
    [Fact]
    public async Task ChunkedPreviewStopsReadingAtSizeLimit()
    {
        var stream = new OversizedPreviewStream();
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"enable\":true}]");
        handler.Override = request => request.Path.StartsWith("/snap/")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream) { Headers = { ContentType = new("image/png") } }
            } : null;
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("Snapshot unavailable", Assert.Single(snapshot.Channels).PreviewMessage);
        Assert.Equal(LinkPiClient.MaximumPreviewBytes + 1, stream.BytesRead);
    }

    private sealed class OversizedPreviewStream : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Clear();
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StalledPreviewTimesOutWithoutBlockingOtherChannelsOrNextRefresh()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"enable\":true},{\"id\":1,\"type\":\"vi\",\"enable\":true}]");
        handler.Override = request => request.Path == "/snap/snap0.jpg"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellationAwareStream())
                { Headers = { ContentType = new("image/png") } }
            }
            : null;
        using var client = new LinkPiClient(TestDevices.Default, handler, TimeSpan.FromMilliseconds(100));
        var snapshot = await client.GetSnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Channels.Count);
        Assert.Equal("Snapshot unavailable", snapshot.Channels[0].PreviewMessage);
        Assert.Contains(handler.Requests, request => request.Path == "/snap/snap1.jpg");
        handler.Override = null;
        await client.GetSnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SnapshotRpcTimeoutDoesNotDiscardMonitoringData()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"enable\":true}]");
        handler.Override = request => request.RpcMethod == "enc.snap"
            ? throw new TaskCanceledException("Timed out", new TimeoutException()) : null;
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("Snapshot unavailable", Assert.Single(snapshot.Channels).PreviewMessage);
    }

    [Fact]
    public void PublicConstructorCreatesClientWithoutSendingARequest()
    {
        using var client = new LinkPiClient(new DeviceSettings
        {
            Name = "Constructor test",
            BaseUrl = "http://localhost/",
            Username = "user",
            Password = "password"
        });
    }

    [Fact]
    public async Task ConcurrentSavesShareOneAuthenticationRequest()
    {
        var authenticationEntered = new ManualResetEventSlim();
        var releaseAuthentication = new ManualResetEventSlim();
        var handler = new LinkPiTestHandler
        {
            PushJson = "{\"autorun\":false,\"url\":[]}",
            Override = request =>
            {
                if (request.Path != "/link/action.php")
                {
                    return null;
                }

                authenticationEntered.Set();
                releaseAuthentication.Wait(TimeSpan.FromSeconds(5));
                return JsonResponse("{}");
            }
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        var configuration = new PushConfiguration();
        var saves = new List<Task>();
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            saves.Add(Task.Run(() => client.SavePushConfigurationAsync(configuration, cancellationToken), cancellationToken));
            Assert.True(authenticationEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            saves.Add(Task.Run(() => client.SavePushConfigurationAsync(configuration, cancellationToken), cancellationToken));
            await Task.Delay(50, cancellationToken);
        }
        finally
        {
            releaseAuthentication.Set();
        }

        await Task.WhenAll(saves);
        Assert.Single(handler.Requests, request => request.Path == "/link/action.php");
    }

    [Fact]
    public async Task ConfigurationRelayRedirectToAnotherHostIsRejected()
    {
        var handler = new LinkPiTestHandler
        {
            ConfigJson = "[{\"id\":0,\"type\":\"vi\"}]",
            Override = request => request.Path == "/link/relay.php"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Post, "http://unexpected.test/link/relay.php"),
                    Content = new StringContent("{\"status\":\"success\"}", Encoding.UTF8, "application/json")
                }
                : null
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SaveChannelConfigurationAsync(0, new ChannelConfiguration(), TestContext.Current.CancellationToken));

        Assert.Contains("unexpected host", exception.Message);
    }

    [Fact]
    public async Task ExpiredSaveSessionAuthenticatesAgainAndRetriesOnce()
    {
        var relayAttempts = 0;
        var handler = new LinkPiTestHandler
        {
            ConfigJson = "[{\"id\":0}]",
            Override = request => request.Path == "/link/relay.php" && Interlocked.Increment(ref relayAttempts) == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : null
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);

        await client.SaveChannelConfigurationAsync(0, new ChannelConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(2, relayAttempts);
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/link/action.php"));
    }

    [Fact]
    public async Task CancellationDuringPreviewCycleIsPropagated()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"enable\":true}]");
        using var client = new LinkPiClient(TestDevices.Default, handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetSnapshotAsync(cancellation.Token));

        Assert.DoesNotContain(handler.Requests, request => request.Path.StartsWith("/snap/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyPreviewBodyIsReportedAsUnavailable()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"enable\":true}]");
        handler.Previews[0] = new PreviewResponse([]);
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channel = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).Channels);

        Assert.False(channel.HasPreview);
        Assert.Equal("Snapshot unavailable", channel.PreviewMessage);
    }

    [Fact]
    public async Task CancellationWhileReadingPreviewIsPropagated()
    {
        var previewStream = new CancellationAwareStream();
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"enable\":true}]");
        handler.Override = request =>
        {
            if (!request.Path.StartsWith("/snap/", StringComparison.Ordinal))
            {
                return null;
            }

            var content = new StreamContent(previewStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        using var client = new LinkPiClient(TestDevices.Default, handler);
        using var cancellation = new CancellationTokenSource();

        var snapshotTask = client.GetSnapshotAsync(cancellation.Token);
        await previewStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshotTask);
    }

    [Fact]
    public async Task MissingRuntimeCollectionsUseEnabledStatusAndParseScalarOutputs()
    {
        var handler = SnapshotHandler("""
            [{
              "type":"vi","name":" ","enable":true,
              "encv":{"codec":"close"},
              "enca":{},
              "stream":{"http":1,"hls":"true","rtmp":"1","rtsp":{"enable":true},"srt":false}
            }]
            """);
        handler.PushJson = "{\"autorun\":true}";
        handler.RpcResults["enc.getInputState"] = "{}";
        handler.RpcResults["enc.getEPG"] = "{}";
        handler.RpcResponses["enc.snap"] = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-1}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);
        var channel = Assert.Single(snapshot.Channels);

        Assert.Equal(0, channel.Id);
        Assert.Equal("0", channel.Initial);
        Assert.Equal("Status unknown", channel.Status);
        Assert.Equal("Video disabled", channel.VideoSummary);
        Assert.Equal("HTTP  ·  HLS  ·  RTMP  ·  RTSP", channel.OutputsSummary);
        Assert.True(snapshot.PushConfiguration.Autorun);
        Assert.Empty(snapshot.PushConfiguration.Destinations);
    }

    [Fact]
    public async Task PushUsesDefaultPortAndFormatsSubDayDuration()
    {
        var handler = SnapshotHandler("[{\"id\":0,\"type\":\"vi\",\"name\":\"HDMI\",\"enable\":false}]");
        handler.PushJson = """
            {"autorun":false,"url":[{"des":"Primary","enable":true,"srcV":"0","path":"rtmp://video.example/live/key"}]}
            """;
        handler.RpcResults["push.getState"] = "{\"pushing\":true,\"status\":[{\"speed\":1000,\"duration\":\"3661\"}]}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var push = Assert.Single((await client.GetSnapshotAsync(CancellationToken.None)).PushDestinations);

        Assert.Equal("rtmp://video.example", push.Destination);
        Assert.Equal("01:01:01", push.Duration);
    }

    [Fact]
    public async Task UnsupportedAndInvalidWatchUrlsAreIgnored()
    {
        var handler = SnapshotHandler("""
            [
              {"id":0,"type":"net","name":"FTP","enable":true,"net":{"decodeV":false}},
              {"id":1,"type":"net","name":"Invalid","enable":true,"net":{"decodeV":false}}
            ]
            """);
        handler.RpcResults["enc.getEPG"] = """
            [{"id":0,"url":"ftp://media.example/file|udp://239.1.1.1"},{"id":1,"url":"http://[invalid"}]
            """;
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var channels = (await client.GetSnapshotAsync(CancellationToken.None)).Channels;

        Assert.All(channels, channel => Assert.False(channel.CanWatch));
    }

    [Fact]
    public async Task HiddenSecondaryVideoOutputIsNotExposed()
    {
        var handler = SnapshotHandler("""
            [{"id":8,"type":"mix","enable":false,"output":{},"output2":{"ui":false,"enable":true}}]
            """);
        handler.HardwareJson = "{\"function\":{\"videoOut\":true}}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var hardware = (await client.GetSnapshotAsync(CancellationToken.None)).Hardware;

        var output = Assert.Single(hardware.VideoOutputs);
        Assert.Equal("output", output.ConfigurationKey);
        Assert.True(hardware.HasVideoOutput);
    }

    [Fact]
    public async Task RpcResponseWithoutResultUsesDefaultSystemValues()
    {
        var handler = SnapshotHandler("[]");
        handler.RpcResponses["enc.getSysState"] = "{\"jsonrpc\":\"2.0\",\"id\":1}";
        using var client = new LinkPiClient(TestDevices.Default, handler);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.CpuPercent);
        Assert.Equal(0, snapshot.MemoryPercent);
        Assert.Equal(0, snapshot.TemperatureCelsius);
    }

    [Fact]
    public async Task RequiredConfigurationHttpFailurePropagates()
    {
        var handler = SnapshotHandler("[]");
        handler.Override = request => request.Path == "/config/config.json"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : null;
        using var client = new LinkPiClient(TestDevices.Default, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetSnapshotAsync(CancellationToken.None));
    }

    private static LinkPiTestHandler SnapshotHandler(string channels) => new()
    {
        ConfigJson = channels,
        PushJson = "{\"autorun\":false,\"url\":[]}",
        HardwareJson = "{}"
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CancellationAwareStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
