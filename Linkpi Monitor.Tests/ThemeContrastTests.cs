using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Linkpi_Monitor.Tests;

public sealed class ThemeContrastTests
{
    [Fact]
    public Task TextPalettesMeetNormalTextContrastRequirement() => WpfTestHost.RunAsync(() =>
    {
        AssertContrast("TextBrush", "WindowBrush", 4.5);
        AssertContrast("MutedTextBrush", "WindowBrush", 4.5);
        AssertContrast("PopupTextBrush", "PopupBackgroundBrush", 4.5);
        AssertContrast("PopupMutedTextBrush", "PopupBackgroundBrush", 4.5);
        AssertContrast("PopupTextBrush", "PopupHighlightBrush", 4.5);
        AssertContrast("TextBrush", "CardBrush", 4.5);
        return Task.CompletedTask;
    });

    private static void AssertContrast(string foregroundKey, string backgroundKey, double minimum)
    {
        var foreground = ((SolidColorBrush)Application.Current.FindResource(foregroundKey)).Color;
        var background = ((SolidColorBrush)Application.Current.FindResource(backgroundKey)).Color;
        var ratio = ContrastRatio(foreground, background);
        Assert.True(ratio >= minimum,
            $"{foregroundKey} on {backgroundKey} has a {ratio:0.00}:1 contrast ratio; expected at least {minimum:0.0}:1.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * Linearize(color.R / 255d)) +
        (0.7152 * Linearize(color.G / 255d)) +
        (0.0722 * Linearize(color.B / 255d));

    private static double Linearize(double component) =>
        component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
}
