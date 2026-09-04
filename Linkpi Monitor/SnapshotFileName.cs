using System.IO;

namespace Linkpi_Monitor;

internal static class SnapshotFileName
{
    public static string EnsurePngExtension(string filePath) => Path.ChangeExtension(filePath, ".png");

    public static string Create(string channelName, DateTime timestamp)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeChannelName = new string(channelName
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');

        if (string.IsNullOrWhiteSpace(safeChannelName))
        {
            safeChannelName = "Video";
        }

        return $"{safeChannelName} {timestamp:yyyy-MM-dd HH-mm-ss}.png";
    }
}
