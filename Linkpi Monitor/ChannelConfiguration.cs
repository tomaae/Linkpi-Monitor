using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Linkpi_Monitor;

public sealed record SelectionOption(string Value, string Label);

public sealed class ChannelConfiguration
{
    internal ChannelConfiguration CreateEditableCopy() =>
        System.Text.Json.JsonSerializer.Deserialize<ChannelConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(this))!;
    public GeneralChannelConfiguration General { get; init; } = new();
    public PhysicalInputConfiguration Input { get; init; } = new();
    public DecodeConfiguration Decode { get; init; } = new();
    public EncoderConfiguration MainEncoder { get; init; } = new();
    public EncoderConfiguration SubEncoder { get; init; } = new();
    public AudioEncoderConfiguration Audio { get; init; } = new();
    public StreamOutputConfiguration MainStream { get; init; } = new();
    public StreamOutputConfiguration SubStream { get; init; } = new();
    public HlsConfiguration Hls { get; init; } = new();
    public TransportStreamConfiguration Transport { get; init; } = new();
    public NdiConfiguration Ndi { get; init; } = new();
    public bool HasSubEncoder { get; init; } = true;
    public bool HasSubStream { get; init; } = true;
    public bool HasHls { get; init; } = true;
    public bool HasTransport { get; init; } = true;
    public bool HasNdi { get; init; } = true;
}

public sealed class GeneralChannelConfiguration
{
    public string Name { get; set; } = string.Empty;
}

public sealed class PhysicalInputConfiguration
{
    public bool IsHdmi { get; init; }
    public bool IsUsbCamera { get; init; }
    public bool IsPhysicalInput => IsHdmi || IsUsbCamera;
    public string Interface { get; init; } = string.Empty;
    public string Device { get; init; } = string.Empty;
    public string CaptureSize { get; set; } = string.Empty;
    public string Framerate { get; set; } = string.Empty;
    public string Rotate { get; set; } = "0";
    public string CropLeft { get; set; } = "0";
    public string CropTop { get; set; } = "0";
    public string CropRight { get; set; } = "0";
    public string CropBottom { get; set; } = "0";
    public string Contrast { get; set; } = "0";
    public bool Deinterlace { get; set; }
    public bool HasDeinterlace { get; init; }
    public bool NtscCompatible { get; set; }

    public IReadOnlyList<SelectionOption> CaptureSizes { get; init; } = [];
    public IReadOnlyList<string> Framerates { get; } = ["15", "20", "24", "25", "30", "50", "60"];
    public IReadOnlyList<string> Rotations { get; } = ["0", "90", "180", "270"];
}

public sealed class DecodeConfiguration
{
    public bool IsNetworkSource { get; init; }
    public string SourceUrl { get; set; } = string.Empty;
    public string InputFramerate { get; set; } = "-1";
    public string Protocol { get; set; } = "tcp";
    public string BufferMode { get; set; } = "0";
    public string MinimumDelay { get; set; } = "500";
    public bool DecodeVideo { get; set; }
    public bool DecodeAudio { get; set; }
    public string Rotate { get; set; } = "0";
    public string CropLeft { get; set; } = "0";
    public string CropTop { get; set; } = "0";
    public string CropRight { get; set; } = "0";
    public string CropBottom { get; set; } = "0";
    public bool Deinterlace { get; set; }
    public bool HasDeinterlace { get; init; }
    public string Contrast { get; set; } = "0";

    public IReadOnlyList<SelectionOption> BufferModes { get; } =
    [
        new("0", "Normal"),
        new("1", "No buffer"),
        new("2", "Buffered"),
        new("3", "Frame sync")
    ];

    public IReadOnlyList<SelectionOption> Protocols { get; } =
    [
        new("tcp", "TCP"),
        new("udp", "UDP")
    ];

    public IReadOnlyList<string> Framerates { get; } = ["-1", "15", "20", "24", "25", "30", "50", "60"];
    public IReadOnlyList<string> Rotations { get; } = ["0", "90", "180", "270"];
}

public abstract class ConfigurationSection : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class EncoderConfiguration : ConfigurationSection
{
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string VideoSize { get; set; } = "-1x-1";
    public string VideoFormat { get; set; } = "h264,main";
    public string RateControl { get; set; } = "cbr";
    public string Bitrate { get; set; } = string.Empty;
    public string Framerate { get; set; } = "-1";
    public string Gop { get; set; } = "1";
    public bool LowLatency { get; set; }
    public string GopMode { get; set; } = "0";
    public string MinimumQp { get; set; } = "22";
    public string MaximumQp { get; set; } = "36";
    public string FixedIQp { get; set; } = "25";
    public string FixedPQp { get; set; } = "25";
    public string TimestampMode { get; set; } = "false,linkpi";

    public IReadOnlyList<SelectionOption> VideoSizes { get; init; } = [];

    public IReadOnlyList<SelectionOption> VideoFormats { get; } =
    [
        new("h264,base", "H.264 Base"),
        new("h264,main", "H.264 Main"),
        new("h264,high", "H.264 High"),
        new("h265,main", "H.265 Main"),
        new("close,base", "Disabled")
    ];

