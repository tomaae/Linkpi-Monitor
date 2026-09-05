using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Linkpi_Monitor;

public partial class WatchWindow : Window
{
    private static readonly Brush OnlineBrush = CreateBrush("#39D98A");
    private static readonly Brush WarningBrush = CreateBrush("#F5B82E");
    private static readonly Brush OfflineBrush = CreateBrush("#FF5C5C");
    private static readonly Brush AccentBrush = CreateBrush("#F5B82E");
    private static readonly Brush AccentTextBrush = CreateBrush("#1C1608");
    private const double HeaderHeight = 64;
    private const double PreferredVideoHeight = 596;
    private const double PreferredVideoWidth = 1060;
    private static readonly TimeSpan SnapshotConfirmationDuration = TimeSpan.FromSeconds(2);

    private readonly string _channelName;
    private readonly Uri _streamUri;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _snapshotConfirmationTimer;
    private readonly CancellationTokenSource _snapshotCancellation = new();
    private Task _snapshotOperation = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _snapshotBusy;
    private bool _closeReady;
    private readonly bool _hasConfiguredAspectRatio;
    private readonly string? _vlcAspectRatio;
    private Media? _media;
    private volatile bool _hasVideoOutput;
    private volatile bool _isClosing;
    private bool _isMuted;
    private double _videoAspectRatio;

    public WatchWindow(ChannelDisplay channel)
    {
        InitializeComponent();
        if (channel.WatchUri is null)
        {
            throw new ArgumentException("The selected channel does not advertise a watchable stream.", nameof(channel));
        }

        _channelName = channel.Name;
        _streamUri = channel.WatchUri;
        var geometry = VideoGeometry.FromChannel(channel);
        _videoAspectRatio = geometry.AspectRatio;
        _hasConfiguredAspectRatio = geometry.IsKnown;
        _vlcAspectRatio = geometry.VlcAspectRatio;
        SizeWindowForAspectRatio();
        Title = $"{channel.Name} — LinkPi Monitor";
        ChannelNameText.Text = channel.Name;
        StreamAddressText.Text = $"{_streamUri.Scheme.ToUpperInvariant()} stream · {_streamUri.Host}";

        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--rtsp-tcp", "--network-caching=300");
        _mediaPlayer = new VlcMediaPlayer(_libVlc) { Volume = 75 };
        VideoView.MediaPlayer = _mediaPlayer;
        _snapshotConfirmationTimer = new DispatcherTimer { Interval = SnapshotConfirmationDuration };
        _snapshotConfirmationTimer.Tick += SnapshotConfirmationTimer_Tick;

        _mediaPlayer.Opening += (_, _) => SetPlaybackStatus("Opening stream", WarningBrush);
        _mediaPlayer.Buffering += (_, args) => SetPlaybackStatus($"Buffering {args.Cache:0}%", WarningBrush);
        _mediaPlayer.Playing += (_, _) => SetPlaybackStatus("Playing", OnlineBrush);
        _mediaPlayer.Vout += (_, args) =>
        {
            _hasVideoOutput = args.Count > 0;
            ApplyConfiguredAspectRatio();
            RefreshAspectRatioFromPlayer();
        };
        _mediaPlayer.Paused += (_, _) => SetPlaybackStatus("Paused", WarningBrush);
        _mediaPlayer.Stopped += (_, _) => SetPlaybackStatus("Stopped", WarningBrush);
        _mediaPlayer.EncounteredError += (_, _) => SetPlaybackStatus("Playback failed", OfflineBrush);
        _mediaPlayer.EndReached += (_, _) => SetPlaybackStatus("Stream ended", WarningBrush);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ResizeVideoToAspectRatio();
        StartPlayback();
    }

    private void StartPlayback()
    {
        _mediaPlayer.Stop();
        _media?.Dispose();
        _media = new Media(_libVlc, _streamUri);
        _media.AddOption(":rtsp-tcp");
        _media.AddOption(":network-caching=300");
        _mediaPlayer.Play(_media);
        ApplyConfiguredAspectRatio();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        _mediaPlayer.Mute = _isMuted;
        MuteButton.Content = _isMuted ? "Unmute" : "Mute";
        MuteButton.ToolTip = _isMuted ? "Restore audio for this live view" : "Mute this live view";

        if (_isMuted)
        {
            MuteButton.Background = AccentBrush;
            MuteButton.BorderBrush = AccentBrush;
            MuteButton.Foreground = AccentTextBrush;
        }
        else
        {
            MuteButton.ClearValue(BackgroundProperty);
            MuteButton.ClearValue(BorderBrushProperty);
            MuteButton.ClearValue(ForegroundProperty);
        }
    }

