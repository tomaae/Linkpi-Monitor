using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Linkpi_Monitor;

namespace Linkpi_Monitor.Tests;

internal sealed record TestRequest(string Method, Uri Uri, string Body)
{
    public string Path => Uri.AbsolutePath;

    public string? RpcMethod
    {
        get
        {
            if (Path != "/RPC")
            {
                return null;
            }

            using var document = JsonDocument.Parse(Body);
            return document.RootElement.TryGetProperty("method", out var method)
                ? method.GetString()
                : null;
        }
    }
}

internal sealed record PreviewResponse(
    byte[] Content,
    string MediaType = "image/png",
    HttpStatusCode StatusCode = HttpStatusCode.OK,
    long? AdvertisedLength = null);

internal sealed class LinkPiTestHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<TestRequest> _requests = new();

    public string ConfigJson { get; set; } = "[]";
    public string PushJson { get; set; } = "{}";
    public string HardwareJson { get; set; } = "{}";
    public HttpStatusCode HardwareStatusCode { get; set; } = HttpStatusCode.OK;
    public string RelayJson { get; set; } = "{\"status\":\"success\",\"msg\":\"ok\"}";
    public HttpStatusCode RelayStatusCode { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode AuthenticationStatusCode { get; set; } = HttpStatusCode.OK;
    public Uri? AuthenticationResultUri { get; set; }
    public Dictionary<string, string> RpcResults { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RpcResponses { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, PreviewResponse> Previews { get; } = [];
    public Func<TestRequest, HttpResponseMessage?>? Override { get; set; }

    public IReadOnlyList<TestRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new TestRequest(
            request.Method.Method,
            request.RequestUri ?? throw new InvalidOperationException("The request URI was missing."),
            body);
        _requests.Enqueue(captured);

        var overridden = Override?.Invoke(captured);
        if (overridden is not null)
        {
            overridden.RequestMessage ??= request;
            return overridden;
        }

        var response = captured.Path switch
        {
            "/config/config.json" => Json(ConfigJson),
            "/config/push.json" => Json(PushJson),
            "/config/hardware.json" => Json(HardwareJson, HardwareStatusCode),
            "/link/action.php" => Json("{}", AuthenticationStatusCode),
            "/link/relay.php" => Json(RelayJson, RelayStatusCode),
            "/RPC" => Rpc(captured),
            _ when captured.Path.StartsWith("/snap/snap", StringComparison.Ordinal) => Preview(captured.Path),
            _ => throw new InvalidOperationException($"Unexpected request: {captured.Method} {captured.Uri}")
        };

        response.RequestMessage = captured.Path == "/link/action.php" && AuthenticationResultUri is not null
            ? new HttpRequestMessage(request.Method, AuthenticationResultUri)
            : request;
        return response;
    }

    private HttpResponseMessage Rpc(TestRequest request)
    {
        var method = request.RpcMethod ?? throw new InvalidOperationException("RPC method was missing.");
        if (RpcResponses.TryGetValue(method, out var fullResponse))
        {
            return Json(fullResponse);
        }

        var result = RpcResults.TryGetValue(method, out var configured)
            ? configured
            : method switch
            {
                "enc.getSysState" => "{}",
                "enc.getInputState" => "[]",
                "enc.getEPG" => "[]",
                "push.getState" => "{}",
                _ => "true"
            };
        return Json($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{result}}}");
    }

    private HttpResponseMessage Preview(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!int.TryParse(fileName.AsSpan("snap".Length), out var channelId) ||
            !Previews.TryGetValue(channelId, out var preview))
        {
            return Json("{}", HttpStatusCode.NotFound);
        }

        var response = new HttpResponseMessage(preview.StatusCode);
        var content = new ByteArrayContent(preview.Content);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(preview.MediaType);
        if (preview.AdvertisedLength.HasValue)
        {
            content.Headers.ContentLength = preview.AdvertisedLength.Value;
        }
        response.Content = content;
        return response;
    }

    private static HttpResponseMessage Json(
        string content,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
}

internal static class TestDevices
{
    public static DeviceSettings Default { get; } = new()
    {
        Name = "Test LinkPi",
        BaseUrl = "http://linkpi.test",
        Username = "test-user",
        Password = "test-password"
    };

    public static ChannelDisplay Channel(
        int width = 0,
        int height = 0,
        string encodedSize = "-1x-1",
        string rotate = "0",
        string left = "0",
        string top = "0",
        string right = "0",
        string bottom = "0",
        System.Windows.Media.ImageSource? preview = null) => new()
        {
            Id = 0,
            Name = "Test channel",
            SourceType = "Network decoder",
            Status = "Online",
            StatusBrush = System.Windows.Media.Brushes.Green,
            Initial = "T",
            VideoSummary = string.Empty,
            AudioSummary = string.Empty,
            OutputsSummary = string.Empty,
            PreviewMessage = string.Empty,
            PreviewImage = preview,
            IsEnabled = true,
            CanPreview = true,
            SourceWidth = width,
            SourceHeight = height,
            Configuration = new ChannelConfiguration
            {
                MainEncoder = new EncoderConfiguration { VideoSize = encodedSize },
                Decode = new DecodeConfiguration
                {
                    IsNetworkSource = true,
                    Rotate = rotate,
                    CropLeft = left,
                    CropTop = top,
                    CropRight = right,
                    CropBottom = bottom
                }
            }
        };

    public static byte[] OnePixelPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1EAAAAASUVORK5CYII=");
}
