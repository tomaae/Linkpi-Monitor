using System.Text;
using System.Diagnostics;
using System.IO;
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
    private Task _activeRefresh = Task.CompletedTask;
    private Task _activeSave = Task.CompletedTask;
    private bool _isClosing;
    private bool _closeReady;
    private AppSettings? _settings;
    private LinkPiClient? _client;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isLoaded;
    private bool _suppressDeviceSelection;
    private bool _suppressTabSelection;
    private DeviceSettings? _activeDevice;
    private TabItem? _selectedMainTab;
    private string _connectionStatus = "Not connected";
    private Brush _connectionBrush = OfflineBrush;
    private string _cpuDisplay = "—";
    private string _memoryDisplay = "—";
    private string _temperatureDisplay = "—";
    private string _errorMessage = string.Empty;
    private PushConfiguration? _pushConfiguration;
    private HardwareConfiguration? _hardware;
    private string _deviceModelDisplay = "No device selected";
    private string _lastUpdatedDisplay = "Never updated";
    private bool _holdHardwareConfiguration;
    private bool _configurationRecoveryAvailable;

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

    public string LastUpdatedDisplay
    {
        get => _lastUpdatedDisplay;
        private set => SetField(ref _lastUpdatedDisplay, value);
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

    public Visibility ConfigurationRecoveryVisibility => _configurationRecoveryAvailable
        ? Visibility.Visible
        : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            try
            {
                _settings = await AppSettings.LoadAsync();
                if (_isClosing) return;
                if (!string.IsNullOrWhiteSpace(_settings.ConfigurationNotice))
                {
                    MessageBox.Show(
                        this,
                        _settings.ConfigurationNotice,
                        "Configuration moved",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception exception)
            {
                ShowConfigurationError(exception);
                return;
            }

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

            if (!_isClosing) _refreshTimer.Start();
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

        if (!HardwareConfigurationEditor.ConfirmDiscardChanges(this))
        {
            _suppressDeviceSelection = true;
            DevicePicker.SelectedItem = _activeDevice;
            _suppressDeviceSelection = false;
            UpdateDeviceActionState();
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
        await _activeRefresh;
        _client?.Dispose();
        _client = new LinkPiClient(device);
        _activeDevice = device;
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
        if (!HardwareConfigurationEditor.ConfirmDiscardChanges(this)) return;
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
        if (!HardwareConfigurationEditor.ConfirmDiscardChanges(this)) return;
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
        if (!HardwareConfigurationEditor.ConfirmDiscardChanges(this)) return;
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
        _activeDevice = null;
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

    private Task RefreshAsync()
    {
        if (_isClosing || _client is null || !_activeRefresh.IsCompleted)
        {
            return Task.CompletedTask;
        }

        return _activeRefresh = RefreshCoreAsync(_client);
    }

    private async Task RefreshCoreAsync(LinkPiClient client)
    {
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        var cancellationToken = cancellation.Token;

        try
        {
            RefreshButton.IsEnabled = false;
            RefreshButton.Content = "Refreshing…";
            var includePreviews = IsVisible && WindowState != WindowState.Minimized && MainTabs.SelectedItem == SourcesTab;
            var snapshot = await client.GetSnapshotAsync(cancellationToken, includePreviews);
            cancellationToken.ThrowIfCancellationRequested();

            CollectionReconciler.Update(Channels, snapshot.Channels, channel => channel.Id,
                (current, incoming) => current.UpdateFrom(incoming, includePreviews));
            CollectionReconciler.Update(PushDestinations, snapshot.PushDestinations, push => push.Index,
                (current, incoming) => current.UpdateFrom(incoming));
            SetPushConfiguration(snapshot.PushConfiguration);
            if (!_holdHardwareConfiguration)
            {
                Hardware = snapshot.Hardware;
            }
            DeviceModelDisplay = string.IsNullOrWhiteSpace(snapshot.Hardware.Model)
                ? DevicePicker.SelectedItem is DeviceSettings selectedDevice ? selectedDevice.Name : "Unknown LinkPi model"
                : snapshot.Hardware.Model;
            CpuDisplay = snapshot.HasSystemMetrics ? $"{snapshot.CpuPercent}%" : "—";
            MemoryDisplay = snapshot.HasSystemMetrics ? $"{snapshot.MemoryPercent}%" : "—";
            TemperatureDisplay = snapshot.HasSystemMetrics ? $"{snapshot.TemperatureCelsius} °C" : "—";
            ConnectionStatus = snapshot.Warnings.Count == 0 ? "Online" : "Online · limited";
            ConnectionBrush = snapshot.Warnings.Count == 0 ? OnlineBrush : BusyBrush;
            ErrorMessage = snapshot.Warnings.Count == 0
                ? string.Empty
                : "Some device information is temporarily unavailable.\n" + string.Join("\n", snapshot.Warnings);
            LastUpdatedDisplay = $"Updated {DateTime.Now:t}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A device change or application close superseded this refresh.
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
        finally
        {
            if (!_isClosing)
            {
                RefreshButton.IsEnabled = true;
                RefreshButton.Content = "Refresh";
            }
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
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
        CpuDisplay = "—";
        MemoryDisplay = "—";
        TemperatureDisplay = "—";
        ErrorMessage = $"Could not refresh the selected LinkPi: {exception.Message}";
    }

    private void ShowConfigurationError(Exception exception)
    {
        _configurationRecoveryAvailable = true;
        OnPropertyChanged(nameof(ConfigurationRecoveryVisibility));
        DeviceModelDisplay = "Configuration unavailable";
        ConnectionStatus = "Configuration error";
        ConnectionBrush = OfflineBrush;
        ErrorMessage = $"Could not load {AppSettings.ConfigPath}: {exception.Message}";
    }

    private void OpenConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = System.IO.Path.GetDirectoryName(AppSettings.ConfigPath);
        if (string.IsNullOrEmpty(directory)) return;

        Directory.CreateDirectory(directory);
        var arguments = File.Exists(AppSettings.ConfigPath)
            ? $"/select,\"{AppSettings.ConfigPath}\""
            : $"\"{directory}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    private async void ResetConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Back up the invalid configuration and start with an empty device list?",
            "Reset configuration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            string? backupPath = null;
            if (File.Exists(AppSettings.ConfigPath))
            {
                backupPath = $"{AppSettings.ConfigPath}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Move(AppSettings.ConfigPath, backupPath);
            }

            _settings = new AppSettings { Devices = [], RefreshIntervalSeconds = 5 };
            await _settings.SaveAsync();
            _configurationRecoveryAvailable = false;
            OnPropertyChanged(nameof(ConfigurationRecoveryVisibility));
            _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
            _isLoaded = true;
            ShowNoDeviceState();
            _refreshTimer.Start();
            ErrorMessage = backupPath is null
                ? "A new empty configuration was created. Add a device to get started."
                : $"A new empty configuration was created. The invalid file was backed up to {backupPath}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Could not reset the configuration.\n\n{exception.Message}",
                "Reset failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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

    private Task SaveHardwareConfigurationAsync(HardwareConfiguration configuration)
    {
        if (!_activeSave.IsCompleted)
        {
            throw new InvalidOperationException("A configuration save is already in progress.");
        }

        return _activeSave = SaveHardwareConfigurationCoreAsync(configuration);
    }

    private async Task SaveHardwareConfigurationCoreAsync(HardwareConfiguration configuration)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("No LinkPi device is selected.");
        }

        await _client.SaveHardwareConfigurationAsync(configuration);
        _holdHardwareConfiguration = false;
        await RefreshAsync();
        _holdHardwareConfiguration = ReferenceEquals(MainTabs.SelectedItem, HardwareTab);
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MainTabs)
        {
            var selectedTab = MainTabs.SelectedItem as TabItem;
            if (!_suppressTabSelection && ReferenceEquals(_selectedMainTab, HardwareTab) &&
                !ReferenceEquals(selectedTab, HardwareTab) &&
                !HardwareConfigurationEditor.ConfirmDiscardChanges(this))
            {
                _suppressTabSelection = true;
                MainTabs.SelectedItem = HardwareTab;
                _suppressTabSelection = false;
                return;
            }

            _selectedMainTab = selectedTab;
            _holdHardwareConfiguration = ReferenceEquals(selectedTab, HardwareTab);
            await RefreshForPreviewActivityAsync();
        }
    }

    private async void Window_StateChanged(object? sender, EventArgs e) => await RefreshForPreviewActivityAsync();

    private async void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        await RefreshForPreviewActivityAsync();

    private async Task RefreshForPreviewActivityAsync()
    {
        if (!_isLoaded || _isClosing) return;
        _refreshCancellation?.Cancel();
        await _activeRefresh;
        await RefreshAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_isClosing) return;
        if (!HardwareConfigurationEditor.ConfirmDiscardChanges(this)) return;
        _isClosing = true;
        IsEnabled = false;
        _refreshTimer.Stop();
        _refreshCancellation?.Cancel();
        await _activeRefresh;
        await _activeSave;
        // Owned windows do not receive Closing when WPF closes their owner.
        await Task.WhenAll(OwnedWindows.OfType<WatchWindow>().Select(window => window.PrepareToCloseAsync()));
        _client?.Dispose();
        _client = null;
        _closeReady = true;
        // A synchronous refresh must not cause a recursive Close inside WPF's Closing event.
        _ = Dispatcher.BeginInvoke(Close);
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
