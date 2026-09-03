using System.Windows;

namespace Linkpi_Monitor;

public partial class DeviceEditorWindow : Window
{
    public DeviceEditorWindow(DeviceSettings? existingDevice = null)
    {
        InitializeComponent();

        if (existingDevice is null)
        {
            BaseUrlTextBox.Text = "http://";
            UsernameTextBox.Text = "admin";
            return;
        }

        Title = "Edit device";
        HeadingText.Text = "Edit device";
        NameTextBox.Text = existingDevice.Name;
        BaseUrlTextBox.Text = existingDevice.BaseUrl;
        UsernameTextBox.Text = existingDevice.Username;
        PasswordInput.Password = existingDevice.Password;
        AllowChangesCheckBox.IsChecked = existingDevice.AllowChanges && !existingDevice.IsProtectedReadOnly;
        AllowChangesCheckBox.IsEnabled = !existingDevice.IsProtectedReadOnly;
    }

    public DeviceSettings? Device { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlTextBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUri) ||
            string.IsNullOrWhiteSpace(parsedUri.Host) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            ShowValidation("Enter an absolute HTTP or HTTPS URL, for example http://192.168.1.100.");
            BaseUrlTextBox.Focus();
            return;
        }

        var name = NameTextBox.Text.Trim();
        var isProtectedHost = parsedUri.Host.Equals(DeviceSettings.ProtectedReadOnlyHost, StringComparison.OrdinalIgnoreCase);
        Device = new DeviceSettings
        {
            Name = string.IsNullOrWhiteSpace(name) ? parsedUri.Host : name,
            BaseUrl = baseUrl,
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordInput.Password,
            AllowChanges = !isProtectedHost && AllowChangesCheckBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationBorder.Visibility = Visibility.Visible;
    }
}
