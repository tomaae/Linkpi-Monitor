using System.Windows;
using System.Windows.Controls;

namespace Linkpi_Monitor;

public partial class HardwareEditor : UserControl
{
    public HardwareEditor()
    {
        InitializeComponent();
    }

    public Func<HardwareConfiguration, Task>? SaveAsync { get; set; }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAsync is null || DataContext is not HardwareConfiguration configuration)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        if (MessageBox.Show(
                owner,
                "Apply the displayed physical audio and video-output configuration to the selected LinkPi?",
                "Save hardware configuration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await SaveAsync(configuration);
            MessageBox.Show(owner, "Hardware configuration was saved.", "Configuration saved",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, $"Could not save the hardware configuration.\n\n{exception.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
