using System.Windows;

namespace Linkpi_Monitor;

public partial class PushConfigWindow : Window
{
    private readonly PushConfiguration _configuration;
    private readonly LinkPiClient _client;

    public PushConfigWindow(
        PushConfiguration configuration,
        LinkPiClient client,
        PushDestinationConfiguration? selectedDestination = null)
    {
        InitializeComponent();
        _configuration = configuration;
        _client = client;
        DataContext = configuration;
        DestinationTabs.SelectedItem = selectedDestination ?? configuration.Destinations.FirstOrDefault();
    }

    private void AddDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = new PushDestinationConfiguration
        {
            Name = $"Push {_configuration.Destinations.Count + 1}",
            Types = _configuration.Types,
            VideoSources = _configuration.VideoSources,
            AudioSources = _configuration.AudioSources,
            VideoSource = _configuration.VideoSources.FirstOrDefault()?.Value ?? string.Empty
        };
        _configuration.Destinations.Add(destination);
        DestinationTabs.SelectedItem = destination;
    }

    private void RemoveDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationTabs.SelectedItem is PushDestinationConfiguration destination)
        {
            _configuration.Destinations.Remove(destination);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "Save the displayed Push configuration? This does not start or stop publishing.",
                "Save Push configuration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await _client.SavePushConfigurationAsync(_configuration);
            MessageBox.Show(this, "Push configuration was saved. Publishing state was not changed.",
                "Configuration saved", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not save the Push configuration.\n\n{exception.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
