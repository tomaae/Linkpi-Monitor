using System.Text;
using System.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Linkpi_Monitor;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Brush OnlineBrush = CreateBrush("#39D98A");
    private static readonly Brush OfflineBrush = CreateBrush("#77808C");
    private static readonly Brush BusyBrush = CreateBrush("#F5B82E");

    private readonly DispatcherTimer _refreshTimer = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AppSettings? _settings;
    private LinkPiClient? _client;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isLoaded;
    private bool _suppressDeviceSelection;
    private string _connectionStatus = "Not connected";
    private Brush _connectionBrush = OfflineBrush;
    private string _cpuDisplay = "—";
    private string _memoryDisplay = "—";
    private string _temperatureDisplay = "—";
    private string _errorMessage = string.Empty;
    private PushConfiguration? _pushConfiguration;
    private HardwareConfiguration? _hardware;
    private string _deviceModelDisplay = "No device selected";
    private bool _holdHardwareConfiguration;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer.Tick += RefreshTimer_Tick;
        HardwareConfigurationEditor.SaveAsync = SaveHardwareConfigurationAsync;
    }

    public ObservableCollection<DeviceSettings> Devices { get; } = [];
    public ObservableCollection<ChannelDisplay> Channels { get; } = [];
    public ObservableCollection<PushDisplay> PushDestinations { get; } = [];
    public HardwareConfiguration? Hardware
    {
        get => _hardware;
        private set
        {
            if (SetField(ref _hardware, value))
            {
                OnPropertyChanged(nameof(HardwareVisibility));
            }
        }
    }

    public Visibility HardwareVisibility => Hardware?.HasHardwareControls == true
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string DeviceModelDisplay
    {
        get => _deviceModelDisplay;
        private set => SetField(ref _deviceModelDisplay, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }

    public Brush ConnectionBrush
    {
        get => _connectionBrush;
        private set => SetField(ref _connectionBrush, value);
    }

    public string CpuDisplay
    {
        get => _cpuDisplay;
        private set => SetField(ref _cpuDisplay, value);
    }

    public string MemoryDisplay
    {
        get => _memoryDisplay;
        private set => SetField(ref _memoryDisplay, value);
    }

    public string TemperatureDisplay
    {
        get => _temperatureDisplay;
        private set => SetField(ref _temperatureDisplay, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(ErrorVisibility));
            }
        }
    }

    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorMessage)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await AppSettings.LoadAsync();
            foreach (var device in _settings.Devices)
            {
                Devices.Add(device);
            }

            _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
            _isLoaded = true;
            if (Devices.Count > 0)
            {
                _suppressDeviceSelection = true;
                DevicePicker.SelectedIndex = 0;
                _suppressDeviceSelection = false;
                UpdateDeviceActionState();
                await ActivateDeviceAsync(Devices[0]);
            }
            else
            {
                ShowNoDeviceState();
            }

            _refreshTimer.Start();
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
    }

    private async void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDeviceActionState();
        if (!_isLoaded || _suppressDeviceSelection)
        {
            return;
        }

        if (DevicePicker.SelectedItem is not DeviceSettings device)
        {
            ShowNoDeviceState();
            return;
        }

        await ActivateDeviceAsync(device);
    }

    private async Task ActivateDeviceAsync(DeviceSettings device)
    {
        _refreshCancellation?.Cancel();
        _client?.Dispose();
        _client = new LinkPiClient(device);
        Channels.Clear();
        PushDestinations.Clear();
        SetPushConfiguration(null);
        Hardware = null;
        DeviceModelDisplay = "Detecting model…";
        ConnectionStatus = "Connecting";
        ConnectionBrush = BusyBrush;
        ErrorMessage = string.Empty;
        await RefreshAsync();
    }

    private async void AddDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new DeviceEditorWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.Device is not { } device)
        {
            return;
        }

        if (ContainsBaseUrl(device.BaseUrl))
        {
            ShowDuplicateDeviceMessage(device.BaseUrl);
            return;
        }

        var updatedDevices = Devices.Append(device).ToArray();
        if (await SaveDevicesAsync(updatedDevices))
        {
            await ApplyDeviceListAsync(updatedDevices, updatedDevices.Length - 1);
        }
    }

    private async void EditDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = DevicePicker.SelectedIndex;
        if (selectedIndex < 0 || DevicePicker.SelectedItem is not DeviceSettings selectedDevice)
        {
            return;
        }

        var editor = new DeviceEditorWindow(selectedDevice) { Owner = this };
        if (editor.ShowDialog() != true || editor.Device is not { } device)
        {
            return;
        }

        if (ContainsBaseUrl(device.BaseUrl, selectedIndex))
        {
            ShowDuplicateDeviceMessage(device.BaseUrl);
            return;
        }

        var updatedDevices = Devices.ToArray();
        updatedDevices[selectedIndex] = device;
        if (await SaveDevicesAsync(updatedDevices))
        {
            await ApplyDeviceListAsync(updatedDevices, selectedIndex);
        }
    }

    private async void DeleteDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = DevicePicker.SelectedIndex;
        if (selectedIndex < 0 || DevicePicker.SelectedItem is not DeviceSettings selectedDevice)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove '{selectedDevice.Name}' from LinkPi Monitor?\n\nThis only changes the local config.json file.",
            "Delete device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var updatedDevices = Devices.Where((_, index) => index != selectedIndex).ToArray();
        if (await SaveDevicesAsync(updatedDevices))
        {
            var nextIndex = updatedDevices.Length == 0 ? -1 : Math.Min(selectedIndex, updatedDevices.Length - 1);
            await ApplyDeviceListAsync(updatedDevices, nextIndex);
        }
    }

    private bool ContainsBaseUrl(string baseUrl, int ignoredIndex = -1) =>
        Devices.Where((_, index) => index != ignoredIndex)
            .Any(device => device.BaseUrl.Equals(baseUrl, StringComparison.OrdinalIgnoreCase));

    private void ShowDuplicateDeviceMessage(string baseUrl) =>
        MessageBox.Show(
            this,
            $"A device with the base URL '{baseUrl}' already exists.",
            "Duplicate device",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private async Task<bool> SaveDevicesAsync(IReadOnlyList<DeviceSettings> devices)
    {
        if (_settings is null)
        {
            MessageBox.Show(
                this,
                "The local device configuration has not finished loading.",
                "Configuration unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        var updatedSettings = new AppSettings
        {
            Devices = [.. devices],
            RefreshIntervalSeconds = _settings.RefreshIntervalSeconds
        };

        try
        {
            await updatedSettings.SaveAsync();
            _settings = updatedSettings;
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Could not save the local device configuration.\n\n{exception.Message}",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private async Task ApplyDeviceListAsync(IReadOnlyList<DeviceSettings> devices, int selectedIndex)
    {
        _suppressDeviceSelection = true;
        Replace(Devices, devices);
        DevicePicker.SelectedIndex = selectedIndex;
        _suppressDeviceSelection = false;
        UpdateDeviceActionState();

        if (selectedIndex >= 0)
        {
            await ActivateDeviceAsync(Devices[selectedIndex]);
        }
        else
        {
            ShowNoDeviceState();
        }
    }

    private void UpdateDeviceActionState()
    {
        var hasSelection = DevicePicker.SelectedItem is DeviceSettings;
        EditDeviceButton.IsEnabled = hasSelection;
        DeleteDeviceButton.IsEnabled = hasSelection;
    }

    private void ShowNoDeviceState()
    {
        _refreshCancellation?.Cancel();
        _client?.Dispose();
        _client = null;
        Channels.Clear();
        PushDestinations.Clear();
        SetPushConfiguration(null);
        Hardware = null;
        DeviceModelDisplay = "No device selected";
        CpuDisplay = "—";
        MemoryDisplay = "—";
        TemperatureDisplay = "—";
        ConnectionStatus = "No device";
        ConnectionBrush = OfflineBrush;
        ErrorMessage = string.Empty;
        UpdateDeviceActionState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_client is null || !await _refreshGate.WaitAsync(0))
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();

        try
        {
            var snapshot = await _client.GetSnapshotAsync(_refreshCancellation.Token);

            Replace(Channels, snapshot.Channels);
            Replace(PushDestinations, snapshot.PushDestinations);
            SetPushConfiguration(snapshot.PushConfiguration);
            if (!_holdHardwareConfiguration)
            {
                Hardware = snapshot.Hardware;
            }
            DeviceModelDisplay = string.IsNullOrWhiteSpace(snapshot.Hardware.Model)
                ? DevicePicker.SelectedItem is DeviceSettings selectedDevice ? selectedDevice.Name : "Unknown LinkPi model"
                : snapshot.Hardware.Model;
            CpuDisplay = $"{snapshot.CpuPercent}%";
            MemoryDisplay = $"{snapshot.MemoryPercent}%";
            TemperatureDisplay = $"{snapshot.TemperatureCelsius} °C";
            ConnectionStatus = "Online";
            ConnectionBrush = OnlineBrush;
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // A device change or application close superseded this refresh.
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void ShowConnectionError(Exception exception)
    {
        ConnectionStatus = "Unavailable";
        ConnectionBrush = OfflineBrush;
        ErrorMessage = $"Could not refresh the selected LinkPi: {exception.Message}";
    }

    private void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChannelDisplay { WatchUri: not null } channel })
        {
            return;
        }

        new WatchWindow(channel) { Owner = this }.Show();
    }

    private async void ConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null && sender is Button { Tag: ChannelDisplay channel })
        {
            if (new StreamConfigWindow(channel, _client) { Owner = this }.ShowDialog() == true)
            {
                await RefreshAsync();
            }
        }
    }

    private async void PushDestinationConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pushConfiguration is not null && sender is Button { Tag: PushDisplay push })
        {
            if (_client is not null && new PushConfigWindow(_pushConfiguration, _client, push.Configuration) { Owner = this }.ShowDialog() == true)
            {
                await RefreshAsync();
            }
        }
    }

    private void SetPushConfiguration(PushConfiguration? configuration)
    {
        _pushConfiguration = configuration;
    }

    private async Task SaveHardwareConfigurationAsync(HardwareConfiguration configuration)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("No LinkPi device is selected.");
        }

        await _client.SaveHardwareConfigurationAsync(configuration);
        _holdHardwareConfiguration = false;
        await RefreshAsync();
        _holdHardwareConfiguration = MainTabs.SelectedItem is TabItem { Header: "Hardware" };
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MainTabs)
        {
            _holdHardwareConfiguration = MainTabs.SelectedItem is TabItem { Header: "Hardware" };
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _client?.Dispose();
        _refreshGate.Dispose();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
