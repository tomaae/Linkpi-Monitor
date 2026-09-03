using System.Windows.Media;
using System.Collections.ObjectModel;

namespace Linkpi_Monitor;

public sealed class LinkPiSnapshot
{
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public int TemperatureCelsius { get; init; }
    public required IReadOnlyList<ChannelDisplay> Channels { get; init; }
    public required IReadOnlyList<PushDisplay> PushDestinations { get; init; }
    public required PushConfiguration PushConfiguration { get; init; }
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
