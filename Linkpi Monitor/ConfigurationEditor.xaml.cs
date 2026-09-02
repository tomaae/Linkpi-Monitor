using System.Windows;
using System.Windows.Controls;

namespace Linkpi_Monitor;

public partial class ConfigurationEditor : UserControl
{
    public ConfigurationEditor()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Window.GetWindow(this)?.Close();
}
