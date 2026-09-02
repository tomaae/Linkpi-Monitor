using System.Windows;

namespace Linkpi_Monitor;

public partial class StreamConfigWindow : Window
{
    public StreamConfigWindow(ChannelDisplay channel)
    {
        InitializeComponent();
        DataContext = channel;
        Title = $"{channel.Name} configuration";
    }
}
