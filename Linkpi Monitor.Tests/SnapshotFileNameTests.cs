using Linkpi_Monitor;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class SnapshotFileNameTests
{
    [Theory]
    [InlineData("frame", "frame.png")]
    [InlineData("frame.jpg", "frame.png")]
    [InlineData("frame.PNG", "frame.png")]
    public void EnforcesPngExtension(string filePath, string expected)
    {
        Assert.Equal(expected, SnapshotFileName.EnsurePngExtension(filePath));
    }

    [Fact]
    public void UsesChannelNameAndSortableTimestamp()
    {
        var result = SnapshotFileName.Create("HDMI 1", new DateTime(2026, 9, 4, 14, 5, 6));

        Assert.Equal("HDMI 1 2026-09-04 14-05-06.png", result);
    }

    [Fact]
    public void ReplacesInvalidFileNameCharacters()
    {
        var result = SnapshotFileName.Create("Network: Main/Backup", new DateTime(2026, 9, 4, 14, 5, 6));

        Assert.Equal("Network- Main-Backup 2026-09-04 14-05-06.png", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void UsesFallbackForEmptySanitizedChannelName(string channelName)
    {
        var result = SnapshotFileName.Create(channelName, new DateTime(2026, 9, 4, 14, 5, 6));

        Assert.Equal("Video 2026-09-04 14-05-06.png", result);
    }
}
