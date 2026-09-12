using System.Windows;
using System.ComponentModel;
using System.Text.Json;

namespace Linkpi_Monitor;

public partial class StreamConfigWindow : Window
{
    private readonly string _baseline;

    public StreamConfigWindow(ChannelDisplay channel, LinkPiClient client)
    {
        InitializeComponent();
        DataContext = channel.CreateEditableCopy();
        _baseline = JsonSerializer.Serialize(DataContext);
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

        if (DialogResult == true || JsonSerializer.Serialize(DataContext) == _baseline)
        {
            return;
        }

        e.Cancel = MessageBox.Show(this, "Discard the unsaved channel configuration changes?", "Unsaved changes",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
    }
}
