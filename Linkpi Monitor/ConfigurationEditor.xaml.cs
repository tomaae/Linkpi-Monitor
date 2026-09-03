using System.Windows;
using System.Windows.Controls;

namespace Linkpi_Monitor;

public partial class ConfigurationEditor : UserControl
{
    public ConfigurationEditor()
    {
        InitializeComponent();
    }

    public Func<ChannelConfiguration, Task>? SaveAsync { get; set; }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAsync is null || DataContext is not ChannelDisplay channel)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        if (MessageBox.Show(
                owner,
                $"Apply the displayed configuration to channel {channel.Id} on the selected LinkPi?",
                "Save channel configuration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await SaveAsync(channel.Configuration);
            MessageBox.Show(owner, "Channel configuration was saved.", "Configuration saved",
                MessageBoxButton.OK, MessageBoxImage.Information);
            if (owner is not null)
            {
                owner.DialogResult = true;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, $"Could not save the channel configuration.\n\n{exception.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Window.GetWindow(this)?.Close();
}
