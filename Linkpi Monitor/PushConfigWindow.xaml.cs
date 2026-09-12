using System.Windows;
using System.ComponentModel;
using System.Text.Json;

namespace Linkpi_Monitor;

public partial class PushConfigWindow : Window
{
    private readonly PushConfiguration _configuration;
    private readonly LinkPiClient _client;
    private readonly string _baseline;
    private bool _isSaving;
    private bool _saved;

    public PushConfigWindow(
        PushConfiguration configuration,
        LinkPiClient client,
        PushDestinationConfiguration? selectedDestination = null)
    {
        InitializeComponent();
        _configuration = configuration.CreateEditableCopy();
        _client = client;
        _baseline = JsonSerializer.Serialize(_configuration);
        DataContext = _configuration;
        DestinationTabs.SelectedItem = selectedDestination is null
            ? _configuration.Destinations.FirstOrDefault()
            : _configuration.Destinations.FirstOrDefault(destination =>
                destination.OriginalIndex == selectedDestination.OriginalIndex);
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
        var validationErrors = ConfigurationValidator.Validate(_configuration);
        if (validationErrors.Count > 0)
        {
            MessageBox.Show(this, ConfigurationValidator.Format(validationErrors), "Check configuration",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
        AddButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        _isSaving = true;
        try
        {
            await _client.SavePushConfigurationAsync(_configuration);
            _saved = true;
            _isSaving = false;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not save the Push configuration.\n\n{exception.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isSaving = false;
            if (IsVisible)
            {
                SaveButton.IsEnabled = true;
                AddButton.IsEnabled = true;
                RemoveButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isSaving)
        {
            e.Cancel = true;
            return;
        }

        if (_saved || JsonSerializer.Serialize(_configuration) == _baseline)
        {
            return;
        }

        e.Cancel = MessageBox.Show(this, "Discard the unsaved Push configuration changes?", "Unsaved changes",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
    }
}
