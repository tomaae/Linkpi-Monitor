using System.Windows;

namespace Linkpi_Monitor;

public partial class PushConfigWindow : Window
{
    private readonly PushConfiguration _configuration;

    public PushConfigWindow(PushConfiguration configuration, PushDestinationConfiguration? selectedDestination = null)
    {
        InitializeComponent();
        _configuration = configuration;
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

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
