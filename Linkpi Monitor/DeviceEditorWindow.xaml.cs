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
    }

    public DeviceSettings? Device { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.TryNormalizeBaseUrl(
                BaseUrlTextBox.Text,
                out var baseUrl,
                out var validationMessage))
        {
            ShowValidation($"Base URL {validationMessage} Example: http://192.168.1.100");
            BaseUrlTextBox.Focus();
            return;
        }

        var parsedUri = new Uri(baseUrl, UriKind.Absolute);
        if (parsedUri.Scheme == Uri.UriSchemeHttp &&
            (!string.IsNullOrWhiteSpace(UsernameTextBox.Text) || !string.IsNullOrEmpty(PasswordInput.Password)))
        {
            var answer = MessageBox.Show(
                this,
                "This device uses HTTP, so its credentials and device data are sent without transport encryption. Continue only on a trusted network.",
                "Unencrypted connection",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        var name = NameTextBox.Text.Trim();
        Device = new DeviceSettings
        {
            Name = string.IsNullOrWhiteSpace(name) ? parsedUri.Host : name,
            BaseUrl = baseUrl,
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordInput.Password
        };
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationBorder.Visibility = Visibility.Visible;
    }
}