    public IReadOnlyList<SelectionOption> RateControls { get; } =
    [
        new("cbr", "CBR"),
        new("vbr", "VBR"),
        new("avbr", "AVBR"),
        new("fixqp", "FIXQP")
    ];

    public IReadOnlyList<string> Bitrates { get; } = ["500", "1000", "2000", "4000", "6000", "8000", "12000", "20000"];
    public IReadOnlyList<string> Framerates { get; } = ["-1", "15", "20", "24", "25", "30", "50", "60"];
    public IReadOnlyList<string> Gops { get; } = ["1", "2", "3", "4", "5", "10"];

    public IReadOnlyList<SelectionOption> GopModes { get; init; } = [];

    public IReadOnlyList<SelectionOption> TimestampModes { get; } =
    [
        new("true,sinsam", "Sinsam sync"),
        new("true,linkpi", "Normal sync"),
        new("false,linkpi", "Disabled")
    ];
}

public sealed class AudioEncoderConfiguration
{
    public string Codec { get; set; } = "aac";
    public string Source { get; set; } = string.Empty;
    public string Gain { get; set; } = "0";
    public string SampleRate { get; set; } = "48000";
    public string Channels { get; set; } = "2";
    public string Bitrate { get; set; } = "128";
    public IReadOnlyList<SelectionOption> Sources { get; init; } = [];

    public IReadOnlyList<SelectionOption> Codecs { get; } =
    [
        new("aac", "AAC"),
        new("aache", "AAC-HE"),
        new("pcma", "PCMA"),
        new("mp2", "MPEG-2"),
        new("mp3", "MP3"),
        new("opus", "OPUS"),
        new("close", "Disabled")
    ];

    public IReadOnlyList<SelectionOption> Gains { get; } =
    [
        new("24", "+24 dB"), new("18", "+18 dB"), new("12", "+12 dB"),
        new("6", "+6 dB"), new("0", "0 dB"), new("-6", "-6 dB"),
        new("-12", "-12 dB"), new("-18", "-18 dB"), new("-24", "-24 dB")
    ];

    public IReadOnlyList<SelectionOption> SampleRates { get; } =
    [
        new("-1", "Automatic"),
        new("16000", "16 kHz"),
        new("32000", "32 kHz"),
        new("44100", "44.1 kHz"),
        new("48000", "48 kHz")
    ];

    public IReadOnlyList<SelectionOption> ChannelModes { get; } =
    [
        new("1", "Mono"),
        new("2", "Stereo")
    ];

    public IReadOnlyList<string> Bitrates { get; } = ["32", "48", "64", "96", "128", "192", "256", "320"];
}

public sealed class StreamOutputConfiguration
{
    public bool Http { get; set; }
    public bool Hls { get; set; }
    public bool Rtmp { get; set; }
    public bool WebRtc { get; set; }
    public string Suffix { get; set; } = string.Empty;
    public RtspConfiguration Rtsp { get; init; } = new();
    public SrtConfiguration Srt { get; init; } = new();
    public UdpConfiguration Udp { get; init; } = new();
    public RistConfiguration Rist { get; init; } = new();
    public PushStreamConfiguration Push { get; init; } = new();
}

public sealed class RtspConfiguration
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Authentication { get; set; }
    public bool Onvif { get; set; }
    public bool HasOnvif { get; init; }
}

public sealed class SrtConfiguration
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "listener";
    public string IpAddress { get; set; } = string.Empty;
    public string StreamId { get; set; } = string.Empty;
    public bool HasStreamId { get; init; }
    public string Port { get; set; } = string.Empty;
    public string Latency { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public IReadOnlyList<string> Modes { get; } = ["caller", "listener", "rendezvous"];
}

public sealed class UdpConfiguration
{
    public bool Enabled { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public string Ttl { get; set; } = "5";
    public bool FlowControl { get; set; }
    public string Bandwidth { get; set; } = "100";
    public bool RtpHeader { get; set; }
}

public sealed class RistConfiguration
{
    public bool Enabled { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
}

public sealed class PushStreamConfiguration
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Format { get; set; } = "auto";
    public string HevcId { get; set; } = "12";
    public string Compatibility { get; set; } = string.Empty;

    public IReadOnlyList<string> Formats { get; } = ["auto", "flv", "rtsp", "rtp", "mpegts", "rtp_mpegts"];

    public IReadOnlyList<SelectionOption> CompatibilityModes { get; } =
    [
        new(string.Empty, "Normal"),
        new("ext_header", "Enhanced RTMP")
    ];
}

public sealed class HlsConfiguration
{
    public string SegmentLength { get; set; } = string.Empty;
    public string ListLength { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
}

public sealed class TransportStreamConfiguration
{
    public string PacketSize { get; set; } = "1316";
    public string Pid { get; set; } = string.Empty;
    public string PmtPid { get; set; } = string.Empty;
    public string ServiceId { get; set; } = string.Empty;
    public string StreamId { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;

    public IReadOnlyList<string> PacketSizes { get; } =
        ["188", "376", "564", "752", "940", "1128", "1316", "1504", "1692", "1880"];
}

public sealed class NdiConfiguration
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
}
