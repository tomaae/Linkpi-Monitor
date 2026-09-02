using System.Text;
using System.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private LinkPiClient? _client;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isLoaded;
    private string _connectionStatus = "Not connected";
    private Brush _connectionBrush = OfflineBrush;
    private string _cpuDisplay = "—";
    private string _memoryDisplay = "—";
    private string _temperatureDisplay = "—";
    private string _errorMessage = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    public ObservableCollection<DeviceSettings> Devices { get; } = [];
    public ObservableCollection<ChannelDisplay> Channels { get; } = [];
    public ObservableCollection<PushDisplay> PushDestinations { get; } = [];

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
            var settings = await AppSettings.LoadAsync();
            foreach (var device in settings.Devices)
            {
                Devices.Add(device);
            }

            _refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
            _isLoaded = true;
            DevicePicker.SelectedIndex = 0;
            _refreshTimer.Start();
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
    }

    private async void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || DevicePicker.SelectedItem is not DeviceSettings device)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _client?.Dispose();
        _client = new LinkPiClient(device);
        Channels.Clear();
        PushDestinations.Clear();
        ConnectionStatus = "Connecting";
        ConnectionBrush = BusyBrush;
        ErrorMessage = string.Empty;
        await RefreshAsync();
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

        try
        {
            Process.Start(new ProcessStartInfo(channel.WatchUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ErrorMessage = $"No application could open the stream. Install or associate a player such as VLC. {exception.Message}";
        }
    }

    private void ConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelDisplay channel })
        {
            new StreamConfigWindow(channel) { Owner = this }.ShowDialog();
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
