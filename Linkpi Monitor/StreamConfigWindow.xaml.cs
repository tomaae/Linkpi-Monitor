using System.Windows;
using System.ComponentModel;
using System.Text.Json;

namespace Linkpi_Monitor;

public partial class StreamConfigWindow : Window
{
    private readonly ChannelConfiguration _configuration;
    private readonly string _baseline;

    public StreamConfigWindow(ChannelDisplay channel, LinkPiClient client)
    {
        InitializeComponent();
        var editableChannel = channel.CreateEditableCopy();
        DataContext = editableChannel;
        _configuration = editableChannel.Configuration;
        _baseline = JsonSerializer.Serialize(_configuration);
        Title = $"{channel.Name} configuration";
        Editor.SaveAsync = configuration => client.SaveChannelConfigurationAsync(channel.Id, configuration);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Editor.IsSaving)
        {
            e.Cancel = true;
            return;
        }

        if (DialogResult == true || JsonSerializer.Serialize(_configuration) == _baseline)
        {
            return;
        }

        e.Cancel = MessageBox.Show(this, "Discard the unsaved channel configuration changes?", "Unsaved changes",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
    }
}
