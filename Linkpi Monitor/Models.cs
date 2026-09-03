using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Windows;

namespace Linkpi_Monitor;

public sealed class LinkPiSnapshot
{
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public int TemperatureCelsius { get; init; }
    public required IReadOnlyList<ChannelDisplay> Channels { get; init; }
    public required IReadOnlyList<PushDisplay> PushDestinations { get; init; }
    public required PushConfiguration PushConfiguration { get; init; }
    public required HardwareConfiguration Hardware { get; init; }
    public bool IsPushing { get; init; }
}

public sealed class ChannelDisplay
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string SourceType { get; init; }
    public required string Status { get; init; }
    public required Brush StatusBrush { get; init; }
    public required string Initial { get; init; }
    public required string VideoSummary { get; init; }
    public required string AudioSummary { get; init; }
    public required string OutputsSummary { get; init; }
    public required string PreviewMessage { get; set; }
    public ImageSource? PreviewImage { get; set; }
    public bool HasPreview => PreviewImage is not null;
    public Uri? WatchUri { get; init; }
    public bool CanWatch => WatchUri is not null;
    public bool IsEnabled { get; init; }
    public bool CanPreview { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public required ChannelConfiguration Configuration { get; init; }
    public double PreviewRenderWidth => VideoGeometry.FromChannel(this).FitWithin(365, 170).Width;
    public double PreviewRenderHeight => VideoGeometry.FromChannel(this).FitWithin(365, 170).Height;
}

public sealed class PushDisplay
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required string Status { get; init; }
    public required string Speed { get; init; }
    public required string Duration { get; init; }
    public required Brush StatusBrush { get; init; }
    public required PushDestinationConfiguration Configuration { get; init; }
}

public sealed class PushConfiguration : ConfigurationSection
{
    private bool _autorun;

    public bool Autorun
    {
        get => _autorun;
        set => SetField(ref _autorun, value);
    }

    public ObservableCollection<PushDestinationConfiguration> Destinations { get; init; } = [];
    public bool AutorunStoredAsString { get; init; }
    public IReadOnlyList<SelectionOption> VideoSources { get; init; } = [];
    public IReadOnlyList<SelectionOption> AudioSources { get; init; } = [];
    public IReadOnlyList<SelectionOption> Types { get; init; } = [];
}

public sealed class PushDestinationConfiguration : ConfigurationSection
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Type { get; set; } = "normal";
    public string VideoSource { get; set; } = string.Empty;
    public string AudioSource { get; set; } = "close";
    public string Stream { get; set; } = "main";
    public string Url { get; set; } = string.Empty;
    public string Compatibility { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    public IReadOnlyList<SelectionOption> VideoSources { get; init; } = [];
    public IReadOnlyList<SelectionOption> AudioSources { get; init; } = [];
    public IReadOnlyList<SelectionOption> Types { get; init; } = [];
    public IReadOnlyList<SelectionOption> Streams { get; } =
    [
        new("main", "Main stream"),
        new("sub", "Sub stream")
    ];
    public IReadOnlyList<SelectionOption> CompatibilityModes { get; } =
    [
        new(string.Empty, "Normal"),
        new("ext_header", "Enhanced RTMP")
    ];
}

public sealed class HardwareConfiguration
{
    public string Model { get; init; } = string.Empty;
    public string Chip { get; init; } = string.Empty;
    public bool HasLineAudio { get; init; }
    public bool HasUsbAudioInput { get; init; }
    public bool HasVideoOutput { get; init; }
    public AudioInputConfiguration UsbAudioInput { get; init; } = new();
    public AudioInputConfiguration LineAudioInput { get; init; } = new();
    public AudioOutputConfiguration LineAudioOutput { get; init; } = new();
    public IReadOnlyList<VideoOutputConfiguration> VideoOutputs { get; init; } = [];
    public Visibility UsbAudioVisibility => HasUsbAudioInput ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LineAudioVisibility => HasLineAudio ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VideoOutputVisibility => HasVideoOutput ? Visibility.Visible : Visibility.Collapsed;
    public bool HasHardwareControls => HasLineAudio || HasUsbAudioInput || HasVideoOutput;
}

public sealed class AudioInputConfiguration
{
    public string Name { get; set; } = string.Empty;
    public bool HasName { get; init; }
    public string Device { get; init; } = string.Empty;
    public string NoiseReduction { get; set; } = "0";
    public string NoiseReductionLevel { get; set; } = "8";
    public string Gain { get; set; } = "0";
    public bool Enabled { get; set; }
    public bool CanDisable { get; init; }
    public Visibility EnabledVisibility => CanDisable ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<SelectionOption> NoiseReductionModes { get; } =
    [
        new("0", "Disabled"),
        new("1", "Enabled")
    ];
    public IReadOnlyList<string> NoiseReductionLevels { get; } =
        Enumerable.Range(1, 16).Select(value => value.ToString()).ToArray();
    public IReadOnlyList<SelectionOption> Gains { get; } = AudioGainOptions.All;
}

public sealed class AudioOutputConfiguration
{
    public string Source { get; set; } = string.Empty;
    public bool SourceStoredAsString { get; init; }
    public string Gain { get; set; } = "0";
    public IReadOnlyList<SelectionOption> Sources { get; init; } = [];
    public IReadOnlyList<SelectionOption> Gains { get; } = AudioGainOptions.All;
}

public sealed class VideoOutputConfiguration
{
    public string ConfigurationKey { get; init; } = "output";
    public string Name { get; init; } = "HDMI output";
    public bool Enabled { get; set; }
    public string Type { get; set; } = "hdmi";
    public string Resolution { get; set; } = "1080P60";
    public string Rotate { get; set; } = "0";
    public bool Mirror { get; set; }
    public bool HasMirror { get; init; }
    public string Source { get; set; } = string.Empty;
    public bool LowLatency { get; set; }
    public string ColorMatrix { get; set; } = "identity";
    public string Luma { get; set; } = "50";
    public string Contrast { get; set; } = "50";
    public string Saturation { get; set; } = "50";
    public string Hue { get; set; } = "50";
    public bool ColorValuesStoredAsString { get; init; }
    public IReadOnlyList<SelectionOption> Sources { get; init; } = [];
    public IReadOnlyList<SelectionOption> Types { get; } =
    [
        new("hdmi", "HDMI"),
        new("dvi", "DVI")
    ];
    public IReadOnlyList<string> Resolutions { get; init; } = [];
    public IReadOnlyList<string> Rotations { get; } = ["0", "90", "180", "270"];
    public IReadOnlyList<SelectionOption> ColorMatrices { get; } =
    [
        new("identity", "Identity"),
        new("601_709", "BT.601 to BT.709"),
        new("709_601", "BT.709 to BT.601")
    ];
}

internal static class AudioGainOptions
{
    public static IReadOnlyList<SelectionOption> All { get; } =
    [
        new("24", "+24 dB"), new("18", "+18 dB"), new("12", "+12 dB"),
        new("6", "+6 dB"), new("0", "0 dB"), new("-6", "-6 dB"),
        new("-12", "-12 dB"), new("-18", "-18 dB"), new("-24", "-24 dB")
    ];
}