    private void VideoOverlay_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        CopyImageMenuItem.IsEnabled = _hasVideoOutput && !_isClosing && !_snapshotBusy;
        SaveImageMenuItem.IsEnabled = CopyImageMenuItem.IsEnabled;
    }

    private async void CopyImageMenuItem_Click(object sender, RoutedEventArgs e) => await StartSnapshotAsync(saveToFile: false);

    private async void SaveImageMenuItem_Click(object sender, RoutedEventArgs e) => await StartSnapshotAsync(saveToFile: true);

    private Task StartSnapshotAsync(bool saveToFile)
    {
        if (_snapshotBusy || _isClosing || !_hasVideoOutput) return Task.CompletedTask;
        _snapshotBusy = true;
        return _snapshotOperation = ProcessSnapshotAsync(saveToFile);
    }

    private async Task ProcessSnapshotAsync(bool saveToFile)
    {
        try
        {
            var frame = await FrameSnapshot.CaptureAsync(path => _mediaPlayer.TakeSnapshot(0, path, 0, 0),
                _snapshotCancellation.Token);
            _snapshotCancellation.Token.ThrowIfCancellationRequested();
            if (saveToFile)
            {
                var filePath = ChooseSnapshotPath();
                if (filePath is null) return;
                await frame.SaveAsync(filePath, _snapshotCancellation.Token);
                if (!_isClosing) ShowSnapshotConfirmation("Frame saved");
            }
            else
            {
                Clipboard.SetImage(frame.Image);
                ShowSnapshotConfirmation("Frame copied");
            }
        }
        catch (OperationCanceledException) when (_snapshotCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_isClosing) ShowSnapshotError(saveToFile ? "The frame could not be saved." : "The frame could not be copied.", exception);
        }
        finally { _snapshotBusy = false; }
    }

    private string? ChooseSnapshotPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save video frame",
            FileName = SnapshotFileName.Create(_channelName, DateTime.Now),
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        var filePath = SnapshotFileName.EnsurePngExtension(dialog.FileName);
        if (!filePath.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(filePath) &&
            MessageBox.Show(this, $"{Path.GetFileName(filePath)} already exists. Replace it?", "Confirm save",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return null;
        }
        return filePath;
    }

    private void ShowSnapshotConfirmation(string message)
    {
        SnapshotConfirmationText.Text = message;
        SnapshotConfirmation.Visibility = Visibility.Visible;
        _snapshotConfirmationTimer.Stop();
        _snapshotConfirmationTimer.Start();
    }

    private void SnapshotConfirmationTimer_Tick(object? sender, EventArgs e)
    {
        _snapshotConfirmationTimer.Stop();
        SnapshotConfirmation.Visibility = Visibility.Collapsed;
    }

    private void ShowSnapshotError(string message, Exception exception) =>
        MessageBox.Show(this, $"{message}\n\n{exception.Message}", "Snapshot failed",
            MessageBoxButton.OK, MessageBoxImage.Error);

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeVideoToAspectRatio();

    private void ApplyConfiguredAspectRatio()
    {
        if (!_isClosing && _vlcAspectRatio is not null)
        {
            _mediaPlayer.AspectRatio = _vlcAspectRatio;
        }
    }

    private void SizeWindowForAspectRatio()
    {
        var workArea = SystemParameters.WorkArea;
        var maximumWidth = Math.Min(PreferredVideoWidth, workArea.Width * 0.9);
        var maximumVideoHeight = Math.Min(PreferredVideoHeight, (workArea.Height * 0.9) - HeaderHeight);

        var videoWidth = maximumVideoHeight * _videoAspectRatio;
        var videoHeight = maximumVideoHeight;
        if (videoWidth > maximumWidth)
        {
            videoWidth = maximumWidth;
            videoHeight = videoWidth / _videoAspectRatio;
        }

        Width = Math.Max(MinWidth, videoWidth);
        Height = Math.Max(MinHeight, videoHeight + HeaderHeight);
    }

    private void ResizeVideoToAspectRatio()
    {
        if (VideoHost.ActualWidth <= 0 || VideoHost.ActualHeight <= 0 || _videoAspectRatio <= 0)
        {
            return;
        }

        var width = VideoHost.ActualWidth;
        var height = width / _videoAspectRatio;
        if (height > VideoHost.ActualHeight)
        {
            height = VideoHost.ActualHeight;
            width = height * _videoAspectRatio;
        }

        VideoView.Width = Math.Max(1, width);
        VideoView.Height = Math.Max(1, height);
    }

    private void RefreshAspectRatioFromPlayer()
    {
        uint width = 0;
        uint height = 0;
        if (_hasConfiguredAspectRatio || _isClosing || !_mediaPlayer.Size(0, ref width, ref height) || height == 0)
        {
            return;
        }

        var detectedAspectRatio = (double)width / height;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || detectedAspectRatio <= 0)
            {
                return;
            }

            _videoAspectRatio = detectedAspectRatio;
            SizeWindowForAspectRatio();
            ResizeVideoToAspectRatio();
        });
    }

    private void SetPlaybackStatus(string text, Brush brush)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            PlaybackStatusText.Text = text;
            PlaybackStatusDot.Fill = brush;
        });
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        await PrepareToCloseAsync();
        _ = Dispatcher.BeginInvoke(Close);
    }

    internal Task PrepareToCloseAsync() => _shutdownTask ??= ShutdownAsync();

    private async Task ShutdownAsync()
    {
        _isClosing = true;
        IsEnabled = false;
        _snapshotConfirmationTimer.Stop();
        _snapshotCancellation.Cancel();
        await _snapshotOperation;
        VideoView.MediaPlayer = null;
        await Task.Run(() =>
        {
            _mediaPlayer.Stop();
            _media?.Dispose();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
        });
        VideoView.Dispose();
        _snapshotCancellation.Dispose();
        _closeReady = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        var owner = Owner;
        if (owner is null || !owner.IsVisible)
        {
            return;
        }

        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (owner.IsVisible)
            {
                owner.Activate();
                owner.Focus();
            }
        });
    }

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
