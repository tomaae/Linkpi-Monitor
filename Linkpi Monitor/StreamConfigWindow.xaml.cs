using System.Windows;

namespace Linkpi_Monitor;

public partial class StreamConfigWindow : Window
{
    public StreamConfigWindow(ChannelDisplay channel, LinkPiClient client)
    {
        InitializeComponent();
        DataContext = channel;
        Title = $"{channel.Name} configuration";
        Editor.SaveAsync = configuration => client.SaveChannelConfigurationAsync(channel.Id, configuration);
        Editor.SaveButton.IsEnabled = client.CanSaveChanges;
        Editor.SaveButton.ToolTip = client.CanSaveChanges
            ? "Save this channel configuration to the selected LinkPi."
            : "Configuration changes are disabled for this device.";
    }
}
