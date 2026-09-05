using System.Windows;

namespace Linkpi_Monitor;

public partial class StreamConfigWindow : Window
{
    public StreamConfigWindow(ChannelDisplay channel, LinkPiClient client)
    {
        InitializeComponent();
        DataContext = channel.CreateEditableCopy();
        Title = $"{channel.Name} configuration";
        Editor.SaveAsync = configuration => client.SaveChannelConfigurationAsync(channel.Id, configuration);
    }
}
