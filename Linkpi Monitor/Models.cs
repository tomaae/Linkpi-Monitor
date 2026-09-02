using System.Windows.Media;

namespace Linkpi_Monitor;

public sealed class LinkPiSnapshot
{
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public int TemperatureCelsius { get; init; }
    public required IReadOnlyList<ChannelDisplay> Channels { get; init; }
    public required IReadOnlyList<PushDisplay> PushDestinations { get; init; }
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
}
